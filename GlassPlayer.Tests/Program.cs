using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlassPlayer;

if (args.Length == 2 && args[0] == "--render-ui")
{
    await UiPreview.RenderAsync(args[1]);
    return 0;
}

if (args.Contains("--soundcloud-metadata-check"))
{
    var results = await new SoundCloud().SearchAsync("Scott Buckley Convergence");
    var imported = await Importer.FromLinkAsync("https://soundcloud.com/scottbuckley/convergence-cc-by");
    Console.WriteLine(JsonSerializer.Serialize(new { searchResults = results.Count, imported = imported.Select(t => new { t.Title, t.Artist, hasArtwork = t.Art != null, t.DurationMs }) }));
    return results.Count > 0 && imported.Count == 1 ? 0 : 1;
}

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

tests.Add(("Spotify desktop selection never launches an app or sends a network command", async () =>
{
    var fake = new FakeHandler((_, _) => throw new Exception("Unexpected HTTP"));
    using var desktop = new SpotifyDesktop();
    var sp = new Spotify(new HttpClient(fake), ClientId, Store("no-launch"), "fixture") { Desktop = desktop };
    await Fails(() => sp.PlayAsync("spotify:track:0123456789ABCDEFGHIJKL"), "standalone");
    Check(fake.Calls == 0 && desktop.Current == null, "A playback connection was started");
}));

tests.Add(("Ad detection requires explicit ad metadata and disabled song controls", () =>
{
    var ad = new SpotifyDesktop.State("Advertisement", "Spotify", null, true, 0, 30000, DateTime.UtcNow, true, true, false, false, false);
    Check(SpotifyAdSilencer.IsAdvertisement(ad), "Explicit ad missed");
    Check(!SpotifyAdSilencer.IsAdvertisement(ad with { Artist = "A real artist" }), "A song title was treated as an ad");
    Check(!SpotifyAdSilencer.IsAdvertisement(ad with { CanSeek = true }), "Seekable music was treated as an ad");
    Check(!SpotifyAdSilencer.IsAdvertisement(ad with { Title = "Want a break from the ads?" }), "Uncertain metadata treated as an ad");
    Check(!SpotifyAdSilencer.IsAdvertisement(null), "Missing metadata treated as an ad");
    return Task.CompletedTask;
}));

tests.Add(("Ad muting restores only its own sessions and never starts on construction", () =>
{
    var audible = new FakeMuteSession("audible");
    var alreadyMuted = new FakeMuteSession("user-muted") { Muted = true };
    int queries = 0;
    using var guard = new SpotifyAdSilencer(() => { queries++; return new[] { audible, alreadyMuted }; });
    Check(queries == 0, "Construction touched audio sessions");
    Check(guard.Apply(true) && audible.Muted && alreadyMuted.Muted, "Ad was not silenced");
    guard.Apply(false);
    Check(!audible.Muted && alreadyMuted.Muted, "Existing user mute was changed");
    guard.Apply(true);
    guard.Dispose();
    Check(!audible.Muted && alreadyMuted.Muted, "Close did not restore mute state");
    return Task.CompletedTask;
}));

tests.Add(("Saved imports retain order, duplicate tracks, artwork and names but no stream tokens", () =>
{
    var library = new SavedLibrary(Path.Combine(temp, "saved.json"));
    var tracks = Enumerable.Range(0, 150).Select(i => new Track(i.ToString(), "Title " + i, "Artist", "https://example.com/cover.png", "temporary-stream-token", "SoundCloud", "https://soundcloud.com/artist/song")).ToList();
    tracks.Add(tracks[0]);
    const string cover = "https://example.com/playlist-cover.png", source = "https://soundcloud.com/artist/sets/original";
    library.Save("My playlist", tracks, cover, source);
    var saved = library.Load().Single();
    Check(saved.Title == "My playlist" && saved.Tracks.Count == 151 && saved.Tracks[149].Title == "Title 149", "Metadata or order lost");
    Check(saved.Tracks.All(t => t.Preview == null && t.Art == tracks[0].Art), "Stream token retained or artwork lost");
    Check(saved.Art == cover && saved.SourceUri == source, "Playlist cover or identity lost");
    library.Save("Renamed playlist", tracks.Take(2), cover, source);
    Check(library.Load().Count == 1 && library.Load()[0].Title == "Renamed playlist" && library.Load()[0].Tracks.Count == 2, "Reimport did not update the same source playlist");
    library.Save("Second playlist", tracks.Take(2), "https://example.com/other-cover.png", "https://soundcloud.com/artist/sets/second");
    Check(library.Load().Count == 2 && library.Load()[1].Title == "Second playlist" && library.Load()[1].Art != cover, "Identical songs collapsed distinct playlists");
    return Task.CompletedTask;
}));

