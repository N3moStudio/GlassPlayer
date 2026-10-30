// Modified 2026-10-26 by N3moStudio: native ARM64, performance, Spotify integration and packaging. Original work by Jonzu00; GPL-3.0.
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Globalization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GlassPlayer;

public record Track(string Id, string Title, string Artist, string? Art, string? Preview, string Source, string? OpenUri, int DurationMs = 0, bool IsPreview = false, bool CatalogRecording = false, string? DownloadedFrom = null)
{
    public string DurationText => IsPreview ? "Preview" : DurationMs > 0 ? $"{DurationMs / 60000}:{DurationMs / 1000 % 60:00}" : "";
}

public interface IProvider
{
    string Name { get; }
    bool Enabled { get; }
    Task<List<Track>> SearchAsync(string query);
    /// <summary>Returns a URL the built-in player can stream, or null if the track must be opened elsewhere.</summary>
    Task<string?> ResolveAsync(Track t);
    Task<List<Track>> SearchAsync(string query, CancellationToken cancellation) => SearchAsync(query).WaitAsync(cancellation);
    Task<string?> ResolveAsync(Track t, CancellationToken cancellation) => ResolveAsync(t).WaitAsync(cancellation);
}

static class Config
{
    static readonly Dictionary<string, string> file = Load();
    static Dictionary<string, string> Load()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config.json"))) ?? new(); }
        catch { return new(); }
    }
    public static string? Get(string key)
    {
        var env = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return file.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
    }
}

/// <summary>Downloaded songs are kept here briefly so replays are instant.</summary>
static class Cache
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassPlayer", "cache");

    public static string? Find(string key)
    {
        if (!Directory.Exists(Dir)) return null;
        return Directory.GetFiles(Dir, key + ".*").FirstOrDefault(f =>
            Path.GetExtension(f).ToLowerInvariant() is ".m4a" or ".mp3" or ".opus" or ".ogg" or ".webm" or ".mp4" or ".flac" or ".wav"
            && new FileInfo(f).Length > 0);
    }

    public static string PathFor(string key, string ext)
    {
        Directory.CreateDirectory(Dir);
        return Path.Combine(Dir, key + "." + ext);
    }

    public static void Cleanup()
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            foreach (var f in Directory.GetFiles(Dir))
                if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(f)).TotalDays > 3) File.Delete(f);
        }
        catch { }
    }
}

/// <summary>yt-dlp helper: downloaded once into %LocalAppData%\GlassPlayer\tools and refreshed every few days.</summary>
static class Dlp
{
    static readonly RecentCache<string, string> Streams = new(32, TimeSpan.FromMinutes(3));
    static readonly SemaphoreSlim StreamWorkers = new(1, 1);
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<string>>> Downloads = new();
    static readonly HttpClient Http = new();
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassPlayer", "tools");
    static readonly string Asset = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "yt-dlp_arm64.exe" : "yt-dlp.exe";
    static readonly string Exe = Path.Combine(Dir, Asset);
    static readonly SemaphoreSlim SetupGate = new(1, 1);
    static bool ready;

