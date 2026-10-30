GlassPlayer **0.3.2** adds bulk offline downloads, remembered song links and organized playlist browsing.

Download the **ARM64 setup.exe** for Snapdragon/Windows ARM or **x64 setup.exe** for Intel/AMD. Setup upgrades the existing app and preserves settings, sign-in and saved playlists. Everyone connects their own account; installers contain no personal library or credentials.

## Offline music

- **Settings → Download all songs** saves unique songs from your saved playlists, one at a time. Progress shows saved, existing and unavailable songs. Cancel keeps completed files; repeat the action to resume.
- **Resync playlists** refreshes the connected Spotify library and saved SoundCloud/YouTube links, then downloads missing songs. Existing audio is retained, including songs removed from an online playlist.
- **Downloads → All / SoundCloud / YouTube Music** filters by audio source. Spotify songs downloaded through a YouTube match appear under YouTube Music. Files and optional artwork live in **Music\GlassPlayer**.

## Choose a recording once

If a Spotify or SoundCloud song cannot be matched confidently, use **Find on YouTube**, choose the recording, then **Download and link**. GlassPlayer remembers the original song and uses your chosen local file next time.

For an existing download, select the original song → **Link a download** → select a file → **Link this download**. Multiple songs can use the same file. Links survive resync and work with automatic matching off. **Unlink** keeps the downloaded file. Missing linked files are reported rather than replaced with a guess.

## Playlist organization

Playlist covers, nested folders, breadcrumbs and **Back** make browsing clearer. Saved Spotify lists appear immediately while refresh runs. Each source shows its own saved lists by default; **All saved sources** includes everything, and Spotify has a **Saved** button for offline browsing.

The experimental Spotify web connector reads folder placement. Transferred SoundCloud/YouTube playlists with a unique exact Spotify name inherit that placement locally; later moves follow their saved Spotify playlist link. No external folders are created, and empty folders without playlists are not shown.

## Verification and limits

59 account-free checks passed, including manual links across restart, source filtering, sequential downloads, cancellation/resume, partial resync failure, nested folders, folder cycles and offscreen UI at the minimum window size. The real Spotify folder check was rejected by the available account session, so actual account folder synchronization is not claimed as verified. Interactive or audible playback was not performed for this release.

Offline metadata is cached, audio is copied in small buffers, folder requests are limited to two at a time, and list animations are shorter. No measured RAM percentage is claimed. Downloading a whole library is optional and never starts automatically.

SoundCloud and YouTube connections remain unofficial and may change. Only available, unprotected recordings can be saved; protected audio and account/region restrictions are not unlocked. Matching uses metadata and cannot prove identical audio. Permanently ad-free provider behavior is not guaranteed. This fork preserves [Jonzu00's original work](https://github.com/Jonzu00/GlassPlayer), attribution and GPL-3.0 licensing.