tests.Add(("Legacy saved imports remain readable and unnamed sources save idempotently", () =>
{
    var path = Path.Combine(temp, "legacy.json");
    var tracks = new[] { new Track("1", "Song", "Artist", "https://example.com/song.png", null, "SoundCloud", "https://soundcloud.com/artist/song") };
    File.WriteAllText(path, JsonSerializer.Serialize(new[] { new { Title = "Legacy", Tracks = tracks } }));
    var library = new SavedLibrary(path);
    Check(library.Load().Single().Art == tracks[0].Art, "Legacy artwork fallback lost");
    library.Save("Legacy", tracks);
    Check(library.Load().Count == 1, "Repeated save duplicated a list");
    library.Save("Distinct name", tracks);
    Check(library.Load().Count == 2, "Distinct name lost");
    return Task.CompletedTask;
}));

tests.Add(("Playlist import retains exact title and cover independently of song art", async () =>
{
    var output = JsonSerializer.Serialize(new { title = "Night Drives ✨", thumbnail = "https://example.com/playlist.jpg", entries = new[] {
        new { id = "1", title = "Song", artist = "Artist", thumbnail = "https://example.com/song.jpg", webpage_url = "https://soundcloud.com/artist/song", duration = 42.5 }
    } });
    var imported = Importer.ParsePlaylist(output, true, "https://soundcloud.com/artist/sets/night-drives-123");
    Check(imported.Title == "Night Drives ✨" && imported.CoverArt!.EndsWith("playlist.jpg") && imported.Tracks.Single().Art!.EndsWith("song.jpg"), "Playlist and song metadata conflated");
    await Fails(() => Task.FromResult(Importer.ParsePlaylist("{\"title\":\"Incomplete\",\"entries\":[null]}", true, imported.SourceUri)), "incomplete");
}));

tests.Add(("Damaged saved library is not silently overwritten", async () =>
{
    var path = Path.Combine(temp, "damaged.json");
    File.WriteAllText(path, "{bad json");
    try { new SavedLibrary(path).Save("New", new[] { new Track("1", "A", "B", null, null, "Local", "a.wav") }); }
    catch (JsonException) { }
    Check(File.ReadAllText(path) == "{bad json", "Damaged library overwritten");
    await Fails(() => Task.Run(() => new SavedLibrary(Path.Combine(temp, "invalid-source.json")).Save("Unknown", new[] { new Track("1", "A", "B", null, null, "Unknown", null) })), "Save tracks");
}));

tests.Add(("Spotify playlists save original names, covers and references without audio credentials", () =>
{
    var library = new SavedLibrary(Path.Combine(temp, "references.json"));
    var original = new Track("1", "Song", "Artist", "https://example.com/album.png", "secret", "Spotify", "spotify:track:1", 138000);
    library.Save("Original playlist", new[] { original }, "https://example.com/cover.png", "https://open.spotify.com/playlist/example");
    var saved = library.Load().Single();
    Check(saved.Title == "Original playlist" && saved.Art!.EndsWith("cover.png") && saved.Tracks.Single() == original with { Preview = null }, "Original metadata was replaced");
    return Task.CompletedTask;
}));

