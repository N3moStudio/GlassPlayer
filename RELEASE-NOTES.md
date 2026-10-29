GlassPlayer **0.3.0** plays Spotify playlist entries through carefully matched YouTube audio, keeps Spotify closed, and adds clear window transparency.

Download the **ARM64 setup.exe** for Snapdragon/Windows ARM or the **x64 setup.exe** for Intel/AMD. Setup upgrades the existing installation and keeps your saved library and settings. Portable ZIPs are optional.

Each user connects their own account: **Settings → Connect library → Spotify → Sync playlists**. Sync saves names, covers, track order and Liked Songs locally. Run it again to refresh Spotify changes. No personal account, session, playlist library or audio cache is bundled with the downloads.

## Changes

- Search SoundCloud or import public playlists and local files without the Spotify app. Save lists to return to them later with their order, names and artwork links.
- Keep the original playlist title and cover separately from song artwork. Separate playlists with identical songs retain their own names and covers.
- Silence clearly detected Spotify ads in optional desktop-controls mode. Music resumes after the ad break; the feature does not skip ads or work with Spotify closed.
- Play Spotify playlist entries and unavailable SoundCloud recordings through matching YouTube Music audio. Automatic matches require title, artist and duration agreement; ambiguous matches need a manual choice. Original metadata stays intact and the actual source is shown beneath the artist. Disable matching in Settings to keep Spotify entries as references.
- Save Spotify playlists with their original titles and covers. Search YouTube Music directly or use **Find on YouTube** for a selected song. Selecting songs never launches Spotify or clicks its controls.
- Sync the connected account's playlist metadata with one atomic save. Cancellation or a failed request preserves the existing library. This manual refresh does not change Spotify or synchronize multiple computers.
- Adjust **Settings → Glass tint** to reveal more of the desktop while artwork and controls stay opaque. This is clear transparency, without desktop blur. Ambient glow is subtler, and volume stays visible at the minimum window size.
- Imports no longer stop at 100 tracks. Track selection ignores stale loading results, and next/previous stay with the playing list while you browse elsewhere.

## Limits and validation

Spotify Free itself still does not supply standalone audio; the audio source for Spotify entries is YouTube Music. These unofficial providers can change, and metadata matching cannot guarantee identical recordings or availability. **Show Spotify** is an optional desktop-control mode that requires Spotify to stay running. SoundCloud restrictions remain; matching does not decrypt protected SoundCloud tracks. Playlist transfers match recordings rather than copying Spotify audio; GlassPlayer does not transfer accounts itself.

Account-free tests cover matching, ambiguity, cancellation, atomic library sync, original metadata, saved playlists, ad classification/mute restoration and offscreen layouts. A real MONTERO fallback passed muted native VLC decode, pause, seek and resume checks without opening Spotify or controlling the desktop. Real-ad detection, other users' account sign-ins, audible listening quality, live transparency, resizing and long-duration use remain unverified. GlassPlayer adds no display ads; permanently ad-free provider behavior is not guaranteed.

This community fork preserves [Jonzu00's original work](https://github.com/Jonzu00/GlassPlayer), credits and GPL-3.0 licensing. See README, changelog and third-party notices.
