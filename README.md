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

## Connect your own library

Every download starts without a bundled account or personal playlists. Open **Settings → Connect library**, sign in to your own Spotify account, then choose **Spotify → Sync playlists**. GlassPlayer saves playlist names, covers, song order and Liked Songs under your Windows account. Select a saved song to play a carefully matched YouTube recording without keeping Spotify open.

Choose **Sync playlists** again to refresh changes from Spotify. This is a manual, one-way refresh on this PC: it does not edit Spotify, transfer playlists to SoundCloud, or sync files between computers. Other saved imports are preserved. A failed or cancelled refresh leaves the previous library intact. Playlists removed from Spotify remain saved locally until managed separately. Your sign-in and library are separate from the application files, so installer upgrades preserve them.

## Standalone playback and glass appearance

GlassPlayer starts on **SoundCloud**. Search SoundCloud or YouTube Music, import playlist links or local audio files, or connect your Spotify library. Audio plays in GlassPlayer's own engine without the Spotify application. Choose **Save list** to keep the metadata, then **Saved imports** to reopen it after browsing or restarting. Track order, duplicate songs, names and artwork links are preserved; stream authorization tokens are not saved. The former 100-track import cap is removed. Import errors are reported instead of silently accepting incomplete output.

**Use matching YouTube audio** is enabled by default in Settings. Spotify entries use matching YouTube Music recordings; SoundCloud tracks try their original source first and use matching audio if that fails. Automatic matching requires a single recording with the same normalized title and artist, a known duration, and a length within 1.5% (minimum 1 second, maximum 2.5 seconds). Version words such as remix, live and clean are retained. Missing or ambiguous matches stop with **Find on YouTube** available for a manual search. This conservative metadata check is not proof of identical audio. Original playlist metadata remains unchanged and the player always shows **via YouTube Music** when that is the audio source. Successful matches are reused in memory, while downloaded audio uses the existing cache.

Imports preserve the playlist's exact title and its own cover when the source supplies one, with first-track artwork as a fallback. Reimporting the same source link updates that saved playlist; separate source playlists keep their own names and covers even when their songs are identical.

The background uses real per-pixel window transparency. Adjust **Settings → Glass tint** from 25% to 95%; this changes the dark background only, leaving controls and artwork opaque. Increase tint over bright or busy desktop content. Ambient glow is optional and much lighter than before. This clear-glass mode does not blur the desktop. Windows composition, GPU performance, resizing and accessibility on a real desktop still need acceptance testing.

The SoundCloud and YouTube Music connections are unofficial and can change. A link does not guarantee a full playable track: availability, regional restrictions and account requirements still apply. Protected recordings retain metadata for matching, but GlassPlayer does not decode their protected SoundCloud audio. Importing a playlist does not unlock restricted songs.

## Spotify library and optional desktop controls

Spotify itself does **not** supply standalone Free-account audio to GlassPlayer. Spotify playlist entries instead use the matching YouTube audio described above. The supported embedded Spotify player and selected-track Web API require Premium. The old workaround opened Spotify, pressed its accessible Play button and hid the window; that automation has been removed.

**Connect library** in Settings uses a temporary Spotify sign-in window to load your playlists, Liked Songs and search results without a developer Client ID. Selecting a song plays matching audio without launching Spotify. Save Spotify playlists with their names and covers to return to them without reconnecting the library. Turning off matching leaves Spotify entries as references; **Find on SoundCloud** and **Find on YouTube** allow a manual choice of recording.

The unofficial library session can expire; reconnect in Settings when needed. No persistent GlassPlayer browser is used for Spotify audio. Selecting the Spotify tab does not take control of another music session or hide any windows.

**Show Spotify** remains an explicit optional action. It opens the installed Spotify desktop app and connects Windows media-session controls for pause, resume, next, previous and seeking when Spotify permits them. These controls require Spotify to remain running. Ads and account restrictions remain. Hide to tray only hides its window; GlassPlayer restores windows it hid when it closes. Standalone playback and matching do not use these controls.

