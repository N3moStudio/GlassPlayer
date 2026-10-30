using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassPlayer;

static class UiPreview
{
    // Renders only this app's visual tree. Never shows a window, reads the desktop,
    // initializes media sessions, signs in, sends input, or starts audio.
    public static Task RenderAsync(string directory)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
                void Drain()
                {
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
                    System.Windows.Threading.Dispatcher.PushFrame(frame);
                }
                Directory.CreateDirectory(directory);
                var settings = new AppSettings { AmbientGlow = false, GlassTint = .60, AllowYouTubeMatches = false };
                var window = new MainWindow(settings, new SavedLibrary(Path.Combine(directory, "fixture-library.json")), connectLibrary: false);
                T Element<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
                object? Field(object owner, string name) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);
                var root = Element<Grid>("Root");
                root.BeginAnimation(UIElement.OpacityProperty, null);
                root.Opacity = 1;
                root.RenderTransform = Transform.Identity;
                if (!window.AllowsTransparency || settings.GlassTint != .60 || Field(window, "activeSp") != null)
                    throw new Exception("Standalone startup or tint was changed");
                var tracks = new List<Track> { new("fixture", "Sample song", "Sample artist", null, null, "Spotify", "spotify:track:0123456789ABCDEFGHIJKL") };
                var list = Element<ListBox>("List");
                list.ItemsSource = tracks;
                list.SelectedIndex = 0;
                if (!Element<Button>("FindSoundCloudButton").IsEnabled || Field(window, "nowPlaying") != null || Field(Field(window, "desktop")!, "manager") != null)
                    throw new Exception("Selecting a Spotify reference initiated playback");
                var youtube = new DeferredProvider { Results = new() { tracks[0] with { Id = "yt-fixture", Source = "YouTube Music", DurationMs = 180000 } } };
                ((Dictionary<string, IProvider>)Field(window, "providers")!)["YouTube Music"] = youtube;
                settings.AllowYouTubeMatches = true;
                list.ItemsSource = new[] { tracks[0] with { DurationMs = 180000 } };
                list.SelectedIndex = 0;
                if (youtube.Pending.Count != 1 || ((Track?)Field(window, "nowPlaying"))?.Source != "Spotify"
                    || Field(window, "activeSp") != null || Field(Field(window, "desktop")!, "manager") != null)
                    throw new Exception("Spotify selection did not use standalone matching");
                youtube.Pending[0].SetResult(null);
                Drain();
                if (!Element<TextBlock>("Status").Text.Contains("unavailable")) throw new Exception("Unavailable match not reported");
                settings.AllowYouTubeMatches = false;
                var fake = new DeferredProvider();
                ((Dictionary<string, IProvider>)Field(window, "providers")!)["SoundCloud"] = fake;
                var showTracks = typeof(MainWindow).GetMethod("ShowTracks", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var first = new Track("first", "First", "Artist", null, null, "SoundCloud", null);
                var second = first with { Id = "second", Title = "Second" };
                showTracks.Invoke(window, new object?[] { new List<Track> { first, second }, "", "Queue", null, null });
                list.SelectedIndex = 0;
                list.SelectedIndex = 1;
                fake.Pending[0].SetException(new Exception("Stale failure"));
                Drain();
                if (Element<TextBlock>("Status").Text != "Loading…" || ((Track)Field(window, "nowPlaying")!).Id != "second")
                    throw new Exception("Stale track result replaced the newer selection");
                fake.Pending[1].SetResult(null);
                Drain();
                showTracks.Invoke(window, new object?[] { new List<Track> { first with { Id = "other", Title = "Other list" } }, "", "Other", null, null });
                typeof(MainWindow).GetMethod("Step", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { 1 });
                if (((Track)Field(window, "nowPlaying")!).Id != "first") throw new Exception("Browsing replaced the playback queue");
                fake.Pending[2].SetResult(null);
                Drain();
                Element<TextBox>("SearchBox").Text = "previous provider query";
                Element<RadioButton>("YouTubeBtn").IsChecked = true;
                Drain();
                if (Element<TextBox>("SearchBox").Text.Length != 0 || list.ItemsSource is IEnumerable<Track>)
                    throw new Exception("Provider switch retained stale search tracks");
                Element<TextBox>("SearchBox").Text = "another query";
                typeof(MainWindow).GetMethod("SavedImports_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { window, new RoutedEventArgs() });
                if (Element<TextBox>("SearchBox").Text.Length != 0 || list.ItemsSource is IEnumerable<Track>)
                    throw new Exception("Playlists did not clear search and return home");
                var delayedSearch = new TaskCompletionSource<List<Track>>();
                youtube.SearchCompletion = delayedSearch;
                Element<TextBox>("SearchBox").Text = "delayed";
                var searchTask = (Task)typeof(MainWindow).GetMethod("SearchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
                Element<RadioButton>("SoundCloudBtn").IsChecked = true;
                delayedSearch.SetResult(new() { tracks[0] });
                Drain();
                if (list.ItemsSource is IEnumerable<Track>) throw new Exception("Old provider search replaced the new provider home");
                var fixtureImagePath = Path.GetFullPath(Path.Combine(directory, "large-art.png"));
                var largeBitmap = BitmapSource.Create(2048, 2048, 96, 96, PixelFormats.Bgra32, null, new byte[2048 * 2048 * 4], 2048 * 4);
                var fixtureEncoder = new PngBitmapEncoder(); fixtureEncoder.Frames.Add(BitmapFrame.Create(largeBitmap));
                using (var fixtureStream = File.Create(fixtureImagePath)) fixtureEncoder.Save(fixtureStream);
                var thumb = (BitmapImage?)new ArtworkConverter().Convert(fixtureImagePath, typeof(ImageSource), null!, System.Globalization.CultureInfo.InvariantCulture);
                if (thumb?.PixelWidth != 96 || ArtworkConverter.Create(fixtureImagePath, 512)?.PixelWidth != 512)
                    throw new Exception("Artwork decoded at unbounded original resolution");
                list.SelectedIndex = -1;
                list.ItemsSource = Enumerable.Range(1, 8).Select(i => new Track(i.ToString(), "Track " + i, "Artist name", null, null, "SoundCloud", null, 185000)).ToList();
                Element<TextBlock>("TitleText").Text = "Your music, in glass";
                Element<TextBlock>("ArtistText").Text = "SoundCloud · standalone playback";
                Element<TextBlock>("SourceText").Text = "via YouTube Music";
                Element<TextBlock>("Status").Text = "Preview using sample metadata. No music is playing.";
                var surface = (FrameworkElement)window.Content;
                void Render(string name, int width, int height)
                {
                    surface.Measure(new Size(width, height));
                    surface.Arrange(new Rect(0, 0, width, height));
                    surface.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(surface);
                    using var stream = File.Create(Path.Combine(directory, name + ".png"));
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
                }
                Render("player-1020", 1020, 660);
                Render("player-860", 860, 580);
                var volume = Element<Slider>("Volume");
                if (volume.TransformToAncestor(surface).TransformBounds(new Rect(volume.RenderSize)).Bottom > 564)
                    throw new Exception("Volume control clipped at minimum window size");
                Element<Slider>("GlassTintSlider").Value = 25;
                if (settings.GlassTint != .25) throw new Exception("Tint slider did not update the backdrop");
                Render("player-clear", 1020, 660);
                Element<Slider>("GlassTintSlider").Value = 95;
                Render("player-readable", 1020, 660);
                Element<RadioButton>("SpotifyBtn").IsChecked = true;
                Drain();
                Render("spotify-sync-860", 860, 580);
                var syncButton = Element<Button>("SyncPlaylistsButton");
                if (Element<StackPanel>("LibraryBar").Visibility != Visibility.Visible || syncButton.RenderSize.Height < 1
                    || syncButton.TransformToAncestor(surface).TransformBounds(new Rect(syncButton.RenderSize)).Bottom > 564)
                    throw new Exception("Library sync action is not available at minimum size");
                Element<Grid>("Overlay").Visibility = Visibility.Visible;
                Element<Border>("Sheet").Opacity = 1;
                Element<Border>("Sheet").RenderTransform = Transform.Identity;
                Render("settings-860", 860, 580);
                ((ScrollViewer)Element<Border>("Sheet").Child).ScrollToBottom();
                Render("settings-appearance-860", 860, 580);
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.IsBackground = true;
        worker.Start();
        return completion.Task;
    }
}

sealed class DeferredProvider : IProvider
{
    public string Name => "SoundCloud";
    public bool Enabled => true;
    public List<TaskCompletionSource<string?>> Pending { get; } = new();
    public List<Track> Results { get; set; } = new();
    public TaskCompletionSource<List<Track>>? SearchCompletion { get; set; }
    public Task<List<Track>> SearchAsync(string query) => SearchCompletion?.Task ?? Task.FromResult(Results);
    public Task<string?> ResolveAsync(Track track)
    {
        var result = new TaskCompletionSource<string?>();
        Pending.Add(result);
        return result.Task;
    }
}
