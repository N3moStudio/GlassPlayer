namespace GlassPlayer;

sealed record LibrarySyncProgress(int Completed, int Total, string Title);
sealed record LibrarySyncResult(int Playlists, int Tracks);

// Pull metadata from the user's connected account. Never modifies Spotify or
// includes downloaded audio, sign-in credentials, or anyone else's library.
sealed class LibrarySync(SavedLibrary saved,
    Func<CancellationToken, Task<List<Spotify.Playlist>>> readPlaylists,
    Func<string, CancellationToken, Task<List<Track>>> readTracks)
{
    public async Task<LibrarySyncResult> RunAsync(Action<LibrarySyncProgress>? progress = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var playlists = await readPlaylists(cancellation);
        var staged = new List<SavedPlaylist>();
        var seen = new HashSet<string>();
        foreach (var playlist in playlists)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!seen.Add(playlist.Id)) throw new InvalidOperationException("Spotify returned a repeated playlist. Saved playlists were not changed.");
            var source = SourceUri(playlist.Id);
            progress?.Invoke(new(staged.Count, playlists.Count, playlist.Title));
            var tracks = await readTracks(playlist.Id, cancellation);
            cancellation.ThrowIfCancellationRequested();
            staged.Add(new(playlist.Title, tracks, playlist.Art, source, playlist.Folders));
        }
        cancellation.ThrowIfCancellationRequested();
        saved.Merge(staged);
        return new(staged.Count, staged.Sum(p => p.Tracks.Count));
    }

    internal static string SourceUri(string id)
    {
        if (id == "liked") return "https://open.spotify.com/collection/tracks";
        SpotifyDesktop.ValidateUri("spotify:playlist:" + id);
        return "https://open.spotify.com/playlist/" + id;
    }
}