**Silence detected Spotify ads** is enabled by default for this optional controls mode. It mutes only Spotify audio sessions when Windows reports explicit advertisement metadata with song controls disabled, then restores the mute changes it made when music resumes, the setting is disabled, standalone playback starts, or GlassPlayer closes normally. It preserves sessions that were already muted. Detection is best effort and does not skip ad time, unlock Premium, or work with Spotify closed. GlassPlayer adds no display ads of its own; SoundCloud streaming is not guaranteed ad-free. Local audio files have no service-inserted ads.

### Moving playlists to SoundCloud

[Soundiiz's Spotify-to-SoundCloud transfer](https://soundiiz.com/tutorial/spotify-to-soundcloud) matches tracks in the destination catalog and leaves the Spotify originals in place. Its Free plan currently transfers one playlist at a time with up to 200 selected tracks per playlist. [Playlist cover copying](https://support.soundiiz.com/hc/en-us/articles/11147026144914-How-to-Change-or-Transfer-a-Playlist-Cover-with-Soundiiz) is best effort; the destination recordings determine track titles, artist metadata and album artwork. Exact recordings are not guaranteed, and [some SoundCloud tracks require Go+](https://help.soundcloud.com/hc/en-us/articles/31353227038747-Soundcloud-Go-General-FAQs).

After transferring and reviewing a playlist, import its SoundCloud link in GlassPlayer and save the list. A private playlist needs its full secret share link; ordinary private links that require account sign-in are unsupported. Treat secret links as private: they are stored locally with the saved playlist. GlassPlayer does not perform the account-to-account transfer itself.

## Changes from upstream

- Self-contained .NET 10 builds for ARM64 and x64, with matching native audio libraries.
- Lazy audio/browser initialization, virtualized lists, fewer idle updates, and smaller cached glow effects.
- Continuous ambient movement and full-panel shadows removed while retaining the glass appearance.
- Spotify desktop controls, a recoverable tray menu, playlists, Liked Songs, search and complete-page loading.
- Encrypted per-user sessions, serialized authorization refresh, useful errors and request-limit backoff.
- Windows installation, stable application identity, single-instance activation, automated builds and package checks.

See [CHANGELOG.md](CHANGELOG.md) for the release description and [CREDITS.md](CREDITS.md) for attribution.

## Validation and limits

The 0.3.0 checks cover account-free fixtures, conservative and ambiguous matching, cancellation, source attribution, saving Spotify references, separate playlist/song artwork, saved-list identity and legacy compatibility, complete imports, no Spotify launch during selection, ad classification/mute restoration, and offscreen WPF layout at default and minimum sizes. Live public SoundCloud metadata search/import succeeded. A protected SoundCloud recording of MONTERO matched a 138-second YouTube Music recording; muted native VLC playback decoded it, paused, sought and resumed successfully. This verifies one real recording, not the whole catalog or audible listening quality. No desktop input was used. Real-ad acceptance, live desktop transparency/resizing and long-duration playback remain unverified.

The 0.2.0 historical installation and real-account validation is documented in the changelog. Those results do not establish acceptance for this build. CI runs account-free fixtures and checks packaged binary architecture.

[Stopify](https://github.com/Anattana-Labs/Stopify) uses Android Media3 and the platform's Widevine DRM support. GlassPlayer's Windows audio backend does not provide that Android system. This fork adapts library/session ideas but does not port the Android audio decoder or provide an independent Free-account Spotify streaming engine. See [Android's DRM documentation](https://developer.android.com/media/media3/exoplayer/drm).

Earlier Spotify developer-API and Web Playback SDK code remains in source for reference and tests, but is inactive in the current UI. A Client ID alone does not authorize an account. Spotify's [Development Mode requirements](https://developer.spotify.com/documentation/web-api/concepts/quota-modes) and [Web Playback SDK requirements](https://developer.spotify.com/documentation/web-playback-sdk) apply to those integrations.

Local files, SoundCloud audio and YouTube matches use GlassPlayer's audio engine. The now-playing source label distinguishes the audio source from the original playlist metadata.

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
