GlassPlayer **0.3.0** plays Spotify playlist entries through carefully matched YouTube audio, keeps Spotify closed, and adds clear window transparency.

## Changes

- Search SoundCloud or import public playlists and local files without the Spotify app. Save lists to return to them later with their order, names and artwork links.
- Keep the original playlist title and cover separately from song artwork. Separate playlists with identical songs retain their own names and covers.
- Silence clearly detected Spotify ads in optional desktop-controls mode. Music resumes after the ad break; the feature does not skip ads or work with Spotify closed.
- Play Spotify playlist entries and unavailable SoundCloud recordings through matching YouTube Music audio. Automatic matches require title, artist and duration agreement; ambiguous matches need a manual choice. Original metadata stays intact and the actual source is shown beneath the artist. Disable matching in Settings to keep Spotify entries as references.
- Save Spotify playlists with their original titles and covers. Search YouTube Music directly or use **Find on YouTube** for a selected song. Selecting songs never launches Spotify or clicks its controls.
- Adjust **Settings → Glass tint** to reveal more of the desktop while artwork and controls stay opaque. This is clear transparency, without desktop blur. Ambient glow is subtler, and volume stays visible at the minimum window size.
- Imports no longer stop at 100 tracks. Track selection ignores stale loading results, and next/previous stay with the playing list while you browse elsewhere.

## Limits and validation

Spotify Free itself still does not supply standalone audio; the audio source for Spotify entries is YouTube Music. These unofficial providers can change, and metadata matching cannot guarantee identical recordings or availability. **Show Spotify** is an optional desktop-control mode that requires Spotify to stay running. SoundCloud restrictions remain; matching does not decrypt protected SoundCloud tracks. Playlist transfers match recordings rather than copying Spotify audio; GlassPlayer does not transfer accounts itself.

Account-free tests cover matching, ambiguity, cancellation, original metadata, saved playlists, ad classification/mute restoration and offscreen layouts. A real MONTERO fallback passed muted native VLC decode, pause, seek and resume checks without opening Spotify or controlling the desktop. Real-ad detection, audible listening quality, live transparency, resizing and long-duration use remain unverified. GlassPlayer adds no display ads; permanently ad-free provider behavior is not guaranteed.

This community fork preserves [Jonzu00's original work](https://github.com/Jonzu00/GlassPlayer), credits and GPL-3.0 licensing. See README, changelog and third-party notices. These notes describe an unreleased development build; no public release has been published.
