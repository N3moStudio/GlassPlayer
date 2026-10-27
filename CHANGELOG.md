# Changelog

## 0.2.0-preview.1 — 2026-10-26

First packaged release of N3moStudio's community fork of Jonzu00/GlassPlayer.

### Windows application

- Separate self-contained ARM64 and x64 builds using .NET 10. The ARM64 player and VLC libraries run natively on Windows ARM.
- Per-user `.exe` installer, Start menu shortcut, Windows Installed apps entry, uninstaller, optional desktop shortcut, stable app identity and single-instance activation.
- Blank Spotify configuration in distributed files; personal sessions and settings stay outside the repository.
- Repeatable release build, architecture checks, fixture tests and SHA-256 checksums.

### Performance and reliability

- Lazy initialization of native audio and temporary browser components.
- Removed continuous ambient movement and expensive full-panel shadows while retaining the original glass interface.
- Smaller cached cover-art glow, virtualized lists, and reduced idle/minimized timer activity.
- Native ARM64 downloader when needed for the pre-existing non-Spotify providers.
- Better initialization, error reporting, native playback fallback and track-selection cancellation.

### Spotify

- Free-account audio through the official Spotify desktop app, controlled through Windows media sessions.
- Hide/restore Spotify using GlassPlayer's tray controls. Closing GlassPlayer restores windows it hid.
- Experimental library login without a developer Client ID, with playlists, Liked Songs, search and pagination.
- Temporary WebView2 sign-in/refresh windows close after capture. The saved session is protected for the current Windows user.
- Playback state follows Spotify, including unavailable controls and ads. Track selection uses Spotify's exact accessible Play control and verifies the reported playback state.
- Earlier Premium developer-API support remains in source, with PKCE, scoped encrypted authorization, pagination and rate-limit handling.

### Validation and known limits

- Local ARM64 validation: 24 automated checks passed, including native VLC decode/pause/seek/resume and Windows media-session initialization.
- A real account loaded 118 library entries and a five-track playlist; resuming Spotify playback with its window hidden was observed. No GlassPlayer library WebView process remained afterward.
- Selected-track activation, long-duration playback, ads, session refresh and clean-machine installation require their own acceptance checks; fixture results do not prove them.
- Spotify stays running and still consumes memory/CPU. The Android client's Widevine playback engine was not ported to Windows.
- The library protocol is unofficial and may change. Selected-track activation currently expects Spotify's English accessible button labels; unsupported or ambiguous controls produce an error instead of clicking another song.
- Preview binaries are not Authenticode-signed. The application is native ARM64; the small Inno Setup installer runs through Windows x86 compatibility on ARM.
