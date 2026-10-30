# Changelog

## 0.3.1 — 2026-10-29

- Stream audio instead of downloading the full song before playback; cancel abandoned helper processes and bound network waits.
- Recognize translated titles, accents and complete artist lists for Spotify matching; accept identical YouTube Music catalog copies while retaining version and duration checks.
- Detect SoundCloud previews, recover full-duration metadata for older 30-second imports, and seek a full-length match instead of playing excerpts.
- Clear stale search results on provider changes and add Playlists, Downloads and Refresh navigation.
- Show Spotify's first page before the full playlist finishes; keep only three small recent playlists briefly in memory.
- Save optional offline audio and cover art to Music\GlassPlayer; replay downloaded songs without a network resolver.
- Bound artwork decoding and caches, stop inactive scroll callbacks, and redact audio stream addresses from logs.


## 0.3.0 — 2026-10-29

- Normal ARM64/x64 installers upgrade the existing application while preserving per-user settings and saved libraries. No personal account data is included in the shared download.
- Add manual Sync playlists for each user's connected Spotify account, including names, covers, track order, duplicates and Liked Songs. Atomic saves preserve the previous library on failure or cancellation and leave other imports intact.
- Standalone SoundCloud-first startup. Spotify playlist entries play carefully matched YouTube Music audio without opening Spotify or invoking its controls. Optional desktop controls begin only after an explicit Show Spotify action.
- Match unavailable SoundCloud audio through YouTube Music using title, artist and bounded duration agreement. Preserve version names, reject ambiguous matches, and show the actual audio source without replacing playlist metadata. Add a matching toggle and manual YouTube search.
- Save Spotify playlist metadata and preserve protected SoundCloud track metadata for later matching; protected SoundCloud audio is not decrypted.
- Clear per-pixel window transparency with a persistent 25–95% background-tint slider, subtler optional glow, and smaller cover art at short window heights so transport and volume remain visible.
- Save imported lists and search results locally with order, duplicates, titles, artist names and artwork links; exclude temporary stream tokens and preserve a damaged library instead of overwriting it.
- Preserve exact imported playlist titles and independent playlist covers. Identify saved playlists by source link so separate lists with identical songs are not merged.
- Best-effort Spotify ad silencing in optional desktop-controls mode, with conservative metadata detection and restoration of only app-owned mute changes. This does not skip ad time or provide standalone Spotify Free streaming.
- Remove the 100-track import cap, reject lookalike import hosts, preserve original artist metadata, and surface incomplete import failures.
- Keep the playback queue stable while browsing and prevent an earlier slow-loading track from replacing a newer selection.
- Account-free fixtures and offscreen WPF previews cover startup, matching, ambiguity, cancellation, metadata persistence and minimum-size layout. Live MONTERO fallback passed muted native VLC decode, pause, seek and resume checks. Live desktop composition, audible quality, real ads and long-duration acceptance remain unverified.

## 0.2.0 — 2026-10-26

First packaged release of N3moStudio's community fork of Jonzu00/GlassPlayer.

### Windows application

- Separate self-contained ARM64 and x64 builds using .NET 10. The ARM64 player and VLC libraries run natively on Windows ARM.
- Per-user `.exe` installer, Start menu shortcut, Windows Installed apps entry, uninstaller, optional desktop shortcut, stable app identity and single-instance activation.
- Blank Spotify configuration in distributed files; personal sessions and settings stay outside the repository.
- Repeatable release build, architecture checks, fixture tests and SHA-256 checksums.
- Full release with versioned installer/portable filenames, one dependency-source ZIP and a single `SHA256SUMS.txt` file.

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
- Installation, Start menu/Installed apps registration, configuration-preserving upgrade, uninstall and reinstall passed on the development ARM64 PC. Packaged startup, single-instance activation and clean shutdown passed.
- Selected-track activation, long-duration playback, ads, session refresh and clean-machine installation require their own acceptance checks; fixture results do not prove them.
- Spotify stays running and still consumes memory/CPU. The Android client's Widevine playback engine was not ported to Windows.
- The library protocol is unofficial and may change. Selected-track activation currently expects Spotify's English accessible button labels; unsupported or ambiguous controls produce an error instead of clicking another song.
- Binaries are not Authenticode-signed. The application is native ARM64; the small Inno Setup installer runs through Windows x86 compatibility on ARM.
