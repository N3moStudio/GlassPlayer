using System.IO;
using System.Windows.Threading;
using GlassPlayer;

static class AudioSmoke
{
    public static Task RunAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var ui = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(ui));
            ui.InvokeAsync(async () =>
            {
                string path = Path.Combine(Path.GetTempPath(), "glass-audio-" + Guid.NewGuid().ToString("N") + ".wav");
                try
                {
                    // Silent PCM exercises the real output pipeline without disturbing the user.
                    using (var writer = new BinaryWriter(File.Create(path)))
                    {
                        int dataSize = 44100 * 2 * 5;
                        writer.Write("RIFF"u8); writer.Write(36 + dataSize); writer.Write("WAVEfmt "u8);
                        writer.Write(16); writer.Write((short)1); writer.Write((short)1);
                        writer.Write(44100); writer.Write(88200); writer.Write((short)2); writer.Write((short)16);
                        writer.Write("data"u8); writer.Write(dataSize); writer.Write(new byte[dataSize]);
                    }
                    using var engine = new AudioEngine(ui, 0);
                    string? error = null;
                    engine.Failed += message => error = message;
                    await engine.Ready.WaitAsync(TimeSpan.FromSeconds(15));
                    engine.Open(path);
                    await Task.Delay(1200);
                    if (!engine.UsingVlc || engine.TimeMs <= 0 || engine.LengthMs < 4000 || error != null)
                        throw new Exception($"Native decode failed: VLC={engine.UsingVlc}, position={engine.TimeMs}, length={engine.LengthMs}, error={error}");
                    engine.SetPaused(true);
                    await Task.Delay(300);
                    long pausedAt = engine.TimeMs;
                    await Task.Delay(400);
                    if (Math.Abs(engine.TimeMs - pausedAt) > 150) throw new Exception("Pause did not hold position");
                    engine.TimeMs = 2500;
                    engine.SetPaused(false);
                    await Task.Delay(600);
                    if (engine.TimeMs < 2400) throw new Exception("Seek/resume failed");
                    engine.Stop();
                    done.TrySetResult();
                }
                catch (Exception ex) { done.TrySetException(ex); }
                finally
                {
                    if (File.Exists(path)) File.Delete(path);
                    ui.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(25));
    }
}
