using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlassPlayer;

if (args.Contains("--live-library-check"))
{
    var library = new SpotifyWebLibrary(new SpotifyWebSession());
    var lists = await library.PlaylistsAsync();
    var sample = lists.First(p => p.Id != "liked");
    var songs = await library.PlaylistAsync(sample.Id);
    Console.WriteLine(JsonSerializer.Serialize(new { playlists = lists.Count, tracks = songs.Select(t => new { t.Title, t.OpenUri }) }));
    return 0;
}

const string ClientId = "0123456789abcdef0123456789abcdef";
var tests = new List<(string Name, Func<Task> Run)>();
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task Fails(Func<Task> action, string message)
{
    try { await action(); }
    catch (Exception ex) when (ex.Message.Contains(message, StringComparison.OrdinalIgnoreCase)) { return; }
    throw new Exception("Expected error containing: " + message);
}
HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
string TrackJson(string id = "song") => JsonSerializer.Serialize(new { id, name = "Test track", uri = "spotify:track:" + id, artists = new[] { new { name = "Artist" } }, album = new { images = Array.Empty<object>() }, duration_ms = 1000 });
var temp = Path.Combine(Path.GetTempPath(), "GlassPlayer.Tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
string Store(string name) => Path.Combine(temp, name + ".bin");
void SaveRefresh(string path) => File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes("fixture-refresh"), null, DataProtectionScope.CurrentUser));