tests.Add(("Library sync updates own-account metadata atomically and preserves other imports", async () =>
{
    const string id = "0123456789ABCDEFGHIJKL";
    var saved = new SavedLibrary(Path.Combine(temp, "sync.json"));
    var local = new Track("local", "Local", "Artist", null, null, "Local", "music.wav");
    saved.Save("Local collection", new[] { local });
    var playlists = new List<Spotify.Playlist> { new(id, "Night drive", "Me", "https://example.com/list.png"), new("liked", "Liked Songs", "Me", null) };
    var song = new Track("sp", "Song", "Artist", "https://example.com/song.png", "temporary", "Spotify", "spotify:track:sp", 138000);
    List<Track> tracks = new() { song, song };
    var sync = new LibrarySync(saved, _ => Task.FromResult(playlists), (_, _) => Task.FromResult(tracks));
    var progress = new List<LibrarySyncProgress>();
    var result = await sync.RunAsync(progress.Add);
    var imported = saved.Load().Single(p => p.SourceUri == LibrarySync.SourceUri(id));
    Check(result == new LibrarySyncResult(2, 4) && progress.Count == 2, "Counts or progress lost");
    Check(imported.Title == "Night drive" && imported.Art == playlists[0].Art && imported.Tracks.Count == 2 && imported.Tracks.All(t => t.Preview == null), "Sync metadata/duplicates or credentials wrong");
    Check(saved.Load().Single(p => p.Title == "Local collection").Tracks.Single() == local, "Other imports changed");
    playlists[0] = playlists[0] with { Title = "Renamed", Art = "https://example.com/new.png" };
    tracks = new();
    await sync.RunAsync();
    Check(saved.Load().Count == 3 && saved.Load().Single(p => p.SourceUri == LibrarySync.SourceUri(id)).Tracks.Count == 0, "Empty remote playlist or repeat sync not applied");
    Check(saved.Load().Single(p => p.SourceUri == LibrarySync.SourceUri(id)).Title == "Renamed", "Renamed playlist duplicated");
}));

