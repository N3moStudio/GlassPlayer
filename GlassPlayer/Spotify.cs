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
using System.Text.RegularExpressions;
using System.Globalization;

namespace GlassPlayer;

/// <summary>
/// Spotify Premium: signs in once with PKCE (no secret needed), loads playlists and optionally plays through the embedded Web Playback SDK.
/// </summary>
class Spotify : IProvider
{
    static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    internal const string Redirect = "http://127.0.0.1:5543/callback";
    internal const string Scopes = "streaming user-read-email user-read-private user-read-playback-state user-modify-playback-state playlist-read-private playlist-read-collaborative";
    readonly HttpClient Http;
    readonly string Store;
    readonly string? id;
    readonly SemaphoreSlim tokenGate = new(1, 1);
    DateTime retryAfter;
    string? rateLimitMessage;
    int sessionVersion;

    public Spotify() : this(SharedHttp, Config.Get("SPOTIFY_CLIENT_ID")) { }

    internal Spotify(HttpClient http, string? clientId, string? store = null, string? initialAccess = null)
    {
        Http = http;
        id = clientId?.Trim();
        // A new scope set and a different Client ID must never reuse an old authorization.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id ?? "")))[..16];
        Store = store ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlassPlayer", $"spotify-v4-{key}.bin");
        access = initialAccess;
        expires = DateTime.UtcNow.AddHours(1);
    }
    string? access, refresh; DateTime expires;

    public string Name => "Spotify";
    public bool Enabled => Library != null || (id != null && Regex.IsMatch(id, "^[a-fA-F0-9]{32}$"));
    public SpotifyDesktop? Desktop;
    public SpotifyWebSession? LibrarySession;
    public SpotifyWebLibrary? Library;
    public WebPlayer? Web;   // built-in "Glass" Spotify device
    bool UseWeb;
    public Task<string> GetTokenAsync() => TokenAsync();
    public bool SignedIn => LibrarySession?.SignedIn ?? (refresh != null || File.Exists(Store));