    public static async Task EnsureAsync(CancellationToken cancellation = default)
    {
        await SetupGate.WaitAsync(cancellation);
        try
        {
            if (ready) return;
            Directory.CreateDirectory(Dir);
            if (!File.Exists(Exe))
            {
                using var response = await Http.GetAsync("https://github.com/yt-dlp/yt-dlp/releases/latest/download/" + Asset, HttpCompletionOption.ResponseHeadersRead, cancellation);
                response.EnsureSuccessStatusCode();
                await using (var input = await response.Content.ReadAsStreamAsync(cancellation))
                await using (var output = File.Create(Exe + ".download")) await input.CopyToAsync(output, cancellation);
                cancellation.ThrowIfCancellationRequested();
                File.Move(Exe + ".download", Exe, true);
            }
            else if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(Exe)).TotalDays > 5)
            {
                try { await RunAsync("-U", cancellation: cancellation, timeout: TimeSpan.FromSeconds(6)); File.SetLastWriteTimeUtc(Exe, DateTime.UtcNow); }
                catch when (!cancellation.IsCancellationRequested) { }
            }
            ready = true;
        }
        finally { SetupGate.Release(); }
    }

    public static async Task<string> RunAsync(string args, bool allowPartial = false, CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(Exe, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8
        };
        cancellation.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
        using var proc = Process.Start(psi)!;
        var err = proc.StandardError.ReadToEndAsync();
        var stdout = proc.StandardOutput.ReadToEndAsync();
        try { await proc.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await proc.WaitForExitAsync();
            await Task.WhenAll(err, stdout);
            if (cancellation.IsCancellationRequested) throw;
            throw new TimeoutException("The audio source took too long to respond. Try this song again.");
        }
        var output = await stdout;
        var error = await err;
        if (proc.ExitCode != 0 && !(allowPartial && output.Length > 0))
        {
            var line = (error.Split('\n').LastOrDefault(l => l.Contains("ERROR")) ?? "").Trim();
            line = Regex.Replace(line, @"https?://\S+", "<audio source>");
            if (line.Length > 160) line = line[..160];
            throw new Exception(line.Length > 0 ? line : "Couldn't load this track. Try another one.");
        }
        return output;
    }

    public static async Task<string> StreamAsync(string url, string key, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (Cache.Find(key) is { } file) return file;
        if (Streams.TryGet(key, out var hit)) return hit;
        await StreamWorkers.WaitAsync(cancellation);
        try
        {
            if (Streams.TryGet(key, out hit)) return hit;
            await EnsureAsync(cancellation);
            var output = await RunAsync($"--no-playlist --no-warnings --socket-timeout 8 --retries 0 --extractor-retries 0 -f \"bestaudio[ext=m4a]/bestaudio/18/best\" --get-url -- \"{url.Replace("\"", "")}\"", cancellation: cancellation, timeout: TimeSpan.FromSeconds(20));
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length != 1 || !Uri.TryCreate(lines[0], UriKind.Absolute, out var uri) || uri.Scheme != "https")
                throw new InvalidDataException("The source did not return a playable audio stream.");
            cancellation.ThrowIfCancellationRequested();
            Streams.Set(key, lines[0]);
            return lines[0];
        }
        finally { StreamWorkers.Release(); }
    }

    /// <summary>Downloads the audio of a page to the cache and returns the local file path.</summary>
    public static Task<string> DownloadAsync(string url, string key, CancellationToken cancellation = default)
        => ShareDownloadAsync(key, () => DownloadCoreAsync(url, key, cancellation)).WaitAsync(cancellation);

    internal static async Task<string> ShareDownloadAsync(string key, Func<Task<string>> download)
    {
        // Rapid A → B → A selections must not run two writers against A's cache file.
        var pending = Downloads.GetOrAdd(key, _ => new Lazy<Task<string>>(download));
        try { return await pending.Value; }
        finally { Downloads.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(key, pending)); }
    }

    static async Task<string> DownloadCoreAsync(string url, string key, CancellationToken cancellation)
    {
        var hit = Cache.Find(key);
        if (hit != null) return hit;
        await EnsureAsync(cancellation);
        Directory.CreateDirectory(Cache.Dir);
        await RunAsync($"-f \"bestaudio[ext=m4a]/bestaudio/18/best\" --socket-timeout 10 --retries 1 --no-playlist --no-warnings -q --no-progress -o \"{Path.Combine(Cache.Dir, key)}.%(ext)s\" -- \"{url.Replace("\"", "")}\"", cancellation: cancellation, timeout: TimeSpan.FromMinutes(5));
        return Cache.Find(key) ?? throw new Exception("Couldn't download this track.");
    }
}

