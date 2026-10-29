// Modified 2026-10-26 by N3moStudio: native ARM64, performance, Spotify integration and packaging. Original work by Jonzu00; GPL-3.0.
using System;
using System.IO;
using System.Text.Json;

namespace GlassPlayer;

class AppSettings
{
    public bool Autoplay { get; set; } = true;
    public bool AmbientGlow { get; set; } = false;
    public bool RememberVolume { get; set; } = true;
    public double Volume { get; set; } = 0.8;
    public bool HideSpotify { get; set; } = true;
    public bool SilenceSpotifyAds { get; set; } = true;
    public bool AllowYouTubeMatches { get; set; } = true;
    // Tint only the backdrop; artwork and controls always stay fully opaque.
    public double GlassTint { get; set; } = 0.60;
    public string StartProvider { get; set; } = "SoundCloud";

    public static double NormalizeTint(double value) => double.IsFinite(value) ? Math.Clamp(value, 0.25, 0.95) : 0.60;

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
