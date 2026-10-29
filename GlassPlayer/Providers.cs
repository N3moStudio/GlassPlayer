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

public record Track(string Id, string Title, string Artist, string? Art, string? Preview, string Source, string? OpenUri, int DurationMs = 0)
{
    public string DurationText => DurationMs > 0 ? $"{DurationMs / 60000}:{DurationMs / 1000 % 60:00}" : "";
}

public interface IProvider
{
    string Name { get; }
    bool Enabled { get; }
    Task<List<Track>> SearchAsync(string query);
    /// <summary>Returns a URL the built-in player can stream, or null if the track must be opened elsewhere.</summary>
    Task<string?> ResolveAsync(Track t);
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
        return Directory.GetFiles(Dir, key + ".*").FirstOrDefault(f => !f.EndsWith(".part") && !f.EndsWith(".tmp") && !f.EndsWith(".ytdl"));
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
    static readonly HttpClient Http = new();
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassPlayer", "tools");
    static readonly string Asset = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "yt-dlp_arm64.exe" : "yt-dlp.exe";
    static readonly string Exe = Path.Combine(Dir, Asset);
    static readonly SemaphoreSlim SetupGate = new(1, 1);
    static bool ready;

    public static async Task EnsureAsync()
    {
        await SetupGate.WaitAsync();
        try
        {
            if (ready) return;
            Directory.CreateDirectory(Dir);
            if (!File.Exists(Exe))
            {
                var bytes = await Http.GetByteArrayAsync("https://github.com/yt-dlp/yt-dlp/releases/latest/download/" + Asset);
                await File.WriteAllBytesAsync(Exe + ".download", bytes);
                File.Move(Exe + ".download", Exe, true);
            }
            else if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(Exe)).TotalDays > 5)
            {
                try { await RunAsync("-U"); File.SetLastWriteTimeUtc(Exe, DateTime.UtcNow); } catch { }
            }
            ready = true;
        }
        finally { SetupGate.Release(); }
    }

    public static async Task<string> RunAsync(string args, bool allowPartial = false)
    {
        var psi = new ProcessStartInfo(Exe, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8
        };
        using var proc = Process.Start(psi)!;
        var err = proc.StandardError.ReadToEndAsync();
        var output = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0 && !(allowPartial && output.Length > 0))
        {
            var line = ((await err).Split('\n').LastOrDefault(l => l.Contains("ERROR")) ?? "").Trim();
            if (line.Length > 160) line = line[..160];
            throw new Exception(line.Length > 0 ? line : "Couldn't load this track. Try another one.");
        }
        return output;
    }

    /// <summary>Downloads the audio of a page to the cache and returns the local file path.</summary>
    public static async Task<string> DownloadAsync(string url, string key)
    {
        var hit = Cache.Find(key);
        if (hit != null) return hit;
        await EnsureAsync();
        Directory.CreateDirectory(Cache.Dir);
        await RunAsync($"-f \"bestaudio[ext=m4a]/bestaudio/18/best\" --no-playlist --no-warnings -q --no-progress -o \"{Path.Combine(Cache.Dir, key)}.%(ext)s\" \"{url}\"");
        return Cache.Find(key) ?? throw new Exception("Couldn't download this track.");
    }
}

/// <summary>SoundCloud without any API key: reads the public web client id, like the website itself does.</summary>
class SoundCloud : IProvider
{
    static readonly HttpClient Http = Make();
    static HttpClient Make()
    {
        var h = new HttpClient();
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        return h;
    }

    string? clientId;
    public string Name => "SoundCloud";
    public bool Enabled => true;

    async Task<string> ClientIdAsync(bool refresh = false)
    {
        if (clientId != null && !refresh) return clientId;
        var html = await Http.GetStringAsync("https://soundcloud.com");
        var scripts = Regex.Matches(html, "<script[^>]+src=\"(https://[^\"]+\\.js)\"").Select(m => m.Groups[1].Value).Reverse().ToList();
        foreach (var url in scripts)
        {
            try
            {
                var js = await Http.GetStringAsync(url);
                var m = Regex.Match(js, "client_id\\s*[:=]\\s*\"([A-Za-z0-9]{32})\"");
                if (m.Success) return clientId = m.Groups[1].Value;
            }
            catch { }
        }
        throw new Exception("Couldn't reach SoundCloud.");
    }

    public async Task<List<Track>> SearchAsync(string q)
    {
        List<Track> list = new();
        try { list = await ApiSearchAsync(q); } catch { }
        if (list.Count == 0) list = await DlpSearchAsync(q); // fallback if the web API changes
        return list;
    }