/// <summary>SoundCloud without any API key: reads the public web client id, like the website itself does.</summary>
class SoundCloud : IProvider
{
    static readonly HttpClient Http = Make();
    static HttpClient Make()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        return h;
    }

    string? clientId;
    readonly RecentCache<string, Track> metadata = new(64, TimeSpan.FromMinutes(20));
    public string Name => "SoundCloud";
    public bool Enabled => true;

    async Task<string> ClientIdAsync(bool refresh = false, CancellationToken cancellation = default)
    {
        if (clientId != null && !refresh) return clientId;
        var html = await Http.GetStringAsync("https://soundcloud.com", cancellation);
        var scripts = Regex.Matches(html, "<script[^>]+src=\"(https://[^\"]+\\.js)\"").Select(m => m.Groups[1].Value).Reverse().ToList();
        foreach (var url in scripts)
        {
            try
            {
                cancellation.ThrowIfCancellationRequested();
                var js = await Http.GetStringAsync(url, cancellation);
                var m = Regex.Match(js, "client_id\\s*[:=]\\s*\"([A-Za-z0-9]{32})\"");
                if (m.Success) return clientId = m.Groups[1].Value;
            }
            catch when (!cancellation.IsCancellationRequested) { }
        }
        throw new Exception("Couldn't reach SoundCloud.");
    }

    public Task<List<Track>> SearchAsync(string q) => SearchAsync(q, CancellationToken.None);
    public async Task<List<Track>> SearchAsync(string q, CancellationToken cancellation)
    {
        List<Track> list = new();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try { list = await ApiSearchAsync(q, deadline.Token); } catch when (!cancellation.IsCancellationRequested) { }
        cancellation.ThrowIfCancellationRequested();
        if (list.Count == 0) list = await DlpSearchAsync(q, cancellation);
        return list;
    }

    async Task<List<Track>> ApiSearchAsync(string q, CancellationToken cancellation)
    {
        for (int attempt = 0; ; attempt++)
        {
            var cid = await ClientIdAsync(attempt > 0, cancellation);
            using var res = await Http.GetAsync($"https://api-v2.soundcloud.com/search/tracks?q={Uri.EscapeDataString(q)}&client_id={cid}&limit=30", cancellation);
            if (attempt == 0 && !res.IsSuccessStatusCode) continue;
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var list = new List<Track>();
            foreach (var t in doc.RootElement.GetProperty("collection").EnumerateArray())
            {
                try
                {
                    bool restricted = t.TryGetProperty("policy", out var pol) && pol.GetString() is "SNIP" or "BLOCK";
                    string? progressive = null;
                    if (t.TryGetProperty("media", out var media) && media.TryGetProperty("transcodings", out var tr))
                        foreach (var x in tr.EnumerateArray())
                            if (x.GetProperty("format").GetProperty("protocol").GetString() == "progressive") progressive = x.GetProperty("url").GetString();
                    string auth = t.TryGetProperty("track_authorization", out var ta) ? ta.GetString() ?? "" : "";
                    string? art = t.TryGetProperty("artwork_url", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
                    if (art == null && t.GetProperty("user").TryGetProperty("avatar_url", out var av) && av.ValueKind == JsonValueKind.String) art = av.GetString();
                    art = art?.Replace("-large", "-t500x500");
                    string? page = t.TryGetProperty("permalink_url", out var pu) ? pu.GetString() : null;
                    list.Add(new Track(t.GetProperty("id").GetRawText(), t.GetProperty("title").GetString() ?? "",
                        t.GetProperty("user").GetProperty("username").GetString() ?? "", art,
                        progressive != null ? progressive + "|" + auth : "", Name, page,
                        t.TryGetProperty("full_duration", out var full) && full.TryGetInt32(out var fullMs) ? fullMs : t.TryGetProperty("duration", out var du) && du.TryGetInt32(out var dms) ? dms : 0, restricted));
                }
                catch { }
            }
            return list;
        }
    }

    async Task<List<Track>> DlpSearchAsync(string q, CancellationToken cancellation)
    {
        await Dlp.EnsureAsync(cancellation);
        var output = await Dlp.RunAsync($"--flat-playlist -j --no-warnings --socket-timeout 8 --retries 0 -- \"scsearch10:{q.Replace("\"", "")}\"", cancellation: cancellation, timeout: TimeSpan.FromSeconds(20));
        var list = new List<Track>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var d = JsonDocument.Parse(line);
                var e = d.RootElement;
                string? S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                list.Add(new Track(e.GetProperty("id").ToString(), S("title") ?? "", S("uploader") ?? "", S("thumbnail"), "", Name, S("webpage_url"),
                    e.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number ? (int)(du.GetDouble() * 1000) : 0));
            }
            catch { }
        }
        return list;
    }

    public Task<string?> ResolveAsync(Track t) => ResolveAsync(t, CancellationToken.None);
    public async Task<string?> ResolveAsync(Track t, CancellationToken cancellation)
    {
        if (t.IsPreview) throw new InvalidOperationException("SoundCloud only provides a preview of this recording. Trying a full-length match.");
        var key = "sc" + t.Id;
        var hit = Cache.Find(key);
        if (hit != null) return hit;
        if (!string.IsNullOrEmpty(t.Preview) && t.Preview.Contains('|'))
        {
            try
            {
                var parts = t.Preview.Split('|');
                var cid = await ClientIdAsync(cancellation: cancellation);
                using var doc = JsonDocument.Parse(await Http.GetStringAsync($"{parts[0]}?client_id={cid}&track_authorization={parts[1]}", cancellation));
                var media = doc.RootElement.GetProperty("url").GetString()!;
                if (Uri.TryCreate(media, UriKind.Absolute, out var uri) && uri.Scheme == "https") return media;
            }
            catch when (!cancellation.IsCancellationRequested) { }
        }
        cancellation.ThrowIfCancellationRequested();
        if (t.OpenUri != null) return await Dlp.StreamAsync(t.OpenUri, key, cancellation);
        return null;
    }

    internal async Task<Track> RefreshPreviewAsync(Track track, CancellationToken cancellation)
    {
        if (metadata.TryGet(track.Id, out var cached)) return cached;
        if (track.OpenUri == null) throw new InvalidOperationException("This saved track may be a preview. Find a full recording on YouTube.");
        var uri = new Uri(track.OpenUri);
        if (uri.Scheme != "https" || uri.Host != "soundcloud.com") throw new InvalidDataException("Invalid SoundCloud track link.");
        var html = await Http.GetStringAsync(uri, cancellation);
        var match = Regex.Match(html, @"window\.__sc_hydration\s*=\s*(\[.*?\]);", RegexOptions.Singleline);
        if (!match.Success) throw new InvalidOperationException("Couldn't check whether this SoundCloud track is a preview. Find a full recording on YouTube.");
        using var doc = JsonDocument.Parse(match.Groups[1].Value);
        var data = doc.RootElement.EnumerateArray().FirstOrDefault(e => SpotifyWebLibrary.At(e, "hydratable").ToString() == "sound");
        var result = ApplyMetadata(track, SpotifyWebLibrary.At(data, "data"));
        metadata.Set(track.Id, result);
        return result;
    }

    internal static Track ApplyMetadata(Track track, JsonElement data)
    {
        if (SpotifyWebLibrary.At(data, "id").ToString() != track.Id) throw new InvalidDataException("SoundCloud returned a different recording.");
        var full = SpotifyWebLibrary.At(data, "full_duration");
        int duration = full.ValueKind == JsonValueKind.Number && full.TryGetInt32(out var ms) ? ms : track.DurationMs;
        bool preview = SpotifyWebLibrary.At(data, "policy").ToString() is "SNIP" or "BLOCK";
        return track with { DurationMs = duration, IsPreview = preview };
    }
}