    // ---- sign-in ----
    static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    string? LoadRefresh()
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(Store), null, DataProtectionScope.CurrentUser)); }
        catch { return null; }
    }

    async Task<string> TokenAsync()
    {
        if (!Enabled) throw new InvalidOperationException("Add a valid Spotify Client ID to config.json, then restart GlassPlayer. Free accounts can use SoundCloud and local audio.");
        await tokenGate.WaitAsync();
        try
        {
            if (access != null && DateTime.UtcNow < expires) return access;
            refresh ??= LoadRefresh();
            if (refresh == null) throw new InvalidOperationException("Connect Spotify in Settings first. API access requires the app owner's Premium account.");
            await ExchangeAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = id! });
            return access!;
        }
        finally { tokenGate.Release(); }
    }

    async Task ExchangeAsync(Dictionary<string, string> form)
    {
        int version = sessionVersion;
        using var content = new FormUrlEncodedContent(form);
        using var res = await Http.PostAsync("https://accounts.spotify.com/api/token", content);
        if (res.StatusCode == HttpStatusCode.BadRequest)
        {
            using var error = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            if (error.RootElement.TryGetProperty("error", out var code) && code.GetString() == "invalid_grant")
            {
                SignOut();
                throw new InvalidOperationException("Spotify authorization expired or was revoked. Connect again in Settings.");
            }
        }
        if (!res.IsSuccessStatusCode) throw new Exception($"Spotify sign-in failed ({(int)res.StatusCode}). Check your Client ID and redirect URI in the Developer Dashboard.");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        if (version != sessionVersion) throw new OperationCanceledException("Spotify was signed out.");
        var r = doc.RootElement;
        access = r.GetProperty("access_token").GetString();
        expires = DateTime.UtcNow.AddSeconds(r.GetProperty("expires_in").GetInt32() - 60);
        if (r.TryGetProperty("refresh_token", out var rt))
        {
            refresh = rt.GetString();
            Directory.CreateDirectory(Path.GetDirectoryName(Store)!);
            File.WriteAllBytes(Store, ProtectedData.Protect(Encoding.UTF8.GetBytes(refresh!), null, DataProtectionScope.CurrentUser));
        }
    }

    async Task LoginAsync()
    {
        var verifier = B64(RandomNumberGenerator.GetBytes(64));
        var challenge = B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = B64(RandomNumberGenerator.GetBytes(16));
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:5543/");
        try { listener.Start(); }
        catch (HttpListenerException) { throw new InvalidOperationException("Cannot listen on 127.0.0.1:5543. Close any other GlassPlayer sign-in window and try again."); }
        Process.Start(new ProcessStartInfo("https://accounts.spotify.com/authorize" +
            $"?client_id={id}&response_type=code&redirect_uri={Uri.EscapeDataString(Redirect)}" +
            $"&scope={Uri.EscapeDataString(Scopes)}&code_challenge_method=S256&code_challenge={challenge}&state={state}") { UseShellExecute = true });
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            while (true)
            {
                var ctx = await listener.GetContextAsync().WaitAsync(timeout.Token);
                var q = ctx.Request.QueryString;
                if (ctx.Request.Url?.AbsolutePath != "/callback" || !ValidState(state, q["state"]))
                {
                    ctx.Response.StatusCode = 400;
                    ctx.Response.Close();
                    continue;
                }
                string message;
                Exception? failure = null;
                try
                {
                    if (q["error"] != null || string.IsNullOrWhiteSpace(q["code"])) throw new InvalidOperationException("Spotify sign-in was cancelled. You can try Connect again.");
                    await ExchangeAsync(new() { ["grant_type"] = "authorization_code", ["code"] = q["code"]!, ["redirect_uri"] = Redirect, ["client_id"] = id!, ["code_verifier"] = verifier });
                    message = "Spotify authorized. Return to GlassPlayer to load your library.";
                }
                catch (Exception ex) { failure = ex; message = "Spotify could not connect. Return to GlassPlayer for details."; }
                var page = Encoding.UTF8.GetBytes("<!doctype html><title>GlassPlayer</title><body style='font-family:Segoe UI;background:#111;color:white;padding:15vh 10%'><h2>" + message + "</h2></body>");
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.Headers["Cache-Control"] = "no-store";
                try { await ctx.Response.OutputStream.WriteAsync(page); }
                catch (HttpListenerException) { }
                finally { ctx.Response.Close(); }
                if (failure != null) throw failure;
                return;
            }
        }
        catch (OperationCanceledException) { throw new TimeoutException($"Sign-in timed out. Save {Redirect} in your Spotify app's Redirect URIs, then try Connect again."); }
    }

    internal static bool ValidState(string expected, string? actual) => actual != null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));

    // ---- API ----
    async Task<HttpResponseMessage> SendAsync(HttpMethod m, string url, object? body = null)
    {
        if (DateTime.UtcNow < retryAfter) throw new InvalidOperationException(rateLimitMessage);
        var address = ApiUri(url);
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(m, address);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync());
            if (m != HttpMethod.Get)
                req.Content = body != null ? new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") : new StringContent("");
            var response = await Http.SendAsync(req);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                response.Dispose(); access = null;
                continue;
            }
            if ((int)response.StatusCode == 429)
            {
                var bodyText = await response.Content.ReadAsStringAsync();
                bool quota = bodyText.Contains("QUOTA_EXCEEDED", StringComparison.Ordinal);
                var delay = response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromMinutes(quota ? 60 : 1);
                retryAfter = DateTime.UtcNow.Add(delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay);
                rateLimitMessage = quota ? "Spotify's developer quota is exhausted. Try again later." : $"Spotify is limiting requests. Try again after {retryAfter.ToLocalTime():t}.";
                response.Dispose();
                throw new InvalidOperationException(rateLimitMessage);
            }
            return response;
        }
    }

    internal static Uri ApiUri(string path)
    {
        var uri = path.StartsWith("/", StringComparison.Ordinal) ? new Uri("https://api.spotify.com/v1" + path) : new Uri(path);
        if (uri.Scheme != "https" || uri.Host != "api.spotify.com" || !uri.IsDefaultPort || !uri.AbsolutePath.StartsWith("/v1/", StringComparison.Ordinal) || uri.UserInfo.Length > 0)
            throw new InvalidOperationException("Spotify returned an invalid page address.");
        return uri;
    }

    static void Check(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException("Spotify denied API access. The app owner needs Premium; your account must be allowed under User Management. Reconnect to grant playlist permissions. Free accounts can use SoundCloud and local audio.");
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("Spotify cannot share this playlist. Development Mode may only allow playlists you own or collaborate on. Try a playlist you own.");
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Spotify authorization is no longer valid. Reconnect in Settings.");
        response.EnsureSuccessStatusCode();
    }

    public async Task<List<Track>> SearchAsync(string q)
    {
        if (Library != null) return await Library.SearchAsync(q);
        if (!Enabled) throw new Exception("Spotify needs a free Client ID. Add SPOTIFY_CLIENT_ID to config.json (see Settings).");
        using var res = await SendAsync(HttpMethod.Get, $"/search?type=track&limit=10&q={Uri.EscapeDataString(q)}");
        Check(res);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var list = new List<Track>();
        foreach (var t in doc.RootElement.GetProperty("tracks").GetProperty("items").EnumerateArray())
            if (ToTrack(t) is { } track) list.Add(track);
        return list;
    }

    internal static Track? ToTrack(JsonElement t)
    {
        if (t.ValueKind != JsonValueKind.Object || !t.TryGetProperty("id", out var ti) || ti.ValueKind != JsonValueKind.String ||
            !t.TryGetProperty("uri", out var uri) || uri.ValueKind != JsonValueKind.String || !uri.GetString()!.StartsWith("spotify:track:", StringComparison.Ordinal)) return null;
        string? art = t.TryGetProperty("album", out var album) ? ImageUrl(album) : null;
        var artist = t.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array
            ? string.Join(", ", artists.EnumerateArray().Select(a => Text(a, "name")).Where(a => a != null)) : "";
        return new Track(ti.GetString()!, Text(t, "name") ?? "Unavailable track", artist, art, null, "Spotify", uri.GetString(),
            t.TryGetProperty("duration_ms", out var dm) && dm.ValueKind == JsonValueKind.Number && dm.TryGetInt32(out var duration) ? duration : 0);
    }

    static string? Text(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    static string? ImageUrl(JsonElement item) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0 ? Text(images[0], "url") : null;
    public record Playlist(string Id, string Title, string Artist, string? Art)
    {
        public string DurationText => "Playlist";
    }

    public async Task<List<Playlist>> PlaylistsAsync(CancellationToken cancellation = default)
    {
        if (Library != null) return await Library.PlaylistsAsync(cancellation);
        var list = new List<Playlist>();
        string? next = "/me/playlists?limit=50";
        var seen = new HashSet<string>();
        while (next != null && seen.Add(next))
        {
            cancellation.ThrowIfCancellationRequested();
            using var res = await SendAsync(HttpMethod.Get, next);
            Check(res);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cancellation));
            foreach (var p in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                if (Text(p, "id") is not { } playlistId) continue;
                var owner = p.TryGetProperty("owner", out var o) ? Text(o, "display_name") : null;
                list.Add(new Playlist(playlistId, Text(p, "name") ?? "Untitled playlist", owner ?? "Spotify playlist", ImageUrl(p)));
            }
            next = Text(doc.RootElement, "next");
        }
        return list;
    }

    public async Task<List<Track>> PlaylistAsync(string id, CancellationToken cancellation = default)
    {
        if (Library != null) return await Library.PlaylistAsync(id, cancellation);
        if (!Regex.IsMatch(id, "^[A-Za-z0-9]+$")) throw new ArgumentException("Invalid Spotify playlist ID.");
        var list = new List<Track>();
        string? next = $"/playlists/{id}/items?limit=50";
        var seen = new HashSet<string>();
        while (next != null && seen.Add(next))
        {
            cancellation.ThrowIfCancellationRequested();
            using var res = await SendAsync(HttpMethod.Get, next);
            Check(res);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            foreach (var it in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                if (it.ValueKind == JsonValueKind.Object && (it.TryGetProperty("item", out var t) || it.TryGetProperty("track", out t)) && ToTrack(t) is { } track) list.Add(track);
            }
            next = Text(doc.RootElement, "next");
        }
        return list;
    }

    public Task<string?> ResolveAsync(Track t) => Task.FromResult<string?>(null); // played remotely, see PlayAsync

    // ---- account ----
    public record Profile(string Name, string? Plan, string? Country, string? Image);

    public async Task ConnectAsync()
    {
        if (LibrarySession != null) { await LibrarySession.ConnectAsync(); return; }
        if (!Enabled) throw new InvalidOperationException("Add a valid Client ID to config.json and restart GlassPlayer.");
        await tokenGate.WaitAsync();
        try { await LoginAsync(); }
        finally { tokenGate.Release(); }
    }

    public void SignOut()
    {
        if (LibrarySession != null) { LibrarySession.SignOut(); return; }
        sessionVersion++;
        access = null; refresh = null; deviceId = null; UseWeb = false;
        Web?.Disconnect();
        try { File.Delete(Store); } catch { }
    }

    public async Task<Profile?> ProfileAsync()
    {
        if (!SignedIn) return null;
        if (Library != null) return await Library.ProfileAsync();
        using var res = await SendAsync(HttpMethod.Get, "/me");
        Check(res);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var e = doc.RootElement;
        string? Str(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        string? img = e.TryGetProperty("images", out var im) && im.ValueKind == JsonValueKind.Array && im.GetArrayLength() > 0 ? im[0].GetProperty("url").GetString() : null;
        return new Profile(Str("display_name") ?? Str("id") ?? "Spotify user", Str("product"), Str("country"), img);
    }

    // ---- playback (Spotify Connect) ----
    string? deviceId;

    public async Task<string> PlayAsync(string uri, Track? track = null)
    {
        if (Desktop != null) throw new InvalidOperationException("Spotify Free tracks cannot play inside GlassPlayer. Use SoundCloud or local files for standalone playback.");
        // The SDK is created on demand. Never launch or fall back to the Spotify desktop app.
        if (Web != null)
        {
            var dev = await Web.WaitReadyAsync(TimeSpan.FromSeconds(15));
            if (dev != null)
            {
                Web.ClearState();
                for (int i = 0; i < 3; i++)
                {
                    using var r = await SendAsync(HttpMethod.Put, $"/me/player/play?device_id={dev}", new { uris = new[] { uri } });
                    if (r.StatusCode == HttpStatusCode.Forbidden) throw new Exception("Spotify Premium is required for playback.");
                    if (r.IsSuccessStatusCode) { deviceId = dev; UseWeb = true; return "Glass"; }
                    if (r.StatusCode != HttpStatusCode.NotFound) break;
                    await Task.Delay(1000);
                }
            }
        }
        UseWeb = false;

        throw new InvalidOperationException("Spotify’s built-in playback could not start. It requires Premium and a compatible WebView2 runtime. No Spotify desktop app was launched.");
    }

    async Task CmdAsync(string url, bool quiet = false)
    {
        if (deviceId != null) url += (url.Contains('?') ? "&" : "?") + "device_id=" + deviceId;
        using var res = await SendAsync(HttpMethod.Put, url);
        if (!res.IsSuccessStatusCode && res.StatusCode != HttpStatusCode.Forbidden && !quiet)
            throw new Exception($"Spotify error ({(int)res.StatusCode}).");
    }
    public Task PauseAsync() => Desktop != null ? Desktop.PauseAsync() : UseWeb ? Web!.CallAsync("pause()") : CmdAsync("/me/player/pause");
    public Task ResumeAsync() => Desktop != null ? Desktop.ResumeAsync() : UseWeb ? Web!.CallAsync("resume()") : CmdAsync("/me/player/play");
    public Task SeekAsync(int ms) => Desktop != null ? Desktop.SeekAsync(ms) : UseWeb ? Web!.CallAsync($"seek({ms})") : CmdAsync($"/me/player/seek?position_ms={ms}");
    public Task VolumeAsync(int pct) => Desktop != null ? Desktop.VolumeAsync(pct) : UseWeb ? Web!.CallAsync("setVolume(" + (pct / 100.0).ToString("0.00", CultureInfo.InvariantCulture) + ")") : CmdAsync($"/me/player/volume?volume_percent={pct}", quiet: true);

    public async Task<(bool Playing, int Progress, int Duration, string? Uri)?> StateAsync()
    {
        if (Desktop != null)
        {
            if (Desktop.Current is not { } state) return null;
            int progress = state.PositionMs + (state.Playing ? (int)Math.Max(0, (DateTime.UtcNow - state.At).TotalMilliseconds) : 0);
            return (state.Playing, Math.Min(state.DurationMs, progress), state.DurationMs, null);
        }
        if (UseWeb) return Web?.Current();
        using var res = await SendAsync(HttpMethod.Get, "/me/player");
        if (res.StatusCode != HttpStatusCode.OK) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var e = doc.RootElement;
        if (!e.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) return null;
        return (e.GetProperty("is_playing").GetBoolean(), e.GetProperty("progress_ms").GetInt32(),
                item.GetProperty("duration_ms").GetInt32(), item.GetProperty("uri").GetString());
    }
}
