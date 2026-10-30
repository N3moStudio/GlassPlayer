using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GlassPlayer;

sealed record OfflineTrack(Track Original, string AudioSource, string FileName, string? ArtworkFile);
sealed record OfflineLink(Track Original, string DownloadSource, string DownloadId);

sealed class OfflineLibrary(string? directory = null)
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    readonly object manifestGate = new();
    List<OfflineTrack>? cached;
    (DateTime Modified, long Length) cachedVersion;
    List<OfflineLink>? cachedLinks;
    (DateTime Modified, long Length) cachedLinksVersion;
    public string DirectoryPath { get; } = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "GlassPlayer");
    string Manifest => Path.Combine(DirectoryPath, "library.json");
    string LinksFile => Path.Combine(DirectoryPath, "links.json");
    List<OfflineLink> ReadLinks()
    {
        var file = new FileInfo(LinksFile);
        var version = file.Exists ? (file.LastWriteTimeUtc, file.Length) : default;
        if (cachedLinks != null && cachedLinksVersion == version) return cachedLinks;
        var links = file.Exists ? JsonSerializer.Deserialize<List<OfflineLink>>(File.ReadAllText(LinksFile))
            ?? throw new InvalidDataException("Song links could not be read.") : new();
        if (links.Any(l => l == null || l.Original == null || string.IsNullOrWhiteSpace(l.DownloadSource) || string.IsNullOrWhiteSpace(l.DownloadId)))
            throw new InvalidDataException("A saved song link is invalid.");
        cachedLinksVersion = version;
        return cachedLinks = links;
    }
    void WriteLinks(List<OfflineLink> links)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(LinksFile + ".tmp", JsonSerializer.Serialize(links));
        File.Move(LinksFile + ".tmp", LinksFile, true);
        cachedLinks = null;
    }
    public bool HasLink(Track original) { lock (manifestGate) return ReadLinks().Any(l => l.Original.Source == original.Source && l.Original.Id == original.Id); }
    public void Unlink(Track original)
    {
        lock (manifestGate)
        {
            var links = ReadLinks().ToList();
            links.RemoveAll(l => l.Original.Source == original.Source && l.Original.Id == original.Id);
            WriteLinks(links);
        }
    }
    public void Link(Track original, Track downloaded)
    {
        if (original.Source is not ("Spotify" or "SoundCloud")) throw new InvalidOperationException("Choose a Spotify or SoundCloud song to link.");
        lock (manifestGate)
        {
            var entry = Read().FirstOrDefault(e => downloaded.Source == "Local"
                ? string.Equals(FilePath(e.FileName), downloaded.OpenUri, StringComparison.OrdinalIgnoreCase)
                : e.Original.Source == downloaded.Source && e.Original.Id == downloaded.Id);
            if (entry == null || !File.Exists(FilePath(entry.FileName))) throw new InvalidOperationException("Download this recording first, then link it.");
            var links = ReadLinks().ToList();
            links.RemoveAll(l => l.Original.Source == original.Source && l.Original.Id == original.Id);
            links.Add(new(original with { Preview = null }, entry.Original.Source, entry.Original.Id));
            WriteLinks(links);
        }
    }
    List<OfflineTrack> Read()
    {
        lock (manifestGate)
        {
            var file = new FileInfo(Manifest);
            var version = file.Exists ? (file.LastWriteTimeUtc, file.Length) : default;
            if (cached != null && cachedVersion == version) return cached;
            var entries = file.Exists ? JsonSerializer.Deserialize<List<OfflineTrack>>(File.ReadAllText(Manifest))
                ?? throw new InvalidDataException("The downloads library is invalid.") : new();
            if (entries.Any(e => e == null || e.Original == null || string.IsNullOrWhiteSpace(e.FileName) || string.IsNullOrWhiteSpace(e.AudioSource)))
                throw new InvalidDataException("The downloads library contains an invalid song.");
            cachedVersion = version;
            return cached = entries;
        }
    }
    static string SourceName(string source)
    {
        const string prefix = "Downloaded · ";
        while (source.StartsWith(prefix, StringComparison.Ordinal)) source = source[prefix.Length..];
        return source;
    }
    string FilePath(string file) => Path.Combine(DirectoryPath, Path.GetFileName(file));
    public List<Track> Tracks(string? audioSource = null, string? query = null) => Read()
        .Where(e => (audioSource == null || SourceName(e.AudioSource) == audioSource)
            && (string.IsNullOrWhiteSpace(query) || (e.Original.Title + " " + e.Original.Artist).Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
            && File.Exists(FilePath(e.FileName))).Select(e => e.Original with
    { Source = "Local", OpenUri = FilePath(e.FileName), Preview = null, DownloadedFrom = SourceName(e.AudioSource),
        Art = e.ArtworkFile != null && File.Exists(FilePath(e.ArtworkFile)) ? FilePath(e.ArtworkFile) : e.Original.Art }).ToList();
    public PlaybackResolution? Find(Track original)
    {
        OfflineTrack? entry;
        lock (manifestGate)
        {
            var link = ReadLinks().FirstOrDefault(l => l.Original.Source == original.Source && l.Original.Id == original.Id);
            try
            {
                entry = Read().FirstOrDefault(e => e.Original.Source == (link?.DownloadSource ?? original.Source) && e.Original.Id == (link?.DownloadId ?? original.Id));
                if (link != null && (entry == null || !File.Exists(FilePath(entry.FileName))))
                    throw new InvalidOperationException("Your linked download is missing. Download the chosen recording again, or unlink it to use automatic matching.");
            }
            catch (Exception ex) when (link == null && ex is IOException or JsonException) { return null; }
        }
        return entry != null && File.Exists(FilePath(entry.FileName)) ? new(FilePath(entry.FileName), "Downloaded · " + SourceName(entry.AudioSource), entry.Original) : null;
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
        _ = Read(); // Preserve a damaged manifest instead of replacing it.
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
        if (Uri.TryCreate(ArtworkConverter.SizedAddress(original.Art, 512), UriKind.Absolute, out var uri) && uri.Scheme == "https")
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
        lock (manifestGate)
        {
            var entries = Read().ToList();
            entries.RemoveAll(e => e.Original.Source == original.Source && e.Original.Id == original.Id);
            entries.Add(new(original with { Preview = null }, SourceName(audioSource), filename, artwork));
            var pending = Manifest + ".tmp";
            File.WriteAllText(pending, JsonSerializer.Serialize(entries));
            File.Move(pending, Manifest, true);
            cached = null;
        }
    }
    static string Key(Track track) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(track.Source + ":" + track.Id)))[..12];
}
