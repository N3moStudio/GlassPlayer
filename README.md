# GlassPlayer — Windows ARM64 community fork

A custom glass music-player interface for Windows 11, with native ARM64 and x64 builds.

**Originally created by [Jonzu00](https://github.com/Jonzu00/GlassPlayer).** This community fork is maintained by [N3moStudio](https://github.com/N3moStudio). It preserves the original interface and Git history while adding ARM64 support, performance improvements, a Spotify desktop connection and Windows installation packages. Licensed under **GPL-3.0**, not MIT. See [credits](CREDITS.md) and the [changelog](CHANGELOG.md).

## Download and install

Get a setup executable from [GitHub Releases](https://github.com/N3moStudio/GlassPlayer/releases).

| Your PC | Download |
| --- | --- |
| Snapdragon / Windows ARM | `GlassPlayer-…-windows-arm64-setup.exe` |
| Intel / AMD 64-bit Windows | `GlassPlayer-…-windows-x64-setup.exe` |
| Prefer no installation | The matching `portable.zip` |

Run setup to install for your Windows account. It adds **GlassPlayer** to Start and Windows app search, and registers an entry in **Settings → Apps → Installed apps**. An optional desktop shortcut is offered. Administrator access is not required. A newer installer upgrades the same installation and preserves settings. The standalone executable in a portable ZIP does not install itself.

Requirements: Windows 11, the appropriate CPU architecture, and Internet access for streaming. The application includes its .NET runtime. Microsoft's [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) is needed only for temporary Spotify library sign-in.

The **ARM64 application, .NET runtime and VLC libraries are native ARM64**. Inno Setup's short-lived installer uses Windows x64 compatibility on ARM. This preview is not code-signed, so Windows may identify its publisher as unknown. Compare downloads with the supplied SHA-256 checksum files.

## Spotify with a Free account

Spotify supplies the audio through its official desktop application. GlassPlayer supplies the custom interface and controls. Spotify continues running in the background; hiding its window does **not** eliminate its CPU or RAM usage. Ads and account restrictions remain in effect.

1. Install [Spotify for Windows](https://www.spotify.com/download/windows/), sign in, and start a song on this PC.
2. Open GlassPlayer. It reads Spotify's Windows media session for the actual title, artwork, playback state and available controls.
3. Open Settings → **Connect library**. Sign in to the **same Spotify account** in the temporary window. It closes when the library session is captured.
4. Choose **My playlists**, open a playlist, and select a song. GlassPlayer opens that track in Spotify, invokes its exact Play control when available, and checks the reported playback state.
5. Choose **Hide to tray**. The GlassPlayer tray menu can restore Spotify or GlassPlayer. Closing GlassPlayer restores Spotify windows it hid.

**No developer Client ID or Client Secret is needed for this mode.** The distributed `config.json` is blank. The desktop and library connections are separate sign-ins.

Playlist browsing is **experimental** and uses Spotify's unofficial web-library protocol. It can break when Spotify changes the service. A library request may briefly create a hidden temporary window to refresh an expired session, then dispose it. If interactive sign-in is required, reconnect in Settings. No persistent GlassPlayer browser is used for Spotify audio.

Selected-track activation currently expects Spotify's English accessible Play-button labels. If Spotify refuses the action, shows an ad, or exposes an ambiguous/localized control, GlassPlayer reports the problem instead of pressing another song's control. Spotify may briefly become visible while opening a selected track. Ordinary playback controls use Windows media sessions; volume changes Spotify's own audio session on the default output.

The tested desktop installation is Spotify's standard Windows download. Microsoft Store installations and non-English Spotify interfaces have not completed acceptance testing.

## Changes from upstream

- Self-contained .NET 10 builds for ARM64 and x64, with matching native audio libraries.
- Lazy audio/browser initialization, virtualized lists, fewer idle updates, and smaller cached glow effects.
- Continuous ambient movement and full-panel shadows removed while retaining the glass appearance.
- Spotify desktop controls, a recoverable tray menu, playlists, Liked Songs, search and complete-page loading.
- Encrypted per-user sessions, serialized authorization refresh, useful errors and request-limit backoff.
- Windows installation, stable application identity, single-instance activation, automated builds and package checks.

See [CHANGELOG.md](CHANGELOG.md) for the release description and [CREDITS.md](CREDITS.md) for attribution.

## Validation and limits

Local Windows ARM64 testing passed 24 checks, including real native VLC silent-PCM decode/pause/seek/resume and Windows media-session initialization. A signed-in Spotify account loaded more than 100 library entries and playlist tracks; resuming Spotify playback while its window was hidden was observed. No GlassPlayer library-browser process remained after sign-in.

CI runs account-free fixtures and checks packaged binary architecture. These checks do not prove Spotify account access, audible output, long-duration playback, advertisements, refresh after expiry, or every track's availability. Selected-track activation and installation/uninstallation have separate acceptance checks. This is a preview release, not a claim that every Spotify flow has been validated.

[Stopify](https://github.com/Anattana-Labs/Stopify) uses Android Media3 and the platform's Widevine DRM support. GlassPlayer's Windows audio backend does not provide that Android system. This fork adapts library/session ideas but does not port the Android audio decoder or provide an independent Free-account Spotify streaming engine. See [Android's DRM documentation](https://developer.android.com/media/media3/exoplayer/drm).

Earlier Spotify developer-API and Web Playback SDK code remains in source for reference and tests, but is inactive in the current UI. A Client ID alone does not authorize an account. Spotify's [Development Mode requirements](https://developer.spotify.com/documentation/web-api/concepts/quota-modes) and [Web Playback SDK requirements](https://developer.spotify.com/documentation/web-playback-sdk) apply to those integrations.

Local files and the pre-existing non-Spotify providers use GlassPlayer's audio engine. Spotify tracks are never replaced with matching audio from another service.

## Build from source

Install .NET 10 SDK and, for setup executables, Inno Setup 6.5 or newer. GitHub's Windows runner includes the installer compiler.

```powershell
dotnet run --project GlassPlayer.Tests -c Release -r win-arm64
./scripts/Build-Release.ps1 -Architecture arm64
```

Replace `arm64` with `x64` for Intel/AMD. `-SkipInstaller` builds a verified portable ZIP without Inno Setup. `--ci` on the test program skips the two interactive media/audio checks and labels them skipped. Use a fresh output directory for every release build.

The build copies only a clean publish tree, forces a blank Client ID, rejects account/debug files, and verifies native executable/runtime/VLC architecture. GitHub Actions produces installers and ZIPs. Version tags create preview releases. Corresponding GlassPlayer source is the release tag; upstream VLC and LibVLCSharp source snapshots accompany binary releases.

## Local data and removal

- Application: `%LocalAppData%\Programs\GlassPlayer`
- Settings: `%AppData%\GlassPlayer\settings.json`
- Library session/profile: `%LocalAppData%\GlassPlayer\spotify-library`
- Earlier encrypted API authorization: `%AppData%\GlassPlayer\spotify-v4-*.bin`
- Diagnostics and media cache: `%LocalAppData%\GlassPlayer`

Tokens are protected with Windows DPAPI for the current Windows user and are not logged or bundled. **Disconnect** forgets the saved library session and switches to a fresh browser profile. Uninstall through Windows Installed apps. Settings, caches and sign-in profiles are preserved for reinstall; remove these separately to erase local data.

## License

[GNU GPL-3.0](LICENSE), inherited from Jonzu00/GlassPlayer. This fork cannot relicense the original application as MIT. Dependencies retain their own licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and `licenses/`. This project is not endorsed by Spotify or the upstream author.