    async Task<List<Track>> ApiSearchAsync(string q)
    {
        for (int attempt = 0; ; attempt++)
        {
            var cid = await ClientIdAsync(attempt > 0);
            var res = await Http.GetAsync($"https://api-v2.soundcloud.com/search/tracks?q={Uri.EscapeDataString(q)}&client_id={cid}&limit=30");
            if (attempt == 0 && !res.IsSuccessStatusCode) continue;
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var list = new List<Track>();
            foreach (var t in doc.RootElement.GetProperty("collection").EnumerateArray())
            {
                try
                {
                    if (t.TryGetProperty("policy", out var pol) && pol.GetString() is "SNIP" or "BLOCK") continue; // previews / blocked
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
                        t.TryGetProperty("duration", out var du) && du.TryGetInt32(out var dms) ? dms : 0));
                }
                catch { }
            }
            return list;
        }
    }

    async Task<List<Track>> DlpSearchAsync(string q)
    {
        await Dlp.EnsureAsync();
        var output = await Dlp.RunAsync($"-j --no-warnings \"scsearch10:{q.Replace("\"", "")}\"");
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

    // Returns a local file path (downloaded into the cache) so playback is rock solid.
    public async Task<string?> ResolveAsync(Track t)
    {
        var key = "sc" + t.Id;
        var hit = Cache.Find(key);
        if (hit != null) return hit;
        if (!string.IsNullOrEmpty(t.Preview) && t.Preview.Contains('|'))
        {
            try
            {
                var parts = t.Preview.Split('|');
                var cid = await ClientIdAsync();
                using var doc = JsonDocument.Parse(await Http.GetStringAsync($"{parts[0]}?client_id={cid}&track_authorization={parts[1]}"));
                var media = doc.RootElement.GetProperty("url").GetString()!;
                var path = Cache.PathFor(key, "mp3");
                using (var s = await Http.GetStreamAsync(media))
                using (var f = File.Create(path + ".tmp"))
                    await s.CopyToAsync(f);
                File.Move(path + ".tmp", path, true);
                return path;
            }
            catch { }
        }
        if (t.OpenUri != null) return await Dlp.DownloadAsync(t.OpenUri, key);
        return null;
    }
}

/// <summary>YouTube Music without any API key: public web search, yt-dlp for the audio.</summary>
class YouTubeMusic : IProvider
{
    static readonly HttpClient Http = new();
    public string Name => "YouTube Music";
    public bool Enabled => true;

    public async Task<List<Track>> SearchAsync(string q)
    {
        var list = new List<Track>();
        try { list = await InnerTubeSearchAsync(q); } catch { }
        if (list.Count == 0) list = await DlpSearchAsync(q);
        return list;
    }

    static async Task<List<Track>> InnerTubeSearchAsync(string q)
    {
        var body = JsonSerializer.Serialize(new
        {
            context = new { client = new { clientName = "WEB_REMIX", clientVersion = $"1.{DateTime.UtcNow.AddDays(-1):yyyyMMdd}.01.00", hl = "en", gl = "US" } },
            query = q,
            @params = "EgWKAQIIAWoMEA4QChADEAQQCRAF" // songs only
        });
        var req = new HttpRequestMessage(HttpMethod.Post, "https://music.youtube.com/youtubei/v1/search?prettyPrint=false")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:128.0) Gecko/20100101 Firefox/128.0");
        req.Headers.Add("Origin", "https://music.youtube.com");
        req.Headers.Add("Referer", "https://music.youtube.com/");
        var res = await Http.SendAsync(req);
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

    static Track? ParseItem(JsonElement r)
    {
        try
        {
            string? id = r.TryGetProperty("playlistItemData", out var pd) ? pd.GetProperty("videoId").GetString() : null;
            if (id == null) return null;
            var cols = r.GetProperty("flexColumns");
            var title = cols[0].GetProperty("musicResponsiveListItemFlexColumnRenderer").GetProperty("text").GetProperty("runs")[0].GetProperty("text").GetString() ?? "";
            var texts = cols[1].GetProperty("musicResponsiveListItemFlexColumnRenderer").GetProperty("text").GetProperty("runs").EnumerateArray()
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
            var artist = texts.FirstOrDefault(s => !IsDur(s)) ?? "";
            return new Track(id, title, artist, art, null, "YouTube Music", null, ms);
        }
        catch { return null; }
    }

    static async Task<List<Track>> DlpSearchAsync(string q)
    {
        await Dlp.EnsureAsync();
        var output = await Dlp.RunAsync($"--flat-playlist -j --no-warnings \"ytsearch12:{q.Replace("\"", "")}\"");
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

    // Downloads the audio to the cache and returns the local file path.
    public Task<string?> ResolveAsync(Track t)
        => Dlp.DownloadAsync($"https://music.youtube.com/watch?v={t.Id}", "yt" + t.Id).ContinueWith(x => (string?)x.Result);
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

sealed record ImportedPlaylist(string Title, List<Track> Tracks, string? CoverArt, string SourceUri);

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

    public static async Task<ImportedPlaylist> PlaylistFromLinkAsync(string url)
    {
        var source = SourceFromLink(url);
        if (source == "Spotify") throw new ArgumentException("Connect your Spotify library to import Spotify references.");
        bool sc = source == "SoundCloud";
        await Dlp.EnsureAsync();
        // Preserve metadata for protected/unavailable recordings so matching can
        // happen at playback time. This flag is never used for audio downloads.
        var output = await Dlp.RunAsync($"-J {(sc ? "" : "--flat-playlist ")}--no-warnings --skip-download --ignore-no-formats-error -- \"{new Uri(url).AbsoluteUri}\"", allowPartial: false);
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