tests.Add(("Playlist library paginates and accepts missing images/owner", async () =>
{
    var fake = new FakeHandler((req, count) => Task.FromResult(count == 1
        ? Json("""{"items":[{"id":"one","name":"First","images":null}],"next":"https://api.spotify.com/v1/me/playlists?offset=50"}""")
        : Json("""{"items":[null,{"id":"two","name":"Second","images":[],"owner":{"display_name":"Owner"}}],"next":null}""")));
    var sp = new Spotify(new HttpClient(fake), ClientId, Store("pages"), "fixture-access");
    var list = await sp.PlaylistsAsync();
    Check(list.Count == 2 && list[1].Artist == "Owner" && fake.Calls == 2, "Pagination failed");
}));
tests.Add(("Playlist items accepts modern and legacy wrappers and skips unavailable entries", async () =>
{
    var fake = new FakeHandler((req, _) =>
    {
        Check(req.RequestUri!.AbsolutePath.EndsWith("/items"), "Old playlist endpoint used");
        return Task.FromResult(Json("{\"items\":[null,{\"item\":null},{\"item\":" + TrackJson("one") + "},{\"track\":" + TrackJson("two") + "},{\"item\":{\"id\":\"podcast\",\"uri\":\"spotify:episode:podcast\"}}],\"next\":null}"));
    });
    var list = await new Spotify(new HttpClient(fake), ClientId, Store("items"), "fixture-access").PlaylistAsync("playlist");
    Check(list.Count == 2 && list[1].Id == "two", "Item parsing failed");
}));
tests.Add(("Long playlists are not truncated at 300 tracks", async () =>
{
    var fake = new FakeHandler((_, count) => Task.FromResult(Json(JsonSerializer.Serialize(new
    {
        items = Enumerable.Range(0, 50).Select(i => new { item = JsonSerializer.Deserialize<JsonElement>(TrackJson($"s{count}n{i}")) }),
        next = count < 7 ? $"https://api.spotify.com/v1/playlists/p/items?offset={count * 50}" : null
    }))));
    var tracks = await new Spotify(new HttpClient(fake), ClientId, Store("long"), "fixture-access").PlaylistAsync("p");
    Check(tracks.Count == 350, "Truncated playlist");
}));
tests.Add(("Forbidden API response explains Premium requirement", async () =>
{
    var fake = new FakeHandler((_, _) => Task.FromResult(Json("{}", HttpStatusCode.Forbidden)));
    await Fails(() => new Spotify(new HttpClient(fake), ClientId, Store("403"), "fixture-access").PlaylistsAsync(), "Premium");
}));
tests.Add(("Quota response backs off without repeat requests", async () =>
{
    var fake = new FakeHandler((_, _) => Task.FromResult(Json("""{"error":{"reason":"QUOTA_EXCEEDED"}}""", HttpStatusCode.TooManyRequests)));
    var sp = new Spotify(new HttpClient(fake), ClientId, Store("quota"), "fixture-access");
    await Fails(() => sp.PlaylistsAsync(), "quota");
    await Fails(() => sp.PlaylistsAsync(), "quota");
    Check(fake.Calls == 1, "Quota caused a request loop");
}));
tests.Add(("Rate limit honors Retry-After", async () =>
{
    var fake = new FakeHandler((_, _) => { var res = Json("{}", HttpStatusCode.TooManyRequests); res.Headers.RetryAfter = new(TimeSpan.FromSeconds(30)); return Task.FromResult(res); });
    var sp = new Spotify(new HttpClient(fake), ClientId, Store("rate"), "fixture-access");
    await Fails(() => sp.SearchAsync("test"), "limiting");
    await Fails(() => sp.SearchAsync("test"), "limiting");
    Check(fake.Calls == 1, "Rate limit caused a request loop");
}));
tests.Add(("Transient refresh failure preserves authorization", async () =>
{
    var store = Store("transient"); SaveRefresh(store);
    var fake = new FakeHandler((_, _) => Task.FromResult(Json("{}", HttpStatusCode.ServiceUnavailable)));
    var sp = new Spotify(new HttpClient(fake), ClientId, store);
    await Fails(() => sp.GetTokenAsync(), "503");
    Check(File.Exists(store) && sp.SignedIn, "Transient error removed refresh token");
}));
tests.Add(("Expired API access refreshes once and retries", async () =>
{
    var store = Store("refresh"); SaveRefresh(store);
    var fake = new FakeHandler((req, count) =>
    {
        if (count == 1) return Task.FromResult(Json("{}", HttpStatusCode.Unauthorized));
        if (count == 2) { Check(req.RequestUri!.Host == "accounts.spotify.com", "Wrong token host"); return Task.FromResult(Json("""{"access_token":"new-fixture","expires_in":3600}""")); }
        Check(req.Headers.Authorization?.Parameter == "new-fixture", "Retry did not use new token");
        return Task.FromResult(Json("""{"items":[],"next":null}"""));
    });
    await new Spotify(new HttpClient(fake), ClientId, store, "expired-fixture").PlaylistsAsync();
    Check(fake.Calls == 3, "Unexpected refresh count");
}));
tests.Add(("Concurrent token callers share one refresh", async () =>
{
    var store = Store("concurrent"); SaveRefresh(store);
    var fake = new FakeHandler(async (_, _) => { await Task.Delay(25); return Json("""{"access_token":"new-fixture","expires_in":3600}"""); });
    var sp = new Spotify(new HttpClient(fake), ClientId, store);
    await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => sp.GetTokenAsync()));
    Check(fake.Calls == 1, "Duplicate refresh requests");
}));
tests.Add(("Revoked authorization requires explicit reconnect", async () =>
{
    var store = Store("revoked"); SaveRefresh(store);
    var fake = new FakeHandler((_, _) => Task.FromResult(Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest)));
    var sp = new Spotify(new HttpClient(fake), ClientId, store);
    await Fails(() => sp.GetTokenAsync(), "revoked");
    Check(!File.Exists(store) && !sp.SignedIn, "Revoked token not cleared");
}));
tests.Add(("Foreign pagination URLs never receive authorization", async () =>
{
    var fake = new FakeHandler((_, _) => Task.FromResult(Json("""{"items":[],"next":"https://example.com/stolen"}""")));
    await Fails(() => new Spotify(new HttpClient(fake), ClientId, Store("foreign"), "fixture-access").PlaylistsAsync(), "invalid page");
    Check(fake.Calls == 1, "Token sent to foreign host");
}));
tests.Add(("OAuth state rejects missing or mismatched values", () =>
{
    Check(!Spotify.ValidState("expected", null) && !Spotify.ValidState("expected", "other") && Spotify.ValidState("expected", "expected"), "Invalid state accepted");
    Check(Spotify.Scopes.Contains("playlist-read-private") && Spotify.Scopes.Contains("playlist-read-collaborative"), "Playlist scopes missing");
    return Task.CompletedTask;
}));
tests.Add(("No saved authorization does not open browser implicitly", async () =>
{
    var fake = new FakeHandler((_, _) => throw new Exception("Unexpected HTTP"));
    await Fails(() => new Spotify(new HttpClient(fake), ClientId, Store("unsigned")).PlaylistsAsync(), "Connect Spotify");
    Check(fake.Calls == 0, "Unauthenticated API request");
}));
tests.Add(("Malformed Client ID fails before network", async () =>
{
    var fake = new FakeHandler((_, _) => throw new Exception("Unexpected HTTP"));
    await Fails(() => new Spotify(new HttpClient(fake), "bad", Store("bad")).GetTokenAsync(), "valid Spotify Client ID");
}));

