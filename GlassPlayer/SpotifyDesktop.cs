using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace GlassPlayer;

// Uses Windows media sessions, not Spotify's Premium-only remote-control API.
sealed class SpotifyDesktop : IDisposable
{
    public record State(string Title, string Artist, byte[]? Artwork, bool Playing,
        int PositionMs, int DurationMs, DateTime At, bool CanPlay, bool CanPause,
        bool CanNext, bool CanPrevious, bool CanSeek);

    GlobalSystemMediaTransportControlsSessionManager? manager;
    GlobalSystemMediaTransportControlsSession? session;
    readonly SemaphoreSlim refreshGate = new(1, 1);
    readonly SemaphoreSlim startGate = new(1, 1);
    bool disposed;
    public State? Current { get; private set; }
    public event Action<State?>? Changed;
    public static string Executable => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spotify", "Spotify.exe");
    public static bool Installed => File.Exists(Executable);

    internal static bool IsSpotifySession(string id) =>
        id.Equals("Spotify.exe", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("SpotifyAB.SpotifyMusic_", StringComparison.OrdinalIgnoreCase);

    internal static string ValidateUri(string uri)
    {
        if (!Regex.IsMatch(uri, @"\Aspotify:(track|playlist|album):[A-Za-z0-9]{22}\z"))
            throw new ArgumentException("Use a Spotify song, album, or playlist link.");
        return uri;
    }

    public async Task StartAsync()
    {
        await startGate.WaitAsync();
        try
        {
            if (manager != null || disposed) return;
            var pending = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (disposed) return;
            manager = pending;
            manager.SessionsChanged += SessionsChanged;
        }
        finally { startGate.Release(); }
        await RefreshAsync();
    }

    void SessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) => _ = RefreshAsync();
    void MediaChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => _ = RefreshAsync();
    void PlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => _ = RefreshAsync(false);
    void TimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => _ = RefreshAsync(false);

    void Detach()
    {
        if (session == null) return;
        session.MediaPropertiesChanged -= MediaChanged;
        session.PlaybackInfoChanged -= PlaybackChanged;
        session.TimelinePropertiesChanged -= TimelineChanged;
        session = null;
    }

    public async Task RefreshAsync(bool metadata = true)
    {
        await refreshGate.WaitAsync();
        try
        {
            if (disposed || manager == null) return;
            var next = manager.GetSessions().FirstOrDefault(s => IsSpotifySession(s.SourceAppUserModelId));
            if (next != session)
            {
                Detach(); session = next; metadata = true;
                if (session != null)
                {
                    session.MediaPropertiesChanged += MediaChanged;
                    session.PlaybackInfoChanged += PlaybackChanged;
                    session.TimelinePropertiesChanged += TimelineChanged;
                }
            }
            if (session == null) { Current = null; Changed?.Invoke(null); return; }
            string title = Current?.Title ?? "Spotify", artist = Current?.Artist ?? "";
            byte[]? art = Current?.Artwork;
            if (metadata || Current == null)
            {
                var media = await session.TryGetMediaPropertiesAsync();
                title = media.Title; artist = media.Artist; art = null;
                if (media.Thumbnail != null)
                {
                    using var stream = await media.Thumbnail.OpenReadAsync();
                    if (stream.Size is > 0 and <= 2 * 1024 * 1024)
                    {
                        using var reader = new DataReader(stream.GetInputStreamAt(0));
                        await reader.LoadAsync((uint)stream.Size);
                        art = new byte[(int)stream.Size]; reader.ReadBytes(art);
                    }
                }
            }
            if (disposed || session == null) return;
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var controls = playback.Controls;
            bool playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var position = timeline.Position.TotalMilliseconds;
            if (playing && timeline.LastUpdatedTime != default)
                position += Math.Max(0, (DateTimeOffset.UtcNow - timeline.LastUpdatedTime).TotalMilliseconds);
            var duration = Math.Max(0, timeline.EndTime.TotalMilliseconds);
            Current = new State(title, artist, art, playing, (int)Math.Clamp(position, 0, Math.Max(0, duration)),
                (int)Math.Min(int.MaxValue, duration), DateTime.UtcNow, controls.IsPlayEnabled, controls.IsPauseEnabled,
                controls.IsNextEnabled, controls.IsPreviousEnabled, controls.IsPlaybackPositionEnabled);
            Changed?.Invoke(Current);
        }
        catch
        {
            // Session removal races are normal. Do not retain a misleading playing indicator.
            Current = null;
            if (!disposed) Changed?.Invoke(null);
        }
        finally { refreshGate.Release(); }
    }

    public void Show()
    {
        if (!Installed) throw new InvalidOperationException("Install the official Spotify desktop app first.");
        RestoreHidden();
        Process.Start(new ProcessStartInfo(Executable) { UseShellExecute = true });
        foreach (var process in Process.GetProcessesByName("Spotify"))
        {
            using (process)
            {
                var window = process.MainWindowHandle;
                if (window == IntPtr.Zero) continue;
                ShowWindowAsync(window, 9); SetForegroundWindow(window);
            }
        }
    }

    public void Hide()
    {
        // The owner creates a recoverable tray icon BEFORE hiding Spotify.
        foreach (var process in Process.GetProcessesByName("Spotify"))
            using (process)
                if (process.MainWindowHandle is var window && window != IntPtr.Zero)
                { ShowWindowAsync(window, 6); ShowWindowAsync(window, 0); hiddenWindows.Add(window); }
    }
    readonly HashSet<IntPtr> hiddenWindows = new();
    public void RestoreHidden()
    {
        foreach (var window in hiddenWindows) if (IsWindow(window)) ShowWindowAsync(window, 4);
        hiddenWindows.Clear();
    }

    async Task Command(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> command)
    {
        await StartAsync();
        if (session == null) throw new InvalidOperationException("Open Spotify, sign in, and start a song on this PC first.");
        if (!await command(session)) throw new InvalidOperationException("Spotify did not accept that control. It may be unavailable during an ad or on your plan.");
        await RefreshAsync(false);
    }
    public Task PauseAsync() => Command(async s => await s.TryPauseAsync());
    public Task ResumeAsync() => Command(async s => await s.TryPlayAsync());
    public Task NextAsync() => Command(async s => await s.TrySkipNextAsync());
    public Task PreviousAsync() => Command(async s => await s.TrySkipPreviousAsync());
    public Task SeekAsync(int milliseconds) => Command(async s => await s.TryChangePlaybackPositionAsync(TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)).Ticks));

    public Task VolumeAsync(int percent) => Task.Run(() =>
    {
        using var devices = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        using var device = devices.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
        var sessions = device.AudioSessionManager.Sessions;
        bool changed = false;
        for (int index = 0; index < sessions.Count; index++)
        {
            using var audio = sessions[index];
            try
            {
                using var process = Process.GetProcessById((int)audio.GetProcessID);
                if (!process.ProcessName.Equals("Spotify", StringComparison.OrdinalIgnoreCase)) continue;
                audio.SimpleAudioVolume.Volume = Math.Clamp(percent / 100f, 0, 1);
                changed = true;
            }
            catch (ArgumentException) { }
        }
        if (!changed) throw new InvalidOperationException("Spotify has no audio session on the default output yet.");
    });

    public void Dispose()
    {
        disposed = true;
        if (manager != null) manager.SessionsChanged -= SessionsChanged;
        Detach(); RestoreHidden();
    }
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
}