/// <summary>YouTube Music without any API key: public web search, yt-dlp for the audio.</summary>
class YouTubeMusic : IProvider
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    readonly RecentCache<string, List<Track>> searches = new(16, TimeSpan.FromMinutes(10));
    public string Name => "YouTube Music";
    public bool Enabled => true;

    public Task<List<Track>> SearchAsync(string q) => SearchAsync(q, CancellationToken.None);
    public async Task<List<Track>> SearchAsync(string q, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (searches.TryGet(q, out var cached)) return new(cached);
        var list = new List<Track>();
        try { list = await InnerTubeSearchAsync(q, cancellation); } catch when (!cancellation.IsCancellationRequested) { }
        cancellation.ThrowIfCancellationRequested();
        if (list.Count == 0) list = await DlpSearchAsync(q, cancellation);
        if (list.Count > 0) searches.Set(q, list);
        return list;
    }

    static async Task<List<Track>> InnerTubeSearchAsync(string q, CancellationToken cancellation)
    {
        var body = JsonSerializer.Serialize(new
        {
            context = new { client = new { clientName = "WEB_REMIX", clientVersion = $"1.{DateTime.UtcNow.AddDays(-1):yyyyMMdd}.01.00", hl = "en", gl = "US" } },
            query = q,
            @params = "EgWKAQIIAWoMEA4QChADEAQQCRAF" // songs only
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://music.youtube.com/youtubei/v1/search?prettyPrint=false")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:128.0) Gecko/20100101 Firefox/128.0");
        req.Headers.Add("Origin", "https://music.youtube.com");
        req.Headers.Add("Referer", "https://music.youtube.com/");
        using var res = await Http.SendAsync(req, cancellation);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var list = new List<Track>();
        Walk(doc.RootElement, list);
        return list;
    }

    static void Walk(JsonElement e, List<Track> list)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            if (e.TryGetProperty("musicResponsiveListItemRenderer", out var r))
            {
                var t = ParseItem(r);
                if (t != null) list.Add(t);
                return;
            }
            foreach (var p in e.EnumerateObject()) Walk(p.Value, list);
        }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var x in e.EnumerateArray()) Walk(x, list);
    }

    internal static Track? ParseItem(JsonElement r)
    {
        try
        {
            string? id = r.TryGetProperty("playlistItemData", out var pd) ? pd.GetProperty("videoId").GetString() : null;
            if (id == null) return null;
            var cols = r.GetProperty("flexColumns");
            var title = cols[0].GetProperty("musicResponsiveListItemFlexColumnRenderer").GetProperty("text").GetProperty("runs")[0].GetProperty("text").GetString() ?? "";
            var runs = cols[1].GetProperty("musicResponsiveListItemFlexColumnRenderer").GetProperty("text").GetProperty("runs");
            var texts = runs.EnumerateArray()
                .Select(x => x.GetProperty("text").GetString() ?? "").Where(s => s.Trim() != "•" && s != "Song" && s != "Video").ToList();
            string? art = null;
            if (r.TryGetProperty("thumbnail", out var th))
            {
                var arr = th.GetProperty("musicThumbnailRenderer").GetProperty("thumbnail").GetProperty("thumbnails");
                art = arr[arr.GetArrayLength() - 1].GetProperty("url").GetString();
                if (art != null) art = Regex.Replace(art, "=w\\d+-h\\d+", "=w512-h512");
            }
            static bool IsDur(string s) => Regex.IsMatch(s.Trim(), "^\\d+(:\\d{2}){1,2}$");
            var dur = texts.LastOrDefault(IsDur);
            int ms = dur == null ? 0 : dur.Trim().Split(':').Select(int.Parse).Aggregate(0, (acc, s) => acc * 60 + s) * 1000;
            var artists = runs.EnumerateArray().Where(x => SpotifyWebLibrary.At(x, "navigationEndpoint", "browseEndpoint",
                "browseEndpointContextSupportedConfigs", "browseEndpointContextMusicConfig", "pageType").ToString() == "MUSIC_PAGE_TYPE_ARTIST")
                .Select(x => x.GetProperty("text").GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
            var artist = artists.Length > 0 ? string.Join(", ", artists) : texts.FirstOrDefault(s => !IsDur(s)) ?? "";
            return new Track(id, title, artist, art, null, "YouTube Music", null, ms, CatalogRecording: artists.Length > 0);
        }
        catch { return null; }
    }

    static async Task<List<Track>> DlpSearchAsync(string q, CancellationToken cancellation)
    {
        await Dlp.EnsureAsync(cancellation);
        var output = await Dlp.RunAsync($"--flat-playlist -j --no-warnings --socket-timeout 8 --retries 0 -- \"ytsearch12:{q.Replace("\"", "")}\"", cancellation: cancellation, timeout: TimeSpan.FromSeconds(20));
        var list = new List<Track>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var d = JsonDocument.Parse(line);
                var e = d.RootElement;
                string? art = null;
                if (e.TryGetProperty("thumbnails", out var th) && th.ValueKind == JsonValueKind.Array && th.GetArrayLength() > 0)
                    art = th[th.GetArrayLength() - 1].GetProperty("url").GetString();
                string artist = (e.TryGetProperty("channel", out var c) && c.ValueKind == JsonValueKind.String) ? c.GetString()!
                              : (e.TryGetProperty("uploader", out var u) && u.ValueKind == JsonValueKind.String) ? u.GetString()! : "";
                list.Add(new Track(e.GetProperty("id").GetString()!, e.GetProperty("title").GetString() ?? "", artist.Replace(" - Topic", ""), art, null, "YouTube Music", null,
                    e.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number ? (int)(du.GetDouble() * 1000) : 0));
            }
            catch { }
        }
        return list;
    }

    public Task<string?> ResolveAsync(Track t)
        => ResolveAsync(t, CancellationToken.None);
    public async Task<string?> ResolveAsync(Track t, CancellationToken cancellation)
        => await Dlp.StreamAsync($"https://music.youtube.com/watch?v={t.Id}", "yt" + t.Id, cancellation);
}

