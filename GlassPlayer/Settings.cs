// Modified 2026-10-26 by N3moStudio: native ARM64, performance, Spotify integration and packaging. Original work by Jonzu00; GPL-3.0.
using System;
using System.IO;
using System.Text.Json;

namespace GlassPlayer;

class AppSettings
{
    public bool Autoplay { get; set; } = true;
    public bool AmbientGlow { get; set; } = true;
    public bool RememberVolume { get; set; } = true;
    public double Volume { get; set; } = 0.8;
    public bool HideSpotify { get; set; } = true;

    static readonly string File_ = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlassPlayer", "settings.json");

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(File_)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
            File.WriteAllText(File_, JsonSerializer.Serialize(this));
        }
        catch { }
    }
}
