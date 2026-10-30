using System.Windows;
using System.Windows.Controls;

namespace GlassPlayer;

sealed record LibraryPlaylistItem(object Playlist, string Title, string Artist, string? Art, List<PlaylistFolder> Folders)
{
    public string DurationText => "›";
    public static LibraryPlaylistItem From(SavedPlaylist playlist) => new(playlist, playlist.Title,
        $"{playlist.Tracks.Count} {(playlist.Tracks.Count == 1 ? "song" : "songs")} · {playlist.Source}", playlist.Art, playlist.Folders ?? new());
    public static LibraryPlaylistItem From(Spotify.Playlist playlist) => new(playlist, playlist.Title,
        playlist.Artist, playlist.Art, playlist.Folders ?? new());
}

sealed record LibraryFolderItem(PlaylistFolder Folder, int Count)
{
    public string Title => Folder.Title;
    public string Artist => $"{Count} {(Count == 1 ? "playlist" : "playlists")}";
}

static class LibraryNavigation
{
    public static List<object> Level(IEnumerable<LibraryPlaylistItem> playlists, IReadOnlyList<PlaylistFolder> path)
    {
        var children = playlists.Where(p => p.Folders.Count >= path.Count
            && p.Folders.Take(path.Count).Select(f => f.Id).SequenceEqual(path.Select(f => f.Id))).ToList();
        var folders = children.Where(p => p.Folders.Count > path.Count)
            .GroupBy(p => p.Folders[path.Count].Id)
            .Select(g => (object)new LibraryFolderItem(g.First().Folders[path.Count], g.Count()));
        return folders.Concat(children.Where(p => p.Folders.Count == path.Count)).ToList();
    }
}

sealed class LibraryTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TrackTemplate { get; set; }
    public DataTemplate? PlaylistTemplate { get; set; }
    public DataTemplate? FolderTemplate { get; set; }
    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item switch
    {
        LibraryFolderItem => FolderTemplate,
        LibraryPlaylistItem => PlaylistTemplate,
        _ => TrackTemplate
    };
}
