using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GlassPlayer;

sealed record OfflineTrack(Track Original, string AudioSource, string FileName, string? ArtworkFile);

sealed class OfflineLibrary(string? directory = null)
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    public string DirectoryPath { get; } = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "GlassPlayer");
    string Manifest => Path.Combine(DirectoryPath, "library.json");
    List<OfflineTrack> Read() => File.Exists(Manifest)
        ? JsonSerializer.Deserialize<List<OfflineTrack>>(File.ReadAllText(Manifest)) ?? new() : new();
    string FilePath(string file) => Path.Combine(DirectoryPath, Path.GetFileName(file));
    public List<Track> Tracks() => Read().Where(e => File.Exists(FilePath(e.FileName))).Select(e => e.Original with
    { Source = "Local", OpenUri = FilePath(e.FileName), Preview = null, Art = e.ArtworkFile != null && File.Exists(FilePath(e.ArtworkFile)) ? FilePath(e.ArtworkFile) : e.Original.Art }).ToList();
    public PlaybackResolution? Find(Track original)
    {
        OfflineTrack? entry;
        try { entry = Read().FirstOrDefault(e => e.Original.Source == original.Source && e.Original.Id == original.Id); }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
        return entry != null && File.Exists(FilePath(entry.FileName)) ? new(FilePath(entry.FileName), "Downloaded · " + entry.AudioSource, entry.Original) : null;
    }
    public async Task SaveAsync(Track original, PlaybackResolution resolved, CancellationToken cancellation)
    {
        var audio = resolved.AudioTrack;
        string file;
        if (File.Exists(resolved.Location)) file = resolved.Location;
        else
        {
            string url = audio.Source == "YouTube Music" ? "https://music.youtube.com/watch?v=" + audio.Id
                : audio.Source == "SoundCloud" && audio.OpenUri != null ? audio.OpenUri
                : throw new InvalidOperationException("This source cannot save an audio file.");
            file = await Dlp.DownloadAsync(url, "offline" + Key(original), cancellation);
        }
        await StoreAsync(original with { DurationMs = audio.DurationMs > 0 ? audio.DurationMs : original.DurationMs, IsPreview = false }, resolved.Source, file, cancellation);
    }
    internal async Task StoreAsync(Track original, string audioSource, string sourceFile, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var entries = Read(); // Preserve a damaged manifest instead of replacing it.
        Directory.CreateDirectory(DirectoryPath);
        string key = Key(original);
        string name = string.Concat((original.Artist + " - " + original.Title).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        name = name[..Math.Min(name.Length, 100)].TrimEnd('.', ' ');
        string filename = name + " - " + key + Path.GetExtension(sourceFile);
        string target = FilePath(filename);
        if (!string.Equals(Path.GetFullPath(sourceFile), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            var temporary = target + ".tmp";
            try
            {
                await using (var input = File.OpenRead(sourceFile))
                await using (var output = File.Create(temporary)) await input.CopyToAsync(output, cancellation);
                cancellation.ThrowIfCancellationRequested();
                File.Move(temporary, target, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        string? artwork = null;
        if (Uri.TryCreate(original.Art, UriKind.Absolute, out var uri) && uri.Scheme == "https")
        {
            try
            {
                using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation);
                response.EnsureSuccessStatusCode();
                // Artwork is optional and bounded; never buffer a full song in memory.
                if (response.Content.Headers.ContentLength is null or > 0 and <= 2_000_000)
                {
                    await using var input = await response.Content.ReadAsStreamAsync(cancellation);
                    using var content = new MemoryStream();
                    var buffer = new byte[32768];
                    int count;
                    while ((count = await input.ReadAsync(buffer, cancellation)) > 0)
                    {
                        if (content.Length + count > 2_000_000) throw new InvalidDataException("Artwork too large");
                        content.Write(buffer, 0, count);
                    }
                    artwork = key + ".jpg";
                    await File.WriteAllBytesAsync(FilePath(artwork), content.ToArray(), cancellation);
                }
            }
            catch when (!cancellation.IsCancellationRequested) { }
        }
        cancellation.ThrowIfCancellationRequested();
        entries.RemoveAll(e => e.Original.Source == original.Source && e.Original.Id == original.Id);
        entries.Add(new(original with { Preview = null }, audioSource, filename, artwork));
        var pending = Manifest + ".tmp";
        File.WriteAllText(pending, JsonSerializer.Serialize(entries));
        File.Move(pending, Manifest, true);
    }
    static string Key(Track track) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(track.Source + ":" + track.Id)))[..12];
}