/// <summary>Songs imported from files on this PC.</summary>
class LocalFiles : IProvider
{
    public string Name => "Local";
    public bool Enabled => true;
    public Task<List<Track>> SearchAsync(string q) => Task.FromResult(new List<Track>());
    public Task<string?> ResolveAsync(Track t) => Task.FromResult<string?>(t.OpenUri);

    public static Track ToTrack(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var parts = name.Split(" - ", 2);
        return parts.Length == 2
            ? new Track(path, parts[1].Trim(), parts[0].Trim(), null, null, "Local", path)
            : new Track(path, name, "This PC", null, null, "Local", path);
    }
}

sealed record ImportedPlaylist(string Title, List<Track> Tracks, string? CoverArt, string SourceUri, List<PlaylistFolder>? Folders = null);

/// <summary>Imports a playlist or song from a YouTube, YouTube Music or SoundCloud link.</summary>
static class Importer
{
    internal static string SourceFromLink(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Paste an HTTPS playlist or song link.");
        return uri.Host.ToLowerInvariant() switch
        {
            "soundcloud.com" or "www.soundcloud.com" or "on.soundcloud.com" => "SoundCloud",
            "youtube.com" or "www.youtube.com" or "music.youtube.com" or "youtu.be" => "YouTube Music",
            "open.spotify.com" => "Spotify",
            _ => throw new ArgumentException("Paste a link from YouTube, SoundCloud or Spotify.")
        };
    }