tests.Add(("Failed or cancelled library sync preserves the entire saved file", async () =>
{
    var path = Path.Combine(temp, "sync-rollback.json");
    var saved = new SavedLibrary(path);
    saved.Save("Keep", new[] { new Track("keep", "Song", "Artist", null, null, "Local", "music.wav") });
    var before = File.ReadAllText(path);
    var lists = new List<Spotify.Playlist> { new("0123456789ABCDEFGHIJKL", "First", "Me", null), new("ABCDEFGHIJKL0123456789", "Second", "Me", null) };
    int calls = 0;
    var sync = new LibrarySync(saved, _ => Task.FromResult(lists), (_, _) => ++calls == 2
        ? Task.FromException<List<Track>>(new Exception("offline")) : Task.FromResult(new List<Track>()));
    await Fails(() => sync.RunAsync(), "offline");
    Check(File.ReadAllText(path) == before, "Partial sync changed saved playlists");
    using var cancellation = new CancellationTokenSource();
    sync = new LibrarySync(saved, _ => Task.FromResult(lists), (_, _) => { cancellation.Cancel(); return Task.FromResult(new List<Track>()); });
    try { await sync.RunAsync(cancellation: cancellation.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
    Check(File.ReadAllText(path) == before, "Cancellation changed saved playlists");
}));

tests.Add(("Sync rejects repeated playlists and never starts for a pre-cancelled request", async () =>
{
    var saved = new SavedLibrary(Path.Combine(temp, "sync-duplicates.json"));
    var playlist = new Spotify.Playlist("0123456789ABCDEFGHIJKL", "Repeated", "Me", null);
    int calls = 0;
    var sync = new LibrarySync(saved, _ => { calls++; return Task.FromResult(new List<Spotify.Playlist> { playlist, playlist }); }, (_, _) => Task.FromResult(new List<Track>()));
    await Fails(() => sync.RunAsync(), "repeated");
    Check(saved.Load().Count == 0, "Repeated response was saved");
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    try { await sync.RunAsync(cancellation: cancellation.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
    Check(calls == 1, "Cancelled sync still contacted the provider");
}));

tests.Add(("Matching requires artist, title, version and duration and rejects ambiguity", () =>
{
    var original = new Track("sp", "MONTERO (Call Me By Your Name)", "Lil Nas X", null, null, "Spotify", null, 137922);
    var match = original with { Id = "yt", Source = "YouTube Music", Title = "montero (Call Me By Your Name)", DurationMs = 138000 };
    Check(PlaybackResolver.FindMatch(original, new[] { match, match }) == match, "Exact match or repeated ID rejected");
    foreach (var wrong in new[] { match with { Title = match.Title + " (Remix)" }, match with { Title = match.Title + " (Clean)" },
        match with { Artist = "Someone else" }, match with { DurationMs = 144000 }, match with { DurationMs = 0 }, match with { Source = "SoundCloud" } })
        Check(PlaybackResolver.FindMatch(original, new[] { wrong }) == null, "Uncertain recording chosen");
    Check(PlaybackResolver.FindMatch(original, new[] { match, match with { Id = "second" } }) == null, "Ambiguous recording chosen");
    var catalog = match with { CatalogRecording = true };
    Check(PlaybackResolver.FindMatch(original, new[] { catalog, catalog with { Id = "album-copy" } }) == catalog, "Identical catalog copies rejected");
    Check(PlaybackResolver.FindMatch(original, new[] { catalog, catalog with { Id = "different-cut", DurationMs = 139000 } }) == null, "Conflicting catalog durations accepted");
    Check(PlaybackResolver.FindMatch(original with { DurationMs = 0 }, new[] { match }) == null, "Unknown source duration matched");
    return Task.CompletedTask;
}));

tests.Add(("Matching accepts translated catalog titles, accents and all collaborators without losing versions", () =>
{
    var original = new Track("sp", "Shinzo wo Sasageyo !", "Linked Horizon", null, null, "Spotify", null, 341000);
    var song = original with { Source = "YouTube Music", Id = "yt", Title = "心臓を捧げよ！ - Shinzo wo Sasageyo !", DurationMs = 342000 };
    Check(PlaybackResolver.FindMatch(original, new[] { song }) == song, "Translated prefix rejected");
    Check(PlaybackResolver.FindMatch(original, new[] { song with { Title = "心臓を捧げよ！ [TV Size] - Shinzo wo Sasageyo !" } }) == null, "Version suffix discarded");
    original = original with { Title = "Vogel Im Kafig", Artist = "Sawano Hiroyuki", DurationMs = 380000 };
    song = song with { Title = "Vogel im Käfig", Artist = "Hiroyuki Sawano", DurationMs = 380000 };
    Check(PlaybackResolver.FindMatch(original, new[] { song }) == song, "Accents or artist name order rejected");
    original = original with { Title = "Escapism.", Artist = "RAYE, 070 Shake", DurationMs = 271000 };
    song = song with { Title = "Escapism.", Artist = "070 Shake, RAYE", DurationMs = 273000 };
    Check(PlaybackResolver.FindMatch(original, new[] { song }) == song, "Collaborator order rejected");
    Check(PlaybackResolver.FindMatch(original, new[] { song with { Artist = "RAYE" } }) == null, "Missing collaborator accepted");
    return Task.CompletedTask;
}));

tests.Add(("YouTube metadata keeps multiple artists and excludes the album from artist names", () =>
{
    object Run(string text, string type) => new { text, navigationEndpoint = new { browseEndpoint = new { browseEndpointContextSupportedConfigs = new { browseEndpointContextMusicConfig = new { pageType = type } } } } };
    using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { playlistItemData = new { videoId = "fixture" }, flexColumns = new object[] {
        new { musicResponsiveListItemFlexColumnRenderer = new { text = new { runs = new object[] { new { text = "Escapism." } } } } },
        new { musicResponsiveListItemFlexColumnRenderer = new { text = new { runs = new object[] { Run("RAYE", "MUSIC_PAGE_TYPE_ARTIST"), new { text = " & " }, Run("070 Shake", "MUSIC_PAGE_TYPE_ARTIST"), Run("Album", "MUSIC_PAGE_TYPE_ALBUM"), new { text = "4:33" } } } } }
    } }));
    var track = YouTubeMusic.ParseItem(doc.RootElement);
    Check(track?.Artist == "RAYE, 070 Shake" && track.DurationMs == 273000, "Artist or duration parsing failed");
    return Task.CompletedTask;
}));

tests.Add(("SoundCloud previews use full-duration metadata and never play the excerpt", async () =>
{
    var original = new Track("256195283", "She Looks So Perfect", "5 Seconds of Summer", null, null, "SoundCloud", null, 30000);
    using var json = JsonDocument.Parse("""{"id":256195283,"duration":30000,"full_duration":202971,"policy":"SNIP"}""");
    original = SoundCloud.ApplyMetadata(original, json.RootElement);
    Check(original.IsPreview && original.DurationMs == 202971 && original.DurationText == "Preview", "Preview classified as full song");
    var native = new PlaybackFake { Name = "SoundCloud" };
    var youtube = new PlaybackFake { Results = new() { original with { Source = "YouTube Music", Id = "yt", IsPreview = false, DurationMs = 203000 } } };
    var resolver = new PlaybackResolver(new Dictionary<string, IProvider> { ["SoundCloud"] = native, ["YouTube Music"] = youtube });
    var result = await resolver.ResolveAsync(original, true);
    Check(result.Source == "YouTube Music" && native.Resolves == 0, "Preview resolved as playable native audio");
    await Fails(() => resolver.ResolveAsync(original, false), "preview");
}));

tests.Add(("Recent caches stay bounded, expire, and can be cleared on sign-out", () =>
{
    var cache = new RecentCache<int, string>(2, TimeSpan.FromMinutes(1));
    cache.Set(1, "one"); cache.Set(2, "two"); cache.Set(3, "three");
    Check(cache.Count == 2 && !cache.TryGet(1, out _) && cache.TryGet(3, out _), "Cache grew past bound");
    cache.Clear(); Check(cache.Count == 0, "Sign-out cache clear failed");
    cache = new(2, TimeSpan.Zero); cache.Set(1, "expired");
    Check(!cache.TryGet(1, out _), "Expired provider address reused");
    return Task.CompletedTask;
}));

tests.Add(("SoundCloud artwork requests a small variant without changing unrelated addresses", () =>
{
    var original = "https://i1.sndcdn.com/artworks-example-original.jpg";
    Check(ArtworkConverter.SizedAddress(original, 96) == "https://i1.sndcdn.com/artworks-example-large.jpg", "Thumbnail fetched original art");
    Check(ArtworkConverter.SizedAddress(original, 512) == "https://i1.sndcdn.com/artworks-example-t500x500.jpg", "Offline cover fetched oversized art");
    const string unrelated = "https://example.com/cover-original.jpg";
    Check(ArtworkConverter.SizedAddress(unrelated, 96) == unrelated, "Unrelated address modified");
    return Task.CompletedTask;
}));

tests.Add(("Downloaded songs persist locally and resolve without a network provider", async () =>
{
    var folder = Path.Combine(temp, "offline");
    var source = Path.Combine(temp, "audio.m4a"); File.WriteAllBytes(source, new byte[] { 1, 2, 3, 4 });
    var original = new Track("id", "A / song", "Artist", null, "temporary-token", "YouTube Music", null, 180000);
    var offline = new OfflineLibrary(folder);
    await offline.StoreAsync(original, "YouTube Music", source, CancellationToken.None);
    var local = offline.Tracks().Single();
    Check(local.Source == "Local" && local.Preview == null && File.Exists(local.OpenUri), "Offline file or metadata lost");
    var resolver = new PlaybackResolver(new Dictionary<string, IProvider>(), offline);
    Check((await resolver.ResolveAsync(original, false)).Location == local.OpenUri, "Offline playback needs provider");
    var before = File.ReadAllText(Path.Combine(folder, "library.json"));
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    try { await offline.StoreAsync(original, "Other", source, cancelled.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
    Check(File.ReadAllText(Path.Combine(folder, "library.json")) == before, "Cancelled save changed offline library");
}));

tests.Add(("Cancelled stream requests never start helper processes", async () =>
{
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    try { await Dlp.StreamAsync("https://example.com", "cancelled", cancellation.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
    try { await Dlp.RunAsync("--version", cancellation: cancellation.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
}));

tests.Add(("Spotify first-page songs are usable while later pages are still loading", async () =>
{
    bool firstPageShown = false;
    var fake = new FakeHandler((_, call) =>
    {
        if (call == 2) Check(firstPageShown, "Waited for full playlist before showing songs");
        return Task.FromResult(Json(JsonSerializer.Serialize(new { data = new { playlistV2 = new { content = new { totalCount = 100,
            items = Enumerable.Range(0, 50).Select(i => new { itemV2 = new { data = WebTrack(i) } }) } } } })));
    });
    var tracks = await WebLibrary(fake).PlaylistPreviewAsync("0123456789ABCDEFGHIJKL", CancellationToken.None, first => { Check(first.Count == 50, "Wrong first-page count"); firstPageShown = true; });
    Check(firstPageShown && tracks.Count == 100, "Final playlist incomplete");
}));

tests.Add(("Spotify matching never invokes Spotify playback and retains original metadata", async () =>
{
    var original = new Track("sp", "Song", "Artist", "https://example.com/original.png", null, "Spotify", "spotify:track:sp", 180000);
    var candidate = original with { Id = "yt", Source = "YouTube Music", Art = "https://example.com/other.png", DurationMs = 181000 };
    var youtube = new PlaybackFake { Results = new() { candidate } };
    // Deliberately no Spotify provider: matching must not require or start it.
    var resolver = new PlaybackResolver(new Dictionary<string, IProvider> { ["YouTube Music"] = youtube });
    var result = await resolver.ResolveAsync(original, true);
    Check(result.Source == "YouTube Music" && result.AudioTrack == candidate && result.IsMatchFor(original), "Source provenance missing");
    Check(original.Source == "Spotify" && original.Art!.EndsWith("original.png"), "Original metadata changed");
    await resolver.ResolveAsync(original, true);
    Check(youtube.Searches == 1 && youtube.Resolves == 2, "Successful match not reused");
}));

tests.Add(("SoundCloud keeps native playback and falls back only when unavailable", async () =>
{
    var original = new Track("sc", "Song", "Artist", null, null, "SoundCloud", null, 180000);
    var soundcloud = new PlaybackFake { Name = "SoundCloud" };
    var youtube = new PlaybackFake { Results = new() { original with { Id = "yt", Source = "YouTube Music" } } };
    var resolver = new PlaybackResolver(new Dictionary<string, IProvider> { ["SoundCloud"] = soundcloud, ["YouTube Music"] = youtube });
    Check((await resolver.ResolveAsync(original, true)).Source == "SoundCloud" && youtube.Searches == 0, "Native source unnecessarily replaced");
    soundcloud.Error = new Exception("Protected recording");
    Check((await resolver.ResolveAsync(original, true)).Source == "YouTube Music", "Unavailable source did not match");
    await resolver.ResolveAsync(original, true);
    Check(soundcloud.Resolves == 2 && youtube.Searches == 1, "Replay retried a known unavailable source");
}));

tests.Add(("Disabled matching and cancelled selections never start fallback searches", async () =>
{
    var original = new Track("sc", "Song", "Artist", null, null, "SoundCloud", null, 180000);
    var soundcloud = new PlaybackFake { Error = new Exception("Protected recording") };
    var youtube = new PlaybackFake();
    var resolver = new PlaybackResolver(new Dictionary<string, IProvider> { ["SoundCloud"] = soundcloud, ["YouTube Music"] = youtube });
    await Fails(() => resolver.ResolveAsync(original, false), "Protected");
    await Fails(() => resolver.ResolveAsync(original with { Source = "Spotify" }, false), "Enable matching");
    using var cancellation = new CancellationTokenSource();
    var pending = new TaskCompletionSource<string?>();
    soundcloud.Pending = pending.Task;
    var resolution = resolver.ResolveAsync(original, true, cancellation.Token);
    cancellation.Cancel();
    try { await resolution; throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
    pending.SetResult(null);
    Check(youtube.Searches == 0, "Disabled/cancelled request started fallback");
}));

tests.Add(("Failed or ambiguous YouTube matches do not become cached playback", async () =>
{
    var original = new Track("sp", "Song", "Artist", null, null, "Spotify", null, 180000);
    var candidate = original with { Id = "yt", Source = "YouTube Music" };
    var youtube = new PlaybackFake { Results = new() { candidate, candidate with { Id = "other" } } };
    var resolver = new PlaybackResolver(new Dictionary<string, IProvider> { ["YouTube Music"] = youtube });
    await Fails(() => resolver.ResolveAsync(original, true), "No single close");
    Check(youtube.Resolves == 0, "Ambiguous audio downloaded");
    youtube.Results = new() { candidate };
    youtube.Error = new Exception("Download failed");
    await Fails(() => resolver.ResolveAsync(original, true), "Download failed");
    youtube.Error = null;
    await resolver.ResolveAsync(original, true);
    Check(youtube.Searches == 3, "Failed match was retained");
}));

tests.Add(("Protected recordings retain metadata for later matching", () =>
{
    var imported = Importer.ParsePlaylist("""{"id":101,"title":"Song","artist":"Artist","duration":138,"formats":[],"webpage_url":"https://soundcloud.com/artist/song"}""", true, "https://soundcloud.com/artist/song");
    Check(imported.Tracks.Single().DurationMs == 138000 && imported.Tracks[0].Preview == null, "Protected metadata lost or treated as audio");
    return Task.CompletedTask;
}));

tests.Add(("Rapid repeated selections share one download and failures can retry", async () =>
{
    int downloads = 0;
    var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    Task<string> Download() { downloads++; return pending.Task; }
    var first = Dlp.ShareDownloadAsync("fixture-shared", Download);
    var second = Dlp.ShareDownloadAsync("fixture-shared", Download);
    Check(downloads == 1, "Concurrent downloads wrote the same cache file");
    pending.SetResult("audio.m4a");
    Check((await Task.WhenAll(first, second)).All(p => p == "audio.m4a"), "Shared download result lost");
    await Fails(() => Dlp.ShareDownloadAsync("fixture-retry", () => Task.FromException<string>(new Exception("offline"))), "offline");
    Check(await Dlp.ShareDownloadAsync("fixture-retry", () => Task.FromResult("recovered.m4a")) == "recovered.m4a", "Failed download prevented retry");
}));

tests.Add(("Playlist import parses more than 100 tracks with original artist and artwork", () =>
{
    var output = string.Join('\n', Enumerable.Range(0, 175).Select(i => JsonSerializer.Serialize(new { id = i, title = "Song " + i, artist = "Actual artist", uploader = "Uploader", thumbnail = "https://example.com/art.png", webpage_url = "https://soundcloud.com/artist/song", duration = 123.5 })));
    var tracks = Importer.ParseOutput(output, true);
    Check(tracks.Count == 175 && tracks[^1].Title == "Song 174", "Playlist truncated");
    Check(tracks.All(t => t.Artist == "Actual artist" && t.DurationMs == 123500 && t.Art != null), "Metadata lost");
    return Task.CompletedTask;
}));

tests.Add(("Import links reject lookalike domains and non-HTTPS schemes", async () =>
{
    Check(Importer.SourceFromLink("https://soundcloud.com/artist/sets/list") == "SoundCloud", "Valid playlist rejected");
    Check(Importer.SourceFromLink("https://on.soundcloud.com/example") == "SoundCloud", "Short link rejected");
    foreach (var link in new[] { "https://evil.test/soundcloud.com", "https://soundcloud.com.evil.test/a", "file:///soundcloud.com", "http://soundcloud.com/a", "https://user@soundcloud.com/a" })
        await Fails(() => Task.FromResult(Importer.SourceFromLink(link)), "Paste");
}));

tests.Add(("Offscreen UI stays standalone when selecting Spotify references", () => UiPreview.RenderAsync(Path.Combine(temp, "ui"))));

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

sealed class FakeMuteSession(string id) : IMuteSession
{
    public string Id => id;
    public bool Muted { get; set; }
    public void Dispose() { }
}

sealed class PlaybackFake : IProvider
{
    public string Name { get; set; } = "YouTube Music";
    public bool Enabled => true;
    public List<Track> Results { get; set; } = new();
    public Exception? Error { get; set; }
    public Task<string?>? Pending { get; set; }
    public int Searches { get; private set; }
    public int Resolves { get; private set; }
    public Task<List<Track>> SearchAsync(string query) { Searches++; return Task.FromResult(Results); }
    public Task<string?> ResolveAsync(Track track) { Resolves++; return Pending ?? (Error != null ? Task.FromException<string?>(Error) : Task.FromResult<string?>("fixture.m4a")); }
}
