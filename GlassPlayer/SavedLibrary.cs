using System.IO;
using System.Text.Json;

namespace GlassPlayer;

sealed record PlaylistFolder(string Id, string Title);
sealed record SavedPlaylist(string Title, List<Track> Tracks, string? CoverArt = null, string? SourceUri = null,
    List<PlaylistFolder>? Folders = null, string? FolderSourceUri = null)
{
    public string Artist => $"{Tracks.Count} tracks · saved on this PC";
    public string? Art => CoverArt ?? Tracks.FirstOrDefault()?.Art;
    public string DurationText => "";
    public string Source => SourceUri?.Contains("open.spotify.com/", StringComparison.OrdinalIgnoreCase) == true ? "Spotify"
        : SourceUri?.Contains("soundcloud.com/", StringComparison.OrdinalIgnoreCase) == true ? "SoundCloud"
        : SourceUri?.Contains("youtube.com/", StringComparison.OrdinalIgnoreCase) == true || SourceUri?.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase) == true ? "YouTube Music"
        : Tracks.Select(t => t.Source).Distinct().Count() == 1 ? Tracks[0].Source : "Mixed";
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

    public void Save(string title, IEnumerable<Track> tracks, string? coverArt = null, string? sourceUri = null, List<PlaylistFolder>? folders = null)
    {
        var clean = tracks.Select(t => t with { Preview = null }).ToList();
        if (clean.Count == 0)
            throw new InvalidDataException("Save tracks from SoundCloud, YouTube, Spotify or local files.");
        Merge(new[] { new SavedPlaylist(string.IsNullOrWhiteSpace(title) ? "Imported music" : title, clean, coverArt, sourceUri, folders) });
    }

    // One atomic write after a complete sync. Other imports remain intact, and
    // empty synced playlists can replace playlists the user emptied in Spotify.
    internal void Merge(IEnumerable<SavedPlaylist> playlists)
    {
        var incoming = playlists.Select(p => p with { Tracks = p.Tracks.Select(t => t with { Preview = null }).ToList() }).ToList();
        if (incoming.Any(p => string.IsNullOrWhiteSpace(p.Title) || p.Tracks.Any(t => t.Source is not ("SoundCloud" or "YouTube Music" or "Local" or "Spotify"))))
            throw new InvalidDataException("Save tracks from SoundCloud, YouTube, Spotify or local files.");
        if (incoming.Count == 0) return;
        var lists = Load(); // Preserve a damaged library instead of silently replacing it.
        foreach (var saved in incoming)
        {
            // Separate playlists may contain exactly the same songs. Preserve their names
            // and covers; a source link identifies a playlist even when its songs change.
            var existing = lists.FindIndex(p => !string.IsNullOrWhiteSpace(saved.SourceUri)
                ? p.SourceUri == saved.SourceUri
                : p.SourceUri == null && p.Title == saved.Title && p.Tracks.Select(t => (t.Source, t.Id)).SequenceEqual(saved.Tracks.Select(t => (t.Source, t.Id))));
            if (existing >= 0) lists[existing] = saved.Source != "Spotify"
                ? saved with { Folders = saved.Folders ?? lists[existing].Folders, FolderSourceUri = saved.FolderSourceUri ?? lists[existing].FolderSourceUri } : saved;
            else lists.Add(saved);
        }
        // Copies transferred to another service retain the same organization.
        // Prefer a previously linked playlist URI; only infer a link from a unique exact title.
        var spotify = lists.Where(p => p.Source == "Spotify").ToList();
        for (int i = 0; i < lists.Count; i++)
        {
            var playlist = lists[i];
            if (playlist.Source is not ("SoundCloud" or "YouTube Music")) continue;
            var original = spotify.FirstOrDefault(p => p.SourceUri != null && p.SourceUri == playlist.FolderSourceUri);
            var named = spotify.Where(p => p.Title == playlist.Title).Take(2).ToList();
            original ??= named.Count == 1 ? named[0] : null;
            if (original?.Folders != null) lists[i] = playlist with { Folders = original.Folders, FolderSourceUri = original.SourceUri };
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(lists));
        File.Move(temporary, file, true);
    }
}
