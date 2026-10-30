using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GlassPlayer;

sealed record PlaybackResolution(string Location, string Source, Track AudioTrack)
{
    public bool IsMatchFor(Track original) => Source != original.Source;
}

// Keep the playlist's metadata separate from the recording used for playback.
// Matches stay in memory; provider URLs and stream credentials are never saved.
sealed class PlaybackResolver(IReadOnlyDictionary<string, IProvider> providers, OfflineLibrary? offline = null)
{
    readonly RecentCache<(string, string, string, string, int), Track> matches = new(64, TimeSpan.FromHours(2));

    public async Task<PlaybackResolution> ResolveAsync(Track original, bool allowMatches, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (original.Source == "Local")
        {
            var location = await providers["Local"].ResolveAsync(original, cancellation);
            if (string.IsNullOrWhiteSpace(location)) throw new InvalidOperationException("This local audio file is unavailable.");
            return new(location, original.DownloadedFrom is { } source ? "Downloaded · " + source : "Local", original);
        }
        if (offline?.Find(original) is { } downloaded) return downloaded;
        // Older imports stored the 30-second excerpt length. Recover the full
        // recording's metadata before matching; never promote a preview to audio.
        if (original.Source == "SoundCloud" && providers.GetValueOrDefault("SoundCloud") is SoundCloud soundcloud
            && original.DurationMs is >= 29000 and <= 31000)
            original = await soundcloud.RefreshPreviewAsync(original, cancellation);
        var key = (original.Source, original.Id, original.Title, original.Artist, original.DurationMs);
        Track? match = null;
        if (allowMatches) matches.TryGet(key, out match);
        // A successful fallback avoids retrying the same protected source on replay.
        if (original.Source != "Spotify" && !original.IsPreview && match == null)
        {
            try
            {
                var location = await providers[original.Source].ResolveAsync(original, cancellation);
                if (!string.IsNullOrWhiteSpace(location)) return new(location, original.Source, original);
                throw new InvalidOperationException("This track can't be streamed from its source.");
            }
            catch (Exception ex) when (allowMatches && original.Source == "SoundCloud" && ex is not OperationCanceledException)
            {
                cancellation.ThrowIfCancellationRequested();
            }
        }
        else if (original.Source == "Spotify" && !allowMatches)
            throw new InvalidOperationException("Enable matching YouTube audio in Settings, or choose a track on YouTube Music.");
        else if (original.IsPreview && !allowMatches)
            throw new InvalidOperationException("SoundCloud only provides a preview. Enable matching YouTube audio to look for the full recording.");

        var youtube = providers["YouTube Music"];
        if (match == null)
        {
            if (original.DurationMs <= 0 || string.IsNullOrWhiteSpace(original.Artist))
                throw NoMatch();
            var candidates = await youtube.SearchAsync($"{original.Artist} {original.Title}", cancellation);
            match = FindMatch(original, candidates);
            if (match == null) throw NoMatch();
        }

        try
        {
            var location = await youtube.ResolveAsync(match, cancellation);
            if (string.IsNullOrWhiteSpace(location)) throw new InvalidOperationException("The matched YouTube recording is unavailable. Use Find on YouTube to choose another recording.");
            cancellation.ThrowIfCancellationRequested();
            matches.Set(key, match);
            return new(location, youtube.Name, match);
        }
        catch
        {
            matches.Remove(key);
            throw;
        }
    }

    static InvalidOperationException NoMatch() => new("No single close YouTube match. Use Find on YouTube to choose the recording; your playlist is unchanged.");

    internal static Track? FindMatch(Track original, IEnumerable<Track> candidates)
    {
        var title = MatchTitle(original.Title);
        var artist = MatchArtists(original.Artist);
        if (title.Length == 0 || artist.Length == 0 || original.DurationMs <= 0) return null;
        double tolerance = Math.Min(2500, Math.Max(1000, original.DurationMs * .015));
        // Version words (live, remix, clean, slowed, etc.) remain part of the title.
        // Catalog copies with identical metadata and duration can use the first
        // search result. Conflicting lengths or ordinary uploads stay ambiguous.
        var close = candidates.Where(t => t.Source == "YouTube Music" && !string.IsNullOrWhiteSpace(t.Id)
            && MatchTitle(t.Title) == title && MatchArtists(t.Artist) == artist && t.DurationMs > 0
            && Math.Abs((long)t.DurationMs - original.DurationMs) <= tolerance)
            .DistinctBy(t => t.Id).ToArray();
        if (close.Length == 1) return close[0];
        return close.Length > 1 && close.All(t => t.CatalogRecording && t.DurationMs == close[0].DurationMs) ? close[0] : null;
    }

    static string MatchTitle(string value)
    {
        // Catalogs sometimes prefix the romanized title with its original script.
        // Only remove a non-Latin prefix; never strip English remix/version labels.
        var parts = Regex.Split(value, @"\s+[-–—]\s+", RegexOptions.CultureInvariant);
        if (parts.Length == 2 && parts[0].Any(c => c > 127 && char.IsLetter(c))
            && !parts[0].Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')) value = parts[1];
        return Normalize(value);
    }

    static string MatchArtists(string value) => string.Join(" | ", value.Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(artist => string.Join(' ', Normalize(artist).Split(' ', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal)))
        .Distinct().Order(StringComparer.Ordinal));

    static string Normalize(string value)
    {
        var text = new StringBuilder();
        foreach (var rune in value.Normalize(NormalizationForm.FormKD).EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune)) text.Append(Rune.ToUpperInvariant(rune));
            else if (Rune.IsWhiteSpace(rune)) text.Append(' ');
            else if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation)
                text.Append(' ');
        }
        return string.Join(' ', text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
