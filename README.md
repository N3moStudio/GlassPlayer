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

The **ARM64 application, .NET runtime and VLC libraries are native ARM64**. Inno Setup's short-lived installer uses Windows x86 compatibility on ARM. The application is not code-signed, so Windows may identify its publisher as unknown. `SHA256SUMS.txt` lists checksums for all downloads. The dependency-source ZIP and GitHub's automatic source-code archives are for developers; you only need the installer for your PC.

## Standalone playback and glass appearance (0.3.0 source)

The current published release is 0.2.0. These changes are in the 0.3.0 development build.

GlassPlayer starts on **SoundCloud**. Search for a track or import a public SoundCloud playlist link; choose a song to play it in GlassPlayer's own audio engine. You can also import local audio files. Neither path requires the Spotify application. Choose **Save list** to keep the imported metadata, then **Saved imports** to reopen it after browsing or restarting. Track order, duplicate songs, names and artwork links are preserved; stream authorization tokens are not saved. The former 100-track import cap is removed. Import errors are reported instead of silently accepting incomplete output.

The background uses real per-pixel window transparency. Adjust **Settings → Glass tint** from 25% to 95%; this changes the dark background only, leaving controls and artwork opaque. Increase tint over bright or busy desktop content. Ambient glow is optional and much lighter than before. This clear-glass mode does not blur the desktop. Windows composition, GPU performance, resizing and accessibility on a real desktop still need acceptance testing.

SoundCloud's existing connection is unofficial and can change. A public link does not guarantee a full playable track: availability, regional restrictions and Go+ requirements still apply. Importing a playlist does not unlock restricted songs.

## Spotify library and optional desktop controls

Spotify Free does **not** provide standalone audio in GlassPlayer. The supported embedded Spotify player and selected-track Web API require Premium. The old workaround opened Spotify, pressed its accessible Play button and hid the window; that automation has been removed.

**Connect library** in Settings uses a temporary Spotify sign-in window to load your playlists, Liked Songs and search results without a developer Client ID. Browsing or selecting these entries never launches Spotify or starts playback. Choose **Find selected on SoundCloud** to search for the artist and title, then review and select a SoundCloud recording yourself. Versions, artwork and availability can differ; GlassPlayer does not silently substitute audio.

The unofficial library session can expire; reconnect in Settings when needed. No persistent GlassPlayer browser is used for Spotify audio. Selecting the Spotify tab does not take control of another music session or hide any windows.

**Show Spotify** remains an explicit optional action. It opens the installed Spotify desktop app and connects Windows media-session controls for pause, resume, next, previous and seeking when Spotify permits them. These controls require Spotify to remain running. Ads and account restrictions remain. Hide to tray only hides its window; GlassPlayer restores windows it hid when it closes. If you want Spotify completely closed, use SoundCloud or local files instead.

### Moving playlists to SoundCloud

[Soundiiz's Spotify-to-SoundCloud transfer](https://soundiiz.com/tutorial/spotify-to-soundcloud) matches tracks in the destination catalog and leaves the Spotify originals in place. Its Free plan currently transfers one playlist at a time with up to 200 selected tracks per playlist. [Playlist cover copying](https://support.soundiiz.com/hc/en-us/articles/11147026144914-How-to-Change-or-Transfer-a-Playlist-Cover-with-Soundiiz) is best effort; the destination recordings determine track titles, artist metadata and album artwork. Exact recordings are not guaranteed, and [some SoundCloud tracks require Go+](https://help.soundcloud.com/hc/en-us/articles/31353227038747-Soundcloud-Go-General-FAQs).

After transferring and reviewing a playlist, import its public SoundCloud link in GlassPlayer and save the list. Private/authenticated SoundCloud playlist import is not supported by this connection. GlassPlayer does not perform the account-to-account transfer itself.

## Changes from upstream

- Self-contained .NET 10 builds for ARM64 and x64, with matching native audio libraries.
- Lazy audio/browser initialization, virtualized lists, fewer idle updates, and smaller cached glow effects.
- Continuous ambient movement and full-panel shadows removed while retaining the glass appearance.
- Spotify desktop controls, a recoverable tray menu, playlists, Liked Songs, search and complete-page loading.
- Encrypted per-user sessions, serialized authorization refresh, useful errors and request-limit backoff.
- Windows installation, stable application identity, single-instance activation, automated builds and package checks.

See [CHANGELOG.md](CHANGELOG.md) for the release description and [CREDITS.md](CREDITS.md) for attribution.

## Validation and limits

The 0.3.0 checks cover account-free fixtures, saved playlist metadata, more-than-100-track parsing, rejecting invalid import hosts, no Spotify launch during reference selection, and offscreen WPF rendering at default and minimum sizes. Live public SoundCloud search and one CC-BY track import returned title, artist, artwork and duration successfully. No desktop input, signed-in account testing or audible playback was used for this change. Offscreen images verify layout and alpha output; they do not prove live desktop composition, window resizing, audio output or long-duration playback.

The 0.2.0 historical installation and real-account validation is documented in the changelog. Those results do not establish acceptance for this build. CI runs account-free fixtures and checks packaged binary architecture.

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

The build copies only a clean publish tree, forces a blank Client ID, rejects account/debug files, and verifies native executable/runtime/VLC architecture. GitHub Actions produces installers and ZIPs. Version tags create releases. Corresponding GlassPlayer source is the release tag; upstream VLC and LibVLCSharp source snapshots accompany binary releases in one dependency-source ZIP, with all download checksums in `SHA256SUMS.txt`.

## Local data and removal

- Application: `%LocalAppData%\Programs\GlassPlayer`
- Settings: `%AppData%\GlassPlayer\settings.json`
- Saved imports: `%AppData%\GlassPlayer\saved-imports.json`
- Library session/profile: `%LocalAppData%\GlassPlayer\spotify-library`
- Earlier encrypted API authorization: `%AppData%\GlassPlayer\spotify-v4-*.bin`
- Diagnostics and media cache: `%LocalAppData%\GlassPlayer`

Tokens are protected with Windows DPAPI for the current Windows user and are not logged or bundled. **Disconnect** forgets the saved library session and switches to a fresh browser profile. Uninstall through Windows Installed apps. Settings, caches and sign-in profiles are preserved for reinstall; remove these separately to erase local data.

## License

[GNU GPL-3.0](LICENSE), inherited from Jonzu00/GlassPlayer. This fork cannot relicense the original application as MIT. Dependencies retain their own licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and `licenses/`. This project is not endorsed by Spotify or the upstream author.