tests.Add(("Desktop session selection excludes unrelated players", () =>
{
    Check(SpotifyDesktop.IsSpotifySession("Spotify.exe"), "Desktop Spotify missing");
    Check(SpotifyDesktop.IsSpotifySession("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"), "Store Spotify missing");
    Check(!SpotifyDesktop.IsSpotifySession("msedge.exe") && !SpotifyDesktop.IsSpotifySession("FakeSpotify.exe"), "Unrelated media session selected");
    return Task.CompletedTask;
}));

tests.Add(("Desktop playback accepts only Spotify entity URIs", async () =>
{
    Check(SpotifyDesktop.ValidateUri("spotify:track:0123456789ABCDEFGHIJKL") != "", "Valid URI rejected");
    await Fails(() => Task.FromResult(SpotifyDesktop.ValidateUri("https://example.com")), "Spotify");
    await Fails(() => Task.FromResult(SpotifyDesktop.ValidateUri("spotify:track:0123456789ABCDEFGHIJKL --argument")), "Spotify");
}));

tests.Add(("Library browser capture is restricted to exact Spotify API hosts", () =>
{
    Check(SpotifyWebSession.IsSessionRequest(new Uri("https://api-partner.spotify.com/pathfinder/v2/query")), "Expected host rejected");
    foreach (var uri in new[] { "https://api.spotify.com.evil.test", "http://api.spotify.com", "https://api.spotify.com:8443", "https://user@api.spotify.com" })
        Check(!SpotifyWebSession.IsSessionRequest(new Uri(uri)), "Unsafe capture origin accepted");
    return Task.CompletedTask;
}));

SpotifyWebLibrary WebLibrary(FakeHandler fake, Action? invalidated = null) => new(new HttpClient(fake),
    () => Task.FromResult(new SpotifyWebSession.Credentials("fixture-access", "fixture-client", "GlassPlayer-tests", DateTime.UtcNow.AddMinutes(30))), invalidated ?? (() => { }));
object WebTrack(int i) => new { uri = "spotify:track:" + i.ToString("D22"), name = "Track " + i,
    artists = new { items = new[] { new { profile = new { name = "Artist" } } } }, trackDuration = new { totalMilliseconds = 123000 } };

tests.Add(("Web library paginates past 100 entries and ignores non-playlists", async () =>
{
    var fake = new FakeHandler(async (req, call) =>
    {
        using var body = JsonDocument.Parse(await req.Content!.ReadAsStringAsync());
        Check(req.RequestUri!.Host == "api-partner.spotify.com", "Wrong destination");
        Check(body.RootElement.GetProperty("variables").GetProperty("offset").GetInt32() == (call - 1) * 100, "Incorrect library offset");
        return Json(JsonSerializer.Serialize(new { data = new { me = new { libraryV3 = new { totalCount = 105,
            items = Enumerable.Range((call - 1) * 100, call == 1 ? 100 : 5).Select(i => new { item = new { uri = "spotify:playlist:" + i.ToString("D22"), data = new { name = "Playlist " + i } } }) } } } }));
    });
    var playlists = await WebLibrary(fake).PlaylistsAsync();
    Check(playlists.Count == 106 && playlists[0].Id == "liked" && fake.Calls == 2, "Library was truncated");
}));

