namespace GlassPlayer;

sealed record DownloadProgress(int Completed, int Total, string Title, int Saved, int Existing, int Failed);
sealed record DownloadFailure(string Title, string Message);
sealed record DownloadBatchResult(int Saved, int Existing, IReadOnlyList<DownloadFailure> Failures);

// One song at a time; completed files survive cancellation and are skipped on resume.
sealed class DownloadBatch(Func<Track, bool> exists,
    Func<Track, CancellationToken, Task> save)
{
    public async Task<DownloadBatchResult> RunAsync(IEnumerable<SavedPlaylist> playlists,
        Action<DownloadProgress>? progress = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var tracks = playlists.SelectMany(p => p.Tracks).Where(t => t.Source != "Local")
            .DistinctBy(t => (t.Source, t.Id)).ToArray();
        int saved = 0, existing = 0;
        var failures = new List<DownloadFailure>();
        for (int i = 0; i < tracks.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var track = tracks[i];
            progress?.Invoke(new(i, tracks.Length, track.Title, saved, existing, failures.Count));
            try
            {
                if (exists(track)) existing++;
                else { await save(track, cancellation); saved++; }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception ex) { failures.Add(new(track.Title, ex.Message)); }
            progress?.Invoke(new(i + 1, tracks.Length, "", saved, existing, failures.Count));
        }
        return new(saved, existing, failures);
    }
}

sealed record PlaylistResyncResult(int Updated, IReadOnlyList<DownloadFailure> Failures);

sealed class PlaylistResync(SavedLibrary saved,
    Func<CancellationToken, Task<LibrarySyncResult>>? syncSpotify,
    Func<string, CancellationToken, Task<ImportedPlaylist>> import)
{
    public async Task<PlaylistResyncResult> RunAsync(Action<string>? progress = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        int updated = 0;
        var failures = new List<DownloadFailure>();
        if (syncSpotify != null)
        {
            progress?.Invoke("Refreshing your connected Spotify playlists…");
            try { updated += (await syncSpotify(cancellation)).Playlists; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception ex) { failures.Add(new("Spotify", ex.Message)); }
        }
        var playlists = saved.Load();
        if (syncSpotify == null && playlists.Any(p => p.SourceUri?.StartsWith("https://open.spotify.com/", StringComparison.Ordinal) == true))
            failures.Add(new("Spotify", "Connect your Spotify library to refresh its playlists. Existing saved lists are kept."));
        foreach (var playlist in playlists.Where(p => p.SourceUri != null && !p.SourceUri.StartsWith("https://open.spotify.com/", StringComparison.Ordinal)))
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Invoke("Refreshing " + playlist.Title + "…");
            try
            {
                var refreshed = await import(playlist.SourceUri!, cancellation);
                cancellation.ThrowIfCancellationRequested();
                // Retain an imported/custom playlist cover while refreshing its songs.
                saved.Merge(new[] { new SavedPlaylist(refreshed.Title, refreshed.Tracks,
                    playlist.CoverArt ?? refreshed.CoverArt, playlist.SourceUri, playlist.Folders, playlist.FolderSourceUri) });
                updated++;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception ex) { failures.Add(new(playlist.Title, ex.Message)); }
        }
        return new(updated, failures);
    }
}
