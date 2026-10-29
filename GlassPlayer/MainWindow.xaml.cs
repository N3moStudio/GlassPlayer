// Modified 2026-10-26 by N3moStudio: native ARM64, performance, Spotify integration and packaging. Original work by Jonzu00; GPL-3.0.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GlassPlayer;

public partial class MainWindow : Window
{
    readonly AppSettings cfg;
    readonly SavedLibrary savedLibrary;
    List<Track>? currentList;
    string currentListTitle = "Imported music";
    bool followSpotify;
    bool appearanceReady;
    int playRequest;
    IReadOnlyList<Track> playbackQueue = Array.Empty<Track>();
    int queueIndex;
    AudioEngine? engine;
    long fallbackMs;
    bool importOpen;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly DispatcherTimer seekDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly DispatcherTimer volDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly Dictionary<string, IProvider> providers = new();
    IProvider? provider;
    Spotify? spotify;
    readonly SpotifyDesktop desktop = new();
    System.Windows.Forms.NotifyIcon? tray;
    bool didAutoHide;
    bool updating, playing, advanced, polling, settingsOpen;

    // Spotify remote state: non-null while a Spotify track is the one playing
    Spotify? activeSp;
    Track? nowPlaying;
    int rProgress, rDuration;
    DateTime rStamp, lastPoll, playStart;
    bool rPlaying;
    int libraryRequest;
    CancellationTokenSource? libraryCancellation;

    public MainWindow() : this(AppSettings.Load(), new SavedLibrary()) { }

    internal MainWindow(AppSettings settings, SavedLibrary library, bool connectLibrary = true)
    {
        cfg = settings;
        savedLibrary = library;
        InitializeComponent();

        if (connectLibrary) _ = Task.Run(Cache.Cleanup);
        foreach (IProvider p in new IProvider[] { new YouTubeMusic(), new SoundCloud(), new Spotify(), new LocalFiles() })
            providers[p.Name] = p;
        spotify = (Spotify)providers["Spotify"];
        spotify.Desktop = desktop;
        if (connectLibrary)
        {
            spotify.LibrarySession = new SpotifyWebSession();
            spotify.Library = new SpotifyWebLibrary(spotify.LibrarySession);
        }
        desktop.Changed += state => Dispatcher.InvokeAsync(() => DesktopChanged(state));
        BuildDetail.Text = $"Running natively as {RuntimeInformation.ProcessArchitecture} · .NET {Environment.Version.Major}";

        AutoplayToggle.IsChecked = cfg.Autoplay;
        VolumeToggle.IsChecked = cfg.RememberVolume;
        GlowToggle.IsChecked = cfg.AmbientGlow;
        HideSpotifyToggle.IsChecked = cfg.HideSpotify;
        Ambient.Opacity = cfg.AmbientGlow ? .18 : 0;
        appearanceReady = true;
        cfg.GlassTint = AppSettings.NormalizeTint(cfg.GlassTint);
        GlassTintSlider.Value = cfg.GlassTint * 100;
        ApplyGlassTint();
        if (cfg.RememberVolume) Volume.Value = cfg.Volume;

        if (cfg.StartProvider == "Spotify") SpotifyBtn.IsChecked = true;
        else SoundCloudBtn.IsChecked = true;

        // Audio and browser runtimes are initialized only when their feature is used.

        seekDebounce.Tick += async (_, _) => { seekDebounce.Stop(); await Safe(() => activeSp?.SeekAsync((int)(Seek.Value * 1000)) ?? Task.CompletedTask); };
        volDebounce.Tick += async (_, _) => { volDebounce.Stop(); await Safe(() => activeSp?.VolumeAsync((int)(Volume.Value * 100)) ?? Task.CompletedTask); };

        timer.Tick += async (_, _) =>
        {
            if (activeSp != null) { await RemoteTickAsync(activeSp); return; }
            if (engine == null) return;
            long len = engine.LengthMs > 0 ? engine.LengthMs : fallbackMs;
            if (len <= 0) return;
            long pos = Math.Max(0, engine.TimeMs);
            updating = true;
            Seek.Maximum = len / 1000.0;
            if (!Seek.IsMouseCaptureWithin) Seek.Value = pos / 1000.0;
            Cur.Text = Fmt(TimeSpan.FromMilliseconds(pos));
            Dur.Text = Fmt(TimeSpan.FromMilliseconds(len));
            updating = false;
        };
        StateChanged += (_, _) => timer.Interval = WindowState == WindowState.Minimized ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(500);

        PreviewKeyDown += (_, e) => { if (e.Key != Key.Escape) return; if (importOpen) ToggleImport(false); else if (settingsOpen) ToggleSettings(false); };
    }

