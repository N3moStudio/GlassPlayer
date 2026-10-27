// Modified 2026-10-26 by N3moStudio: native ARM64, performance, Spotify integration and packaging. Original work by Jonzu00; GPL-3.0.
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace GlassPlayer;

public partial class App : Application
{
    Mutex? instance;
    bool ownsInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        SetCurrentProcessExplicitAppUserModelID("N3moStudio.GlassPlayer");
        instance = new Mutex(true, @"Local\N3moStudio.GlassPlayer", out ownsInstance);
        if (!ownsInstance)
        {
            foreach (var process in Process.GetProcessesByName("GlassPlayer"))
                using (process)
                {
                    if (process.Id == Environment.ProcessId || process.MainWindowHandle == IntPtr.Zero) continue;
                    ShowWindowAsync(process.MainWindowHandle, 9);
                    SetForegroundWindow(process.MainWindowHandle);
                    break;
                }
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsInstance) instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);

    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            Report(e.Exception);
            e.Handled = true;
            if (MainWindow == null || !MainWindow.IsVisible) Shutdown(); // failed while starting
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();
    }

    static void Report(Exception? ex)
    {
        string path = "";
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassPlayer");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, "crash.log");
            File.AppendAllText(path, $"{DateTime.Now}\n{ex}\n\n");
        }
        catch { }
        MessageBox.Show("Glass ran into a problem:\n\n" + ex?.Message + (path.Length > 0 ? "\n\nDetails saved to:\n" + path : ""), "Glass");
    }
}
