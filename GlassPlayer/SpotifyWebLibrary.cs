using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GlassPlayer;

// Read-only library requests, based on the same web protocol used by Stopify.
// These unofficial query identifiers can change; failures are surfaced, never replaced with other audio.
sealed class SpotifyWebLibrary
{
    readonly HttpClient http;
    readonly Func<Task<SpotifyWebSession.Credentials>> credentials;
    readonly Action invalidate;
    DateTime retryAfter;
    static readonly HttpClient SharedHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };
    internal const string LibraryHash = "973e511ca44261fda7eebac8b653155e7caee3675abb4fb110cc1b8c78b091c3";
    internal const string PlaylistHash = "8964e8eafb21aa992a7d951d256d83285c04be2105d209262901de70cb97584a";
    internal const string ProfileHash = "53bcb064f6cd18c23f752bc324a791194d20df612d8e1239c735144ab0399ced";
    internal const string LikedHash = "087278b20b743578a6262c2b0b4bcd20d879c503cc359a2285baf083ef944240";
    internal const string SearchHash = "dd78eaff943eba629ed70ee25517b9cea0dcaa41193e2592ae2727660b21892c";

    public SpotifyWebLibrary(SpotifyWebSession session) : this(SharedHttp, session.GetAsync, session.Invalidate) { }
    internal SpotifyWebLibrary(HttpClient http, Func<Task<SpotifyWebSession.Credentials>> credentials, Action invalidate)
    { this.http = http; this.credentials = credentials; this.invalidate = invalidate; }

    async Task<JsonDocument> Query(string name, string hash, object variables, CancellationToken cancellation = default)
    {
        if (DateTime.UtcNow < retryAfter) throw new InvalidOperationException($"Spotify is limiting library requests. Try again after {retryAfter.ToLocalTime():t}.");
        for (int attempt = 0; ; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            var auth = await credentials();
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api-partner.spotify.com/pathfinder/v2/query");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Access);
            request.Headers.TryAddWithoutValidation("client-token", auth.ClientToken);
            request.Headers.TryAddWithoutValidation("User-Agent", auth.UserAgent);
            request.Headers.TryAddWithoutValidation("app-platform", "WebPlayer");
            request.Headers.TryAddWithoutValidation("Origin", "https://open.spotify.com");
            request.Headers.Referrer = new Uri("https://open.spotify.com/");
            request.Content = new StringContent(JsonSerializer.Serialize(new { operationName = name, variables,
                extensions = new { persistedQuery = new { version = 1, sha256Hash = hash } } }), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, cancellation);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { invalidate(); continue; }
            if ((int)response.StatusCode == 429)
            {
                var delay = response.Headers.RetryAfter?.Delta ?? response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow ?? TimeSpan.FromMinutes(1);
                retryAfter = DateTime.UtcNow.Add(delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1));
                throw new InvalidOperationException("Spotify is limiting requests. Wait a little before refreshing your library.");
            }
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Spotify library access failed ({(int)response.StatusCode}). Reconnect your library in Settings. Playback in Spotify remains separate.");
            var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            if (At(document.RootElement, "errors").ValueKind == JsonValueKind.Array && At(document.RootElement, "errors").GetArrayLength() > 0)
            {
                document.Dispose();
                throw new InvalidOperationException("Spotify changed or rejected this library request. Use Show Spotify for now; the library connector may need an update.");
            }
            return document;
        }
    }

    public async Task<Spotify.Profile?> ProfileAsync()
    {
        using var doc = await Query("profileAttributes", ProfileHash, new { });
        var profile = At(doc.RootElement, "data", "me", "profile");
        if (profile.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Spotify did not return a signed-in profile. Connect your library again.");
        return new Spotify.Profile(Text(profile, "name") ?? Text(profile, "username") ?? "Spotify", null, null,
            Image(At(profile, "avatar", "sources")));
    }

    public async Task<List<Spotify.Playlist>> PlaylistsAsync(CancellationToken cancellation = default)
    {
        var result = new List<Spotify.Playlist> { new("liked", "Liked Songs", "Your saved tracks", null) };
        var ids = new HashSet<string>();
        var pages = new HashSet<string>();
        for (int offset = 0; ; )
        {
            using var doc = await Query("libraryV3", LibraryHash, new { filters = Array.Empty<string>(), order = (string?)null,
                textFilter = "", features = new[] { "LIKED_SONGS", "YOUR_EPISODES_V2", "PRERELEASES", "EVENTS" },
                limit = 100, offset, flatten = true, expandedFolders = Array.Empty<string>(), folderUri = (string?)null, includeFoldersWhenFlattening = false }, cancellation);
            var page = At(doc.RootElement, "data", "me", "libraryV3");
            var items = RequiredItems(page);
            if (items.GetArrayLength() == 0) break;
            if (!pages.Add(items.GetRawText())) throw new InvalidOperationException("Spotify repeated a library page. Refresh later; the partial list was not saved.");
            foreach (var entry in items.EnumerateArray())
            {
                var item = At(entry, "item"); var data = At(item, "data");
                string? uri = Text(item, "uri") ?? Text(item, "_uri") ?? Text(data, "uri");
                if (uri == null || !uri.StartsWith("spotify:playlist:", StringComparison.Ordinal)) continue;
                var id = uri[17..];
                if (!ids.Add(id)) continue;
                var images = At(data, "images", "items");
                string? art = images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0 ? Image(At(images[0], "sources")) : null;
                result.Add(new Spotify.Playlist(id, Text(data, "name") ?? "Untitled playlist",
                    Text(At(data, "ownerV2", "data"), "name") ?? "Spotify playlist", art));
            }
            offset += items.GetArrayLength();
            if (Complete(page, items, offset, 100)) break;
        }
        return result;
    }

    public async Task<List<Track>> PlaylistAsync(string id, CancellationToken cancellation = default)
    {
        bool liked = id == "liked";
        if (!liked) SpotifyDesktop.ValidateUri("spotify:playlist:" + id);
        var result = new List<Track>();
        for (int offset = 0; ; )
        {
            object variables = liked ? new { offset, limit = 50 } : new { uri = "spotify:playlist:" + id, offset, limit = 50,
                enableWatchFeedEntrypoint = true, includeEpisodeContentRatingsV2 = true };
            using var doc = await Query(liked ? "fetchLibraryTracks" : "fetchPlaylist", liked ? LikedHash : PlaylistHash, variables, cancellation);
            var page = liked ? At(doc.RootElement, "data", "me", "library", "tracks") : At(doc.RootElement, "data", "playlistV2", "content");
            var items = RequiredItems(page);
            if (items.GetArrayLength() == 0) break;
            foreach (var entry in items.EnumerateArray())
            {
                var wrapper = At(entry, liked ? "track" : "itemV2");
                if (ParseTrack(At(wrapper, "data"), Text(wrapper, "_uri") ?? Text(wrapper, "uri")) is { } track) result.Add(track);
            }
            offset += items.GetArrayLength();
            if (Complete(page, items, offset, 50)) break;
            if (offset > 100000) throw new InvalidOperationException("Spotify returned an unexpectedly large playlist.");
        }
        return result;
    }

    public async Task<List<Track>> SearchAsync(string query)
    {
        using var doc = await Query("searchTopResultsList", SearchHash, new { query, offset = 0, limit = 20,
            numberOfTopResults = 20, includeAudiobooks = true, includeArtistHasConcertsField = false,
            includePreReleases = true, includeAuthors = true, includeAlbumPreReleases = false,
            includeEpisodeContentRatingsV2 = true, isPrefix = (string?)null, sectionFilters = new[] { "GENERIC", "VIDEO_CONTENT" } });
        var search = At(doc.RootElement, "data", "searchV2");
        var list = new List<Track>();
        var ids = new HashSet<string>();
        foreach (var path in new[] { new[] { "tracksV2", "items" }, new[] { "topResultsV2", "itemsV2" } })
        {
            var items = At(search, path);
            if (items.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in items.EnumerateArray())
            {
                var track = ParseTrack(At(item, "item", "data")) ?? ParseTrack(At(item, "data"));
                if (track != null && ids.Add(track.Id)) list.Add(track);
            }
        }
        return list;
    }

    internal static Track? ParseTrack(JsonElement data, string? fallbackUri = null)
    {
        string? uri = Text(data, "uri") ?? fallbackUri;
        if (data.ValueKind != JsonValueKind.Object || uri == null || !System.Text.RegularExpressions.Regex.IsMatch(uri, "^spotify:track:[A-Za-z0-9]{22}$")) return null;
        if (At(data, "playability", "playable").ValueKind == JsonValueKind.False) return null;
        var artists = At(data, "artists", "items");
        string artist = artists.ValueKind == JsonValueKind.Array ? string.Join(", ", artists.EnumerateArray().Select(a => Text(At(a, "profile"), "name") ?? Text(a, "name")).Where(n => n != null)) : "";
        int duration = Number(At(data, "trackDuration", "totalMilliseconds")) ?? Number(At(data, "duration", "totalMilliseconds")) ?? Number(At(data, "duration")) ?? 0;
        return new Track(uri[14..], Text(data, "name") ?? "Unavailable track", artist,
            Image(At(data, "albumOfTrack", "coverArt", "sources")), null, "Spotify", uri, duration);
    }
    static JsonElement RequiredItems(JsonElement page)
    {
        var items = At(page, "items");
        if (items.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Spotify returned an unfamiliar library format. The connector may need an update.");
        return items;
    }
    static bool Complete(JsonElement page, JsonElement items, int offset, int limit) =>
        Number(At(page, "totalCount")) is { } total ? offset >= total : items.GetArrayLength() < limit;
    internal static JsonElement At(JsonElement value, params string[] path)
    {
        foreach (var key in path) { if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out value)) return default; }
        return value;
    }
    static string? Text(JsonElement item, string key) => At(item, key) is var value && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    static int? Number(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : null;
    static string? Image(JsonElement sources) => sources.ValueKind == JsonValueKind.Array && sources.GetArrayLength() > 0 ? Text(sources[0], "url") : null;
}