    void WindowSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        WindowSurface.Clip = new RectangleGeometry(new Rect(e.NewSize), 24, 24);
        // Keep transport and volume visible at the minimum supported height.
        if (ArtBox != null) ArtBox.Width = ArtBox.Height = Math.Clamp(e.NewSize.Height - 380, 160, 280);
    }

    void ApplyGlassTint()
    {
        if (GlassBackdrop == null || GlassTintLabel == null) return;
        var tint = SystemParameters.HighContrast ? 1 : AppSettings.NormalizeTint(cfg.GlassTint);
        GlassBackdrop.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(tint * 255), 16, 20, 28));
        GlassTintLabel.Text = $"{cfg.GlassTint:P0} tint";
    }

    void GlassTint_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!appearanceReady) return;
        cfg.GlassTint = AppSettings.NormalizeTint(e.NewValue / 100);
        ApplyGlassTint();
    }

    bool closing;
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!closing)
        {
            e.Cancel = true; // play the exit animation first, then close for real
            closing = true;
            Anim(Root, OpacityProperty, 0, 240);
            Anim(Ambient, OpacityProperty, 0, 240);
            Anim(Overlay, OpacityProperty, 0, 240);
            Anim(RootScale, ScaleTransform.ScaleXProperty, .95, 260);
            Anim(RootScale, ScaleTransform.ScaleYProperty, .95, 260);
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
            t.Tick += (_, _) => { t.Stop(); Close(); };
            t.Start();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        timer.Stop(); seekDebounce.Stop(); volDebounce.Stop();
        playRequest++;
        libraryCancellation?.Cancel();
        CompositionTarget.Rendering -= ScrollStep;
        engine?.Dispose();
        desktop.Dispose();
        if (tray != null) { tray.Visible = false; tray.Dispose(); }
        spotify?.Web?.Disconnect();
        Web.Dispose();
        cfg.Volume = Volume.Value;
        cfg.Save();
        base.OnClosed(e);
    }

    static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    async Task Safe(Func<Task> action)
    {
        try { await action(); } catch (Exception ex) { Status.Text = ex.Message; }
    }

    byte[]? desktopArtwork;
    void DesktopChanged(SpotifyDesktop.State? state)
    {
        if (closing || !followSpotify) return;
        if (state == null)
        {
            if (activeSp?.Desktop != null)
            {
                activeSp = null; nowPlaying = null; SetPlaying(false);
                TitleText.Text = "Nothing playing"; ArtistText.Text = "Open Spotify and start a song";
            }
            Seek.IsEnabled = false; NextBtn.IsEnabled = false; PrevBtn.IsEnabled = false;
            PlayBtn.IsEnabled = true;
            return;
        }
        if (state.Playing) engine?.Stop();
        activeSp = spotify;
        nowPlaying = new Track("desktop", state.Title, state.Artist, null, null, "Spotify", null, state.DurationMs);
        TitleText.Text = string.IsNullOrWhiteSpace(state.Title) ? "Spotify" : state.Title;
        ArtistText.Text = state.Artist;
        if (!ReferenceEquals(desktopArtwork, state.Artwork))
        {
            desktopArtwork = state.Artwork;
            BitmapImage? image = null;
            if (state.Artwork != null)
            {
                try
                {
                    using var stream = new MemoryStream(state.Artwork);
                    image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = stream; image.DecodePixelWidth = 480; image.EndInit(); image.Freeze();
                }
                catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
                { image = null; }
            }
            ArtBrush.ImageSource = image; Glow.Source = image;
        }
        Seek.IsEnabled = state.CanSeek;
        NextBtn.IsEnabled = state.CanNext; PrevBtn.IsEnabled = state.CanPrevious;
        PlayBtn.IsEnabled = state.Playing ? state.CanPause : state.CanPlay;
        SetPlaying(state.Playing);
        _ = RemoteTickAsync(spotify!);
        if (state.Playing && cfg.HideSpotify && !didAutoHide) { didAutoHide = true; HideSpotify(); }
    }

    void EnsureTray()
    {
        if (tray != null) return;
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open GlassPlayer", null, (_, _) => Dispatcher.Invoke(() => { Show(); WindowState = WindowState.Normal; Activate(); }));
        menu.Items.Add("Show Spotify", null, (_, _) => Dispatcher.Invoke(() => ShowSpotify_Click(this, new RoutedEventArgs())));
        menu.Items.Add("Hide Spotify", null, (_, _) => Dispatcher.Invoke(HideSpotify));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Quit GlassPlayer", null, (_, _) => Dispatcher.Invoke(Close));
        tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "GlassPlayer — Spotify controls", Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu, Visible = true
        };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => { Show(); WindowState = WindowState.Normal; Activate(); });
    }
    void HideSpotify() { EnsureTray(); desktop.Hide(); }
    async void ShowSpotify_Click(object sender, RoutedEventArgs args)
    {
        playRequest++;
        followSpotify = true;
        didAutoHide = true; // A deliberate Show action stays visible until the user hides or plays from GlassPlayer.
        desktop.RestoreHidden();
        await Safe(() => { desktop.Show(); return Task.CompletedTask; });
        await Safe(desktop.StartAsync);
        DesktopChanged(desktop.Current);
    }
    void HideSpotify_Click(object sender, RoutedEventArgs args) => HideSpotify();

    void StartEngine()
    {
        var eng = new AudioEngine(Dispatcher, Volume.Value);
        eng.Ended += () =>
        {
            if (activeSp != null) return;
            if ((DateTime.UtcNow - playStart).TotalSeconds < 3) { Status.Text = "Couldn't play this track. Try another one."; SetPlaying(false); return; }
            if (cfg.Autoplay) Step(1); else SetPlaying(false);
        };
        eng.Failed += msg => { Status.Text = msg; SetPlaying(false); };
        engine = eng;
    }

    // ---- animation helpers ----
    static void Anim(IAnimatable target, DependencyProperty prop, double to, int ms = 350, Action? done = null)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (done != null) a.Completed += (_, _) => done();
        target.BeginAnimation(prop, a);
    }

    static void Fade(UIElement el, double from, double to, int ms)
        => el.BeginAnimation(OpacityProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

    async Task AnimateResultsAsync()
    {
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        for (int i = 0; i < Math.Min(List.Items.Count, 14); i++)
        {
            if (List.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem c) continue;
            var tt = new TranslateTransform(0, 18);
            c.RenderTransform = tt;
            var begin = TimeSpan.FromMilliseconds(i * 45);
            c.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { BeginTime = begin, EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(420)) { BeginTime = begin, EasingFunction = ease });
        }
    }

    // ---- smooth wheel scrolling for the song list ----
    ScrollViewer? listScroll;
    double scrollTarget;
    bool scrolling;

    static ScrollViewer? FindScroll(DependencyObject d)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is ScrollViewer sv) return sv;
            var r = FindScroll(c);
            if (r != null) return r;
        }
        return null;
    }

    void List_PreviewMouseWheel(object s, MouseWheelEventArgs e)
    {
        listScroll ??= FindScroll(List);
        if (listScroll == null) return;
        e.Handled = true;
        if (!scrolling) scrollTarget = listScroll.VerticalOffset;
        scrollTarget = Math.Clamp(scrollTarget - e.Delta * 0.9, 0, listScroll.ScrollableHeight);
        if (!scrolling) { scrolling = true; CompositionTarget.Rendering += ScrollStep; }
    }

    void ScrollStep(object? s, EventArgs e)
    {
        var sv = listScroll!;
        double diff = scrollTarget - sv.VerticalOffset;
        if (Math.Abs(diff) < 0.5)
        {
            sv.ScrollToVerticalOffset(scrollTarget);
            CompositionTarget.Rendering -= ScrollStep;
            scrolling = false;
            return;
        }
        sv.ScrollToVerticalOffset(sv.VerticalOffset + diff * 0.18);
    }

    // Interpolate locally; remote API polls are deliberately sparse to preserve quota and battery.
    async Task RemoteTickAsync(Spotify sp)
    {
        var now = DateTime.UtcNow;
        if (sp.Desktop != null)
        {
            if (await sp.StateAsync() is not { } native) return;
            updating = true;
            Seek.Maximum = Math.Max(1, native.Duration / 1000.0);
            if (!Seek.IsMouseCaptureWithin) Seek.Value = native.Progress / 1000.0;
            Cur.Text = Fmt(TimeSpan.FromMilliseconds(native.Progress));
            Dur.Text = Fmt(TimeSpan.FromMilliseconds(native.Duration));
            updating = false;
            return;
        }
        if (!polling && (now - lastPoll).TotalSeconds > (rPlaying ? 5 : 15))
        {
            polling = true; lastPoll = now;
            try
            {
                if (await sp.StateAsync() is { } st && (st.Uri == nowPlaying?.OpenUri || (DateTime.UtcNow - playStart).TotalSeconds > 3))
                {
                    rPlaying = st.Playing; rProgress = st.Progress; rDuration = st.Duration; rStamp = DateTime.UtcNow;
                    SetPlaying(st.Playing);
                }
            }
            catch (Exception ex) { Status.Text = ex.Message; }
            polling = false;
        }
        if (rDuration <= 0 || WindowState == WindowState.Minimized) return;
        int pos = rPlaying ? Math.Min(rProgress + (int)(DateTime.UtcNow - rStamp).TotalMilliseconds, rDuration) : rProgress;
        updating = true;
        Seek.Maximum = rDuration / 1000.0;
        if (!Seek.IsMouseCaptureWithin) Seek.Value = pos / 1000.0;
        Cur.Text = Fmt(TimeSpan.FromMilliseconds(pos));
        Dur.Text = Fmt(TimeSpan.FromMilliseconds(rDuration));
        updating = false;
        if (rPlaying && pos >= rDuration - 1500 && !advanced) { advanced = true; if (cfg.Autoplay) Step(1); }
    }

    // ---- search ----
    async void Provider_Checked(object s, RoutedEventArgs e)
    {
        if (s is RadioButton { Tag: string name } && providers.TryGetValue(name, out var p)) provider = p;
        if (provider == null) return;
        libraryRequest++;
        libraryCancellation?.Cancel();
        LibraryBar.Visibility = provider == spotify ? Visibility.Visible : Visibility.Collapsed;
        SavedBar.Visibility = provider == spotify ? Visibility.Collapsed : Visibility.Visible;
        cfg.StartProvider = provider.Name;
        currentList = null;
        SaveImportButton.IsEnabled = false;
        List.ItemsSource = null;
        if (provider == spotify) await LoadPlaylistsAsync();
        else if (SearchBox.Text.Length > 0) await SearchAsync();
        else ShowSavedImports();
    }

    async void Library_Click(object s, RoutedEventArgs e) => await LoadPlaylistsAsync();

    async Task LoadPlaylistsAsync()
    {
        if (spotify == null) return;
        int request = ++libraryRequest;
        libraryCancellation?.Cancel();
        libraryCancellation = new CancellationTokenSource();
        if (!spotify.SignedIn)
        {
            List.ItemsSource = null;
            Status.Text = "Connect library in Settings to browse Spotify playlists. SoundCloud and local files play without the Spotify app.";
            return;
        }
        Status.Text = "Loading your playlists…";
        try
        {
            var playlists = await spotify.PlaylistsAsync(libraryCancellation.Token);
            if (request != libraryRequest) return;
            List.ItemsSource = playlists;
            Status.Text = playlists.Count == 0 ? "No playlists returned. Check the account connected in Settings." : $"{playlists.Count} playlists · Select one to see its songs.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (request == libraryRequest) Status.Text = ex.Message; }
    }

    async Task OpenPlaylistAsync(Spotify.Playlist playlist)
    {
        int request = ++libraryRequest;
        libraryCancellation?.Cancel();
        libraryCancellation = new CancellationTokenSource();
        Status.Text = "Loading " + playlist.Title + "…";
        try
        {
            var tracks = await spotify!.PlaylistAsync(playlist.Id, libraryCancellation.Token);
            if (request != libraryRequest) return;
            ShowTracks(tracks, $"{playlist.Title} · {tracks.Count} Spotify references", playlist.Title);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (request == libraryRequest) Status.Text = ex.Message; }
    }

    void Search_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) _ = SearchAsync(); }

    async Task SearchAsync()
    {
        if (provider == null || string.IsNullOrWhiteSpace(SearchBox.Text)) return;
        int request = ++libraryRequest;
        libraryCancellation?.Cancel();
        var searchProvider = provider;
        Status.Text = provider is Spotify { SignedIn: false } ? "Connect your Spotify library in Settings first." : "Searching…";
        try
        {
            var results = await searchProvider.SearchAsync(SearchBox.Text.Trim());
            if (request != libraryRequest) return;
            ShowTracks(results, results.Count == 0 ? "No results. Try different words." : "Choose a track. Availability depends on the source.", SearchBox.Text.Trim());
        }
        catch (Exception ex) { if (request == libraryRequest) Status.Text = "Search failed: " + ex.Message; }
    }

    void List_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        FindSoundCloudButton.IsEnabled = List.SelectedItem is Track { Source: "Spotify" };
        if (List.SelectedItem is Track { Source: "Spotify" })
            Status.Text = "Spotify reference selected. Find it on SoundCloud and choose the version you want. Spotify stays closed.";
        else if (List.SelectedItem is Track t) _ = PlayAsync(t);
        else if (List.SelectedItem is Spotify.Playlist playlist) _ = OpenPlaylistAsync(playlist);
        else if (List.SelectedItem is SavedPlaylist saved)
            ShowTracks(saved.Tracks, $"{saved.Title} · {saved.Tracks.Count} saved tracks", saved.Title);
    }

    void FindSoundCloud_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not Track { Source: "Spotify" } track) return;
        SearchBox.Text = $"{track.Artist} {track.Title}";
        SoundCloudBtn.IsChecked = true;
    }

    // ---- playback ----
    async Task PlayAsync(Track t, bool keepQueue = false)
    {
        if (t.Source == "Spotify")
        {
            Status.Text = "Spotify Free cannot stream inside GlassPlayer. Choose a SoundCloud track or local file.";
            return;
        }
        int request = ++playRequest;
        if (!keepQueue)
        {
            playbackQueue = currentList?.Where(track => track.Source != "Spotify").ToArray() ?? new[] { t };
            queueIndex = Math.Max(0, Array.IndexOf(playbackQueue.ToArray(), t));
        }
        followSpotify = false;
        engine?.Stop();
        SetPlaying(false);
        Seek.IsEnabled = false; PrevBtn.IsEnabled = true; NextBtn.IsEnabled = true; PlayBtn.IsEnabled = false;
        nowPlaying = t; advanced = false;
        fallbackMs = t.DurationMs;
        updating = true; Seek.Maximum = 1; Seek.Value = 0; Cur.Text = "0:00"; Dur.Text = t.DurationMs > 0 ? Fmt(TimeSpan.FromMilliseconds(t.DurationMs)) : "0:00"; updating = false;
        TitleText.Text = t.Title;
        ArtistText.Text = t.Artist;
        Fade(TitleText, 0, 1, 450); Fade(ArtistText, 0, 1, 600); Fade(ArtBox, .25, 1, 550);
        if (t.Art != null)
        {
            var img = new BitmapImage(new Uri(t.Art));
            ArtBrush.ImageSource = img;
            Glow.Source = img;
            Fade(Glow, 0, .6, 1000);
        }
        else { ArtBrush.ImageSource = null; Glow.Source = null; }
        try
        {
            Status.Text = "";
            if (activeSp != null) { var old = activeSp; activeSp = null; await Safe(old.PauseAsync); }
            if (request != playRequest || closing) return;
            Status.Text = "Loading…";
            var url = await providers[t.Source].ResolveAsync(t);
            if (request != playRequest || closing) return;
            if (url == null) { Status.Text = "This track can't be streamed."; return; }
            if (engine == null) StartEngine();
            await engine!.Ready.WaitAsync(TimeSpan.FromSeconds(15));
            if (request != playRequest || closing) return;
            engine.Open(url);
            playStart = DateTime.UtcNow;
            Seek.IsEnabled = true; PlayBtn.IsEnabled = true;
            SetPlaying(true);
            Status.Text = "";
        }
        catch (Exception ex) { if (request == playRequest && !closing) { Status.Text = ex.Message; SetPlaying(false); } }
    }

    void SetPlaying(bool on)
    {
        if (on || (activeSp != null && activeSp.Desktop == null)) timer.Start(); else timer.Stop();
        if (playing == on) return;
        playing = on;
        PlayBtn.Content = on ? "\uE769" : "\uE768";
        double scale = on ? 1.0 : 0.88; // cover art swells while playing, settles when paused
        Anim(ArtScale, ScaleTransform.ScaleXProperty, scale, 500);
        Anim(ArtScale, ScaleTransform.ScaleYProperty, scale, 500);
    }

    async void PlayPause_Click(object s, RoutedEventArgs e)
    {
        if (followSpotify && desktop.Current == null)
        { Status.Text = "Spotify is not playing. Use SoundCloud or import local files for standalone playback."; return; }
        if (nowPlaying == null) return;
        bool resume = !playing;
        if (activeSp?.Desktop != null)
        {
            await Safe(() => resume ? desktop.ResumeAsync() : desktop.PauseAsync());
            return;
        }
        SetPlaying(resume);
        if (activeSp != null)
        {
            rPlaying = resume; rStamp = DateTime.UtcNow; lastPoll = DateTime.UtcNow;
            var sp = activeSp;
            await Safe(() => resume ? sp.ResumeAsync() : sp.PauseAsync());
        }
        else engine?.SetPaused(!resume);
    }

    void Step(int d)
    {
        int n = playbackQueue.Count;
        if (n == 0) return;
        queueIndex = (queueIndex + d + n) % n;
        _ = PlayAsync(playbackQueue[queueIndex], keepQueue: true);
    }
    async void Next_Click(object s, RoutedEventArgs e)
    { if (activeSp?.Desktop != null) await Safe(desktop.NextAsync); else Step(1); }
    async void Prev_Click(object s, RoutedEventArgs e)
    { if (activeSp?.Desktop != null) await Safe(desktop.PreviousAsync); else Step(-1); }

    void Seek_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updating) return;
        if (activeSp != null)
        {
            rProgress = (int)(e.NewValue * 1000); rStamp = DateTime.UtcNow; lastPoll = DateTime.UtcNow;
            seekDebounce.Stop(); seekDebounce.Start();
        }
        else if (engine != null) engine.TimeMs = (long)(e.NewValue * 1000);
    }

    void Volume_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (engine != null) engine.Volume = e.NewValue;
        if (activeSp != null) { volDebounce.Stop(); volDebounce.Start(); }
    }

    // ---- import ----
    void ShowTracks(List<Track> tracks, string message, string? title = null)
    {
        currentList = tracks;
        currentListTitle = title ?? "Imported music";
        SaveImportButton.IsEnabled = tracks.Count > 0 && tracks.All(t => t.Source != "Spotify");
        List.ItemsSource = tracks;
        Status.Text = message;
        listScroll?.ScrollToTop();
        _ = AnimateResultsAsync();
    }

    void ShowSavedImports()
    {
        currentList = null;
        SaveImportButton.IsEnabled = false;
        try
        {
            var lists = savedLibrary.Load();
            List.ItemsSource = lists;
            Status.Text = lists.Count == 0 ? "Search SoundCloud, or import a playlist or local files. Save a list to keep it here." : "Choose a saved import. Spotify is not needed.";
        }
        catch (Exception ex) { Status.Text = "Couldn't read saved imports: " + ex.Message; }
    }

    void SavedImports_Click(object sender, RoutedEventArgs e)
    {
        libraryRequest++;
        libraryCancellation?.Cancel();
        ShowSavedImports();
    }

    void SaveImport_Click(object sender, RoutedEventArgs e)
    {
        if (currentList is not { Count: > 0 } tracks) return;
        try
        {
            savedLibrary.Save(currentListTitle, tracks);
            SaveImportButton.IsEnabled = false;
            Status.Text = "Saved. Open Saved imports to return to this list.";
        }
        catch (Exception ex) { Status.Text = "Couldn't save this list: " + ex.Message; }
    }

    void Import_Click(object s, RoutedEventArgs e) => ToggleImport(true);
    void ImportDim_Click(object s, MouseButtonEventArgs e) => ToggleImport(false);
    void Import_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) ImportGo_Click(s, e); }

    void ToggleImport(bool open)
    {
        importOpen = open;
        if (open)
        {
            ImportOverlay.Visibility = Visibility.Visible;
            ImportStatus.Text = "";
            Anim(ImportCard, OpacityProperty, 1, 300);
            Anim(ImportScale, ScaleTransform.ScaleXProperty, 1, 380);
            Anim(ImportScale, ScaleTransform.ScaleYProperty, 1, 380);
            ImportBox.Focus();
        }
        else
        {
            Anim(ImportCard, OpacityProperty, 0, 200);
            Anim(ImportScale, ScaleTransform.ScaleXProperty, .94, 220);
            Anim(ImportScale, ScaleTransform.ScaleYProperty, .94, 220, () => { if (!importOpen) ImportOverlay.Visibility = Visibility.Collapsed; });
        }
    }

    void ImportFiles_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "Audio files|*.mp3;*.m4a;*.aac;*.wav;*.flac;*.ogg;*.opus;*.wma|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        var tracks = dlg.FileNames.Select(LocalFiles.ToTrack).ToList();
        SoundCloudBtn.IsChecked = true;
        libraryRequest++;
        libraryCancellation?.Cancel();
        ShowTracks(tracks, $"Imported {tracks.Count} file{(tracks.Count == 1 ? "" : "s")} · Save list to keep it", Path.GetFileName(Path.GetDirectoryName(dlg.FileNames[0])));
        ToggleImport(false);
    }

    async void ImportGo_Click(object s, RoutedEventArgs e)
    {
        var link = ImportBox.Text.Trim();
        if (link.Length == 0) return;
        ImportGo.IsEnabled = false;
        ImportStatus.Text = "Importing…";
        try
        {
            List<Track> tracks;
            var source = Importer.SourceFromLink(link);
            var sp = Regex.Match(new Uri(link).AbsolutePath, "^/playlist/([A-Za-z0-9]{22})/?$");
            if (source == "Spotify")
            {
                if (!sp.Success) throw new Exception("For Spotify, paste a playlist link.");
                if (!spotify!.SignedIn) throw new Exception("Connect your Spotify library in Settings first.");
                tracks = await spotify.PlaylistAsync(sp.Groups[1].Value);
            }
            else if (source is "YouTube Music" or "SoundCloud")
                tracks = await Importer.FromLinkAsync(link);
            else
                throw new Exception("Paste a link from YouTube, YouTube Music, SoundCloud or Spotify.");

            if (tracks.Count == 0) ImportStatus.Text = "Nothing found at that link.";
            else
            {
                libraryRequest++;
                libraryCancellation?.Cancel();
                var isSpotify = tracks[0].Source == "Spotify";
                if (isSpotify) SpotifyBtn.IsChecked = true;
                else SoundCloudBtn.IsChecked = true;
                libraryRequest++; // Invalidate any tab's pending search/library load.
                ShowTracks(tracks, isSpotify ? $"{tracks.Count} Spotify references · Select a song to find it on SoundCloud" : $"Imported {tracks.Count} available tracks · Save list to keep them", Importer.TitleFromLink(link));
                ImportBox.Text = "";
                ToggleImport(false);
            }
        }
        catch (Exception ex) { ImportStatus.Text = ex.Message; }
        finally { ImportGo.IsEnabled = true; }
    }

    // ---- settings sheet ----
    void Settings_Click(object s, RoutedEventArgs e) => ToggleSettings(!settingsOpen);
    void CloseSettings_Click(object s, RoutedEventArgs e) => ToggleSettings(false);
    void Dim_Click(object s, MouseButtonEventArgs e) => ToggleSettings(false);

    async void ToggleSettings(bool open)
    {
        settingsOpen = open;
        if (open)
        {
            Overlay.Visibility = Visibility.Visible;
            Anim(Dim, OpacityProperty, 1, 300);
            Anim(Sheet, OpacityProperty, 1, 350);
            Anim(SheetScale, ScaleTransform.ScaleXProperty, 1, 450);
            Anim(SheetScale, ScaleTransform.ScaleYProperty, 1, 450);
            await RefreshAccountsAsync();
        }
        else
        {
            Anim(Dim, OpacityProperty, 0, 250);
            Anim(Sheet, OpacityProperty, 0, 250);
            Anim(SheetScale, ScaleTransform.ScaleXProperty, .94, 280);
            Anim(SheetScale, ScaleTransform.ScaleYProperty, .94, 280, () => { if (!settingsOpen) Overlay.Visibility = Visibility.Collapsed; });
        }
    }

    void SetAvatar(string? url, string initial)
    {
        AvatarInitial.Text = initial;
        if (url != null)
        {
            try { AvatarBrush.ImageSource = new BitmapImage(new Uri(url)); AvatarInitial.Visibility = Visibility.Collapsed; return; }
            catch { }
        }
        AvatarBrush.ImageSource = null;
        AvatarInitial.Visibility = Visibility.Visible;
    }

    async Task RefreshAccountsAsync()
    {
        SoundCloudDetail.Text = "Play inside GlassPlayer without Spotify. Public links are supported; availability varies. Some SoundCloud songs require Go+ or are unavailable. This unofficial connection may change.";

        if (spotify == null || !spotify.Enabled)
        {
            SpotifyName.Text = "Spotify";
            SpotifyDetail.Text = "Spotify is unavailable.";
            SpotifyAction.Visibility = Visibility.Collapsed;
            SetAvatar(null, "S");
            return;
        }
        SpotifyAction.Visibility = Visibility.Visible;
        if (!spotify.SignedIn)
        {
            SpotifyName.Text = "Not connected";
            SpotifyDetail.Text = "Sign in to show playlists here. Playback uses the Spotify desktop app, including on Free accounts.";
            SpotifyAction.Content = "Connect library";
            SetAvatar(null, "S");
            return;
        }

        SpotifyAction.Content = "Disconnect";
        SpotifyName.Text = "Loading…";
        SpotifyDetail.Text = "";
        try
        {
            var pr = await spotify.ProfileAsync();
            if (pr == null)
            {
                SpotifyName.Text = "Connected";
                SpotifyDetail.Text = "Couldn't load your profile. Sign out and connect again.";
                SetAvatar(null, "S");
                return;
            }
            SpotifyName.Text = pr.Name;
            string plan = pr.Plan switch
            {
                "premium" => "Spotify Premium",
                null => "Library connected · audio plays in Spotify",
                _ => "Spotify Free · audio plays in Spotify"
            };
            SpotifyDetail.Text = pr.Country != null ? $"{plan} ({pr.Country})" : plan;
            SetAvatar(pr.Image, pr.Name.Length > 0 ? pr.Name[..1].ToUpper() : "S");
        }
        catch (Exception ex) { SpotifyName.Text = "Connected"; SpotifyDetail.Text = ex.Message; }
    }

    async void SpotifyAction_Click(object s, RoutedEventArgs e)
    {
        if (spotify == null) return;
        if (spotify.SignedIn)
        {
            if (activeSp != null) { await Safe(spotify.PauseAsync); activeSp = null; SetPlaying(false); }
            spotify.SignOut();
            libraryRequest++;
            libraryCancellation?.Cancel();
            if (provider == spotify) List.ItemsSource = null;
            await RefreshAccountsAsync();
            return;
        }
        SpotifyAction.IsEnabled = false;
        SpotifyDetail.Text = "Sign in in the temporary Spotify window. It closes when connected…";
        try { await spotify.ConnectAsync(); }
        catch (Exception ex) { SpotifyDetail.Text = ex.Message; SpotifyAction.IsEnabled = true; return; }
        SpotifyAction.IsEnabled = true;
        await RefreshAccountsAsync();
        if (provider == spotify) await LoadPlaylistsAsync();
    }

    void Setting_Click(object s, RoutedEventArgs e)
    {
        if (s is not ToggleButton { Tag: string key } tb) return;
        bool on = tb.IsChecked == true;
        switch (key)
        {
            case "Autoplay": cfg.Autoplay = on; break;
            case "Volume": cfg.RememberVolume = on; break;
            case "Glow": cfg.AmbientGlow = on; Anim(Ambient, OpacityProperty, on ? .18 : 0, 600); break;
            case "HideSpotify": cfg.HideSpotify = on; if (on && followSpotify && desktop.Current != null) HideSpotify(); else if (!on) desktop.RestoreHidden(); break;
        }
        cfg.Save();
    }

    void Close_Click(object s, RoutedEventArgs e) => Close();
    void Min_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