tests.Add(("Web playlist loads all pages and preserves intentional duplicates", async () =>
{
    var fake = new FakeHandler(async (req, call) =>
    {
        using var body = JsonDocument.Parse(await req.Content!.ReadAsStringAsync());
        Check(body.RootElement.GetProperty("variables").GetProperty("offset").GetInt32() == (call - 1) * 50, "Incorrect track offset");
        return Json(JsonSerializer.Serialize(new { data = new { playlistV2 = new { content = new { totalCount = 350,
            items = Enumerable.Range(0, 50).Select(_ => new { itemV2 = new { data = WebTrack(1) } }) } } } }));
    });
    var tracks = await WebLibrary(fake).PlaylistAsync("0123456789ABCDEFGHIJKL");
    Check(tracks.Count == 350 && fake.Calls == 7 && tracks[0].Artist == "Artist", "Playlist truncated or duplicate deleted");
}));

tests.Add(("Web library rejects changed schema rather than showing an empty library", async () =>
{
    await Fails(() => WebLibrary(new FakeHandler((_, _) => Task.FromResult(Json("{\"data\":{}}")))).PlaylistsAsync(), "unfamiliar");
    await Fails(() => WebLibrary(new FakeHandler((_, _) => Task.FromResult(Json("{\"errors\":[{\"message\":\"PersistedQueryNotFound\"}]}")))).PlaylistsAsync(), "changed");
}));

tests.Add(("Web library authorization retries once and rate limits back off", async () =>
{
    int invalidations = 0;
    var fake = new FakeHandler((_, call) => Task.FromResult(call == 1 ? Json("{}", HttpStatusCode.Unauthorized) : Json("{\"data\":{\"me\":{\"libraryV3\":{\"items\":[]}}}}")));
    await WebLibrary(fake, () => invalidations++).PlaylistsAsync();
    Check(invalidations == 1 && fake.Calls == 2, "Wrong authorization retry behavior");
    var limited = new FakeHandler((_, _) => Task.FromResult(Json("{}", HttpStatusCode.TooManyRequests)));
    var library = WebLibrary(limited);
    await Fails(() => library.PlaylistsAsync(), "limiting");
    await Fails(() => library.PlaylistsAsync(), "limiting");
    Check(limited.Calls == 1, "Rate-limit request loop");
}));

tests.Add(("Web track parser excludes episodes and unavailable recordings", () =>
{
    using var episode = JsonDocument.Parse("{\"uri\":\"spotify:episode:0123456789ABCDEFGHIJKL\",\"name\":\"Episode\"}");
    Check(SpotifyWebLibrary.ParseTrack(episode.RootElement) == null, "Episode treated as music");
    using var unavailable = JsonDocument.Parse("{\"uri\":\"spotify:track:0123456789ABCDEFGHIJKL\",\"playability\":{\"playable\":false}}");
    Check(SpotifyWebLibrary.ParseTrack(unavailable.RootElement) == null, "Unavailable track included");
    return Task.CompletedTask;
}));

if (!args.Contains("--ci"))
{
    tests.Add(("Windows media session manager initializes with concurrent callers", async () =>
    {
        using var desktop = new SpotifyDesktop();
        await Task.WhenAll(desktop.StartAsync(), desktop.StartAsync());
    }));
    tests.Add(("Native audio decodes, pauses, seeks and resumes", AudioSmoke.RunAsync));
}
else Console.WriteLine("SKIP 2 interactive Windows media/audio checks; run without --ci on a desktop to verify them.");

int failed = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + test.Name + ": " + ex.Message); }
}
// Only this test-owned temporary directory is removed.
Directory.Delete(temp, true);
Console.WriteLine($"{tests.Count - failed}/{tests.Count} tests passed.");
return failed == 0 ? 0 : 1;

sealed class FakeHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    int calls;
    public int Calls => calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, Interlocked.Increment(ref calls));
}
