using System.Security.Cryptography;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace GlassPlayer;

// A short-lived, official Spotify login page supplies the user's own library session.
// The browser is disposed after capture; it is never used as an audio player.
sealed class SpotifyWebSession
{
    internal record Credentials(string Access, string ClientToken, string UserAgent, DateTime Expires);
    readonly string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassPlayer", "spotify-library");
    readonly SemaphoreSlim gate = new(1, 1);
    Credentials? credentials;
    int generation;
    string Store => Path.Combine(root, "session.bin");
    public bool SignedIn => credentials != null || File.Exists(Store);
    public int? LastBrowserProcessId { get; private set; }
    public bool BrowserOpen { get; private set; }

    internal static bool IsSessionRequest(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Host is "api.spotify.com" or "api-partner.spotify.com";

    public async Task<Credentials> GetAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (credentials == null && File.Exists(Store))
                try { credentials = JsonSerializer.Deserialize<Credentials>(ProtectedData.Unprotect(File.ReadAllBytes(Store), null, DataProtectionScope.CurrentUser)); }
                catch { }
            if (credentials != null && credentials.Expires > DateTime.UtcNow) return credentials;
            if (!SignedIn) throw new InvalidOperationException("Choose Connect library in Settings to load your Spotify playlists.");
            return await CaptureAsync(false);
        }
        finally { gate.Release(); }
    }

    public async Task ConnectAsync()
    {
        await gate.WaitAsync();
        try { await CaptureAsync(true); }
        finally { gate.Release(); }
    }

    public void Invalidate() { if (credentials != null) credentials = credentials with { Expires = DateTime.MinValue }; }
    public void SignOut()
    {
        generation++; credentials = null;
        Directory.CreateDirectory(root);
        if (File.Exists(Store)) File.Delete(Store);
        // Use a fresh local browser profile next time. No previous account is silently reconnected.
        File.WriteAllText(Path.Combine(root, "profile-id"), Guid.NewGuid().ToString("N"));
    }

    async Task<Credentials> CaptureAsync(bool interactive)
    {
        if (!System.Windows.Application.Current.Dispatcher.CheckAccess())
            return await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => CaptureAsync(interactive)).Task.Unwrap();
        Directory.CreateDirectory(root);
        string profileFile = Path.Combine(root, "profile-id");
        string profile = File.Exists(profileFile) ? File.ReadAllText(profileFile) : "initial";
        if (!System.Text.RegularExpressions.Regex.IsMatch(profile, "^[a-zA-Z0-9]+$")) profile = "initial";
        int version = generation;
        var completion = new TaskCompletionSource<Credentials>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(interactive ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(25));
        var browser = new WebView2();
        var window = new Window
        {
            Title = "Connect your Spotify library — GlassPlayer", Width = 560, Height = 760,
            Content = browser, Owner = System.Windows.Application.Current.MainWindow,
            ShowInTaskbar = interactive, ShowActivated = interactive, Opacity = interactive ? 1 : 0,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        window.Closed += (_, _) => completion.TrySetCanceled();
        string? access = null, clientToken = null;
        bool capturing = false;
        CoreWebView2Environment? environment = null;
        try
        {
            window.Show(); BrowserOpen = true;
            environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "profiles", profile));
            await browser.EnsureCoreWebView2Async(environment);
            LastBrowserProcessId = (int)browser.CoreWebView2.BrowserProcessId;
            browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            browser.CoreWebView2.AddWebResourceRequestedFilter("https://*.spotify.com/*", CoreWebView2WebResourceContext.All);
            browser.CoreWebView2.WebResourceRequested += OnRequest;
            browser.CoreWebView2.ProcessFailed += (_, _) => completion.TrySetException(new InvalidOperationException("Spotify sign-in could not load. Try Connect library again."));
            browser.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                // Keep Spotify's account flow in this temporary window; no external player window.
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
                    (uri.Host == "spotify.com" || uri.Host.EndsWith(".spotify.com", StringComparison.Ordinal)))
                { e.Handled = true; browser.CoreWebView2.Navigate(e.Uri); }
            };
            browser.CoreWebView2.Navigate("https://open.spotify.com/collection/playlists");
            var result = await completion.Task.WaitAsync(timeout.Token);
            if (version != generation) throw new OperationCanceledException();
            credentials = result;
            File.WriteAllBytes(Store, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(result), null, DataProtectionScope.CurrentUser));
            return result;

            async void OnRequest(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
            {
                if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri) || !IsSessionRequest(uri)) return;
                try
                {
                    var headers = args.Request.Headers;
                    if (headers.Contains("authorization"))
                    {
                        string value = headers.GetHeader("authorization");
                        if (value.StartsWith("Bearer BQ", StringComparison.Ordinal)) access = value[7..];
                    }
                    if (headers.Contains("client-token")) clientToken = headers.GetHeader("client-token");
                    if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(clientToken) || capturing) return;
                    capturing = true;
                    // Capture only signed-in account sessions. Anonymous Spotify pages also have tokens.
                    var cookies = await browser.CoreWebView2.CookieManager.GetCookiesAsync("https://open.spotify.com");
                    bool authenticated = cookies.Any(c => c.Name == "sp_dc" && !string.IsNullOrEmpty(c.Value));
                    if (!authenticated) { capturing = false; return; }
                    completion.TrySetResult(new Credentials(access, clientToken, browser.CoreWebView2.Settings.UserAgent, DateTime.UtcNow.AddMinutes(40)));
                }
                catch { capturing = false; }
            }
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException(interactive ? "Spotify sign-in was cancelled or timed out." : "Your Spotify library session needs attention. Choose Connect library in Settings again.");
        }
        finally
        {
            browser.Dispose();
            window.Close();
            BrowserOpen = false;
            // No timer, WebView, or hidden page survives this method. Cookies stay in the isolated profile.
        }
    }
}
