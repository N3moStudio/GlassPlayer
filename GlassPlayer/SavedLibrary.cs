using System.IO;
using System.Text.Json;

namespace GlassPlayer;

sealed record SavedPlaylist(string Title, List<Track> Tracks, string? CoverArt = null, string? SourceUri = null)
{
    public string Artist => $"{Tracks.Count} tracks · saved on this PC";
    public string? Art => CoverArt ?? Tracks.FirstOrDefault()?.Art;
    public string DurationText => "";
}

// Metadata only. Never persist provider stream tokens or cache downloaded audio here.
sealed class SavedLibrary(string? path = null)
{
    readonly string file = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlassPlayer", "saved-imports.json");

    public List<SavedPlaylist> Load()
    {
        if (!File.Exists(file)) return new();
        var lists = JsonSerializer.Deserialize<List<SavedPlaylist>>(File.ReadAllText(file))
            ?? throw new InvalidDataException("The saved library is empty or invalid.");
        if (lists.Any(p => p == null || string.IsNullOrWhiteSpace(p.Title) || p.Tracks == null || p.Tracks.Any(t => t == null)))
            throw new InvalidDataException("The saved library contains an invalid playlist.");
        return lists;
    }

    public void Save(string title, IEnumerable<Track> tracks, string? coverArt = null, string? sourceUri = null)
    {
        var clean = tracks.Select(t => t with { Preview = null }).ToList();
        if (clean.Count == 0 || clean.Any(t => t.Source is not ("SoundCloud" or "YouTube Music" or "Local" or "Spotify")))
            throw new InvalidDataException("Save tracks from SoundCloud, YouTube, Spotify or local files.");
        var lists = Load(); // Preserve a damaged library instead of silently replacing it.
        var saved = new SavedPlaylist(string.IsNullOrWhiteSpace(title) ? "Imported music" : title, clean, coverArt, sourceUri);
        // Separate playlists may contain exactly the same songs. Preserve their names
        // and covers; a source link identifies a playlist even when its songs change.
        var existing = lists.FindIndex(p => !string.IsNullOrWhiteSpace(sourceUri)
            ? p.SourceUri == sourceUri
            : p.SourceUri == null && p.Title == saved.Title && p.Tracks.Select(t => (t.Source, t.Id)).SequenceEqual(clean.Select(t => (t.Source, t.Id))));
        if (existing >= 0) lists[existing] = saved;
        else lists.Add(saved);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(lists));
        File.Move(temporary, file, true);
    }
}