    internal static string TitleFromLink(string link) => Uri.TryCreate(link, UriKind.Absolute, out var uri)
        ? Uri.UnescapeDataString(uri.Segments.LastOrDefault()?.Trim('/') ?? "Imported music").Replace('-', ' ')
        : "Imported music";

    public static async Task<List<Track>> FromLinkAsync(string url) => (await PlaylistFromLinkAsync(url)).Tracks;

    public static async Task<ImportedPlaylist> PlaylistFromLinkAsync(string url, CancellationToken cancellation = default)
    {
        var source = SourceFromLink(url);
        if (source == "Spotify") throw new ArgumentException("Connect your Spotify library to import Spotify references.");
        bool sc = source == "SoundCloud";
        await Dlp.EnsureAsync(cancellation);
        // Preserve metadata for protected/unavailable recordings so matching can
        // happen at playback time. This flag is never used for audio downloads.
        var output = await Dlp.RunAsync($"-J {(sc ? "" : "--flat-playlist ")}--no-warnings --skip-download --ignore-no-formats-error -- \"{new Uri(url).AbsoluteUri}\"", allowPartial: false, cancellation: cancellation, timeout: TimeSpan.FromMinutes(3));
        return ParsePlaylist(output, sc, url);
    }

    internal static ImportedPlaylist ParsePlaylist(string output, bool sc, string sourceUri)
    {
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        var tracks = new List<Track>();
        if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            foreach (var entry in entries.EnumerateArray()) tracks.Add(ParseTrack(entry, sc));
        else tracks.Add(ParseTrack(root, sc));
        return new ImportedPlaylist(Text(root, "title") ?? TitleFromLink(sourceUri), tracks, Artwork(root), sourceUri);
    }

    static string? Text(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static string? Artwork(JsonElement element)
    {
        var art = Text(element, "thumbnail");
        if (!string.IsNullOrWhiteSpace(art)) return art;
        if (element.TryGetProperty("thumbnails", out var thumbnails) && thumbnails.ValueKind == JsonValueKind.Array)
            return thumbnails.EnumerateArray().Select(t => Text(t, "url")).LastOrDefault(url => !string.IsNullOrWhiteSpace(url));
        return null;
    }

    static Track ParseTrack(JsonElement element, bool sc)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("id", out var id) || id.ValueKind is not (JsonValueKind.Number or JsonValueKind.String) || string.IsNullOrWhiteSpace(Text(element, "title")))
            throw new InvalidDataException("A track could not be imported. The incomplete playlist was not saved.");
        int duration = element.TryGetProperty("duration", out var value) && value.ValueKind == JsonValueKind.Number
            ? (int)Math.Clamp(value.GetDouble() * 1000, 0, int.MaxValue) : 0;
        var artist = (Text(element, "artist") ?? Text(element, "uploader") ?? Text(element, "channel") ?? "").Replace(" - Topic", "");
        return new Track(id.ToString(), Text(element, "title")!, artist, Artwork(element), null,
            sc ? "SoundCloud" : "YouTube Music", sc ? Text(element, "webpage_url") : null, duration);
    }

    internal static List<Track> ParseOutput(string output, bool sc)
    {
        var list = new List<Track>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var d = JsonDocument.Parse(line);
            list.Add(ParseTrack(d.RootElement, sc));
        }
        return list;
    }
}
