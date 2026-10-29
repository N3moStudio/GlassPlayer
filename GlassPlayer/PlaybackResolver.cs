using System.Globalization;
using System.Text;

namespace GlassPlayer;

sealed record PlaybackResolution(string Location, string Source, Track AudioTrack)
{
    public bool IsMatchFor(Track original) => Source != original.Source;
}

// Keep the playlist's metadata separate from the recording used for playback.
// Matches stay in memory; provider URLs and stream credentials are never saved.
sealed class PlaybackResolver(IReadOnlyDictionary<string, IProvider> providers)
{
    readonly Dictionary<(string, string, string, string, int), Track> matches = new();

    public async Task<PlaybackResolution> ResolveAsync(Track original, bool allowMatches, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var key = (original.Source, original.Id, original.Title, original.Artist, original.DurationMs);
        Track? match = null;
        if (allowMatches) lock (matches) matches.TryGetValue(key, out match);
        // A successful fallback avoids retrying the same protected source on replay.
        if (original.Source != "Spotify" && match == null)
        {
            try
            {
                var location = await providers[original.Source].ResolveAsync(original).WaitAsync(cancellation);
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

        var youtube = providers["YouTube Music"];
        if (match == null)
        {
            if (original.DurationMs <= 0 || string.IsNullOrWhiteSpace(original.Artist))
                throw NoMatch();
            var candidates = await youtube.SearchAsync($"{original.Artist} {original.Title}").WaitAsync(cancellation);
            match = FindMatch(original, candidates);
            if (match == null) throw NoMatch();
        }

        try
        {
            var location = await youtube.ResolveAsync(match).WaitAsync(cancellation);
            if (string.IsNullOrWhiteSpace(location)) throw new InvalidOperationException("The matched YouTube recording is unavailable. Use Find on YouTube to choose another recording.");
            cancellation.ThrowIfCancellationRequested();
            lock (matches) matches[key] = match;
            return new(location, youtube.Name, match);
        }
        catch
        {
            lock (matches) matches.Remove(key);
            throw;
        }
    }

    static InvalidOperationException NoMatch() => new("No single close YouTube match. Use Find on YouTube to choose the recording; your playlist is unchanged.");

    internal static Track? FindMatch(Track original, IEnumerable<Track> candidates)
    {
        var title = Normalize(original.Title);
        var artist = Normalize(original.Artist);
        if (title.Length == 0 || artist.Length == 0 || original.DurationMs <= 0) return null;
        double tolerance = Math.Min(2500, Math.Max(1000, original.DurationMs * .015));
        // Version words (live, remix, clean, slowed, etc.) remain part of the title.
        // Different IDs with equally close metadata require the user's choice.
        var close = candidates.Where(t => t.Source == "YouTube Music" && !string.IsNullOrWhiteSpace(t.Id)
            && Normalize(t.Title) == title && Normalize(t.Artist) == artist && t.DurationMs > 0
            && Math.Abs((long)t.DurationMs - original.DurationMs) <= tolerance)
            .DistinctBy(t => t.Id).Take(2).ToArray();
        return close.Length == 1 ? close[0] : null;
    }

    static string Normalize(string value)
    {
        var text = new StringBuilder();
        foreach (var rune in value.Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune)) text.Append(Rune.ToUpperInvariant(rune));
            else if (Rune.IsWhiteSpace(rune)) text.Append(' ');
            else if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation)
                text.Append(' ');
        }
        return string.Join(' ', text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
