using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace GlassPlayer;

interface IMuteSession : IDisposable
{
    string Id { get; }
    bool Muted { get; set; }
}

// Only owns mute changes it made itself. Never changes master volume or launches Spotify.
sealed class SpotifyAdSilencer(Func<IEnumerable<IMuteSession>>? sessions = null) : IDisposable
{
    readonly Func<IEnumerable<IMuteSession>> getSessions = sessions ?? SpotifySessions;
    readonly Dictionary<string, IMuteSession> owned = new();
    readonly object gate = new();
    bool disposed;

    internal static bool IsAdvertisement(SpotifyDesktop.State? state) => state != null &&
        !state.CanSeek && !state.CanNext &&
        (string.IsNullOrWhiteSpace(state.Artist) || state.Artist.Equals("Spotify", StringComparison.OrdinalIgnoreCase)) &&
        (state.Title.Trim().Equals("Advertisement", StringComparison.OrdinalIgnoreCase) ||
         state.Title.Trim().Equals("Spotify Advertisement", StringComparison.OrdinalIgnoreCase));

    public bool Apply(bool silence)
    {
        lock (gate)
        {
            if (disposed) return false;
            if (!silence) { Restore(); return false; }
            try
            {
                foreach (var session in getSessions())
                {
                    bool retained = false;
                    try
                    {
                        if (owned.ContainsKey(session.Id) || session.Muted) continue;
                        session.Muted = true;
                        owned.Add(session.Id, session);
                        retained = true;
                    }
                    finally { if (!retained) session.Dispose(); }
                }
            }
            catch { /* Devices can disappear during playback. Retry on the next state change. */ }
            return owned.Count > 0;
        }
    }

    void Restore()
    {
        foreach (var (id, session) in owned.ToArray())
        {
            try
            {
                if (session.Muted) session.Muted = false;
                owned.Remove(id);
                session.Dispose();
            }
            catch { /* Keep the handle to retry restoration on the next update or close. */ }
        }
    }

    static IEnumerable<IMuteSession> SpotifySessions()
    {
        using var devices = new MMDeviceEnumerator();
        foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    bool spotify = false;
                    try
                    {
                        using var process = Process.GetProcessById((int)session.GetProcessID);
                        spotify = process.ProcessName.Equals("Spotify", StringComparison.OrdinalIgnoreCase);
                    }
                    catch (ArgumentException) { }
                    if (spotify) yield return new WindowsMuteSession(device.ID, session);
                    else session.Dispose();
                }
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            Restore();
            foreach (var session in owned.Values) session.Dispose();
            owned.Clear();
            disposed = true;
        }
    }

    sealed class WindowsMuteSession(string device, AudioSessionControl session) : IMuteSession
    {
        public string Id => device + ":" + session.GetSessionInstanceIdentifier;
        public bool Muted { get => session.SimpleAudioVolume.Mute; set => session.SimpleAudioVolume.Mute = value; }
        public void Dispose() => session.Dispose();
    }
}
