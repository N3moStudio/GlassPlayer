GlassPlayer **0.3.0** makes standalone SoundCloud and local-file playback the default, adds clear window transparency, and removes automatic Spotify song activation.

## Changes

- Search SoundCloud or import public playlists and local files without the Spotify app. Save lists to return to them later with their order, names and artwork links.
- Browse Spotify playlists as references. Selecting a song no longer opens Spotify, clicks its Play button or hides its window. Use **Find selected on SoundCloud** to review possible recordings yourself.
- Adjust **Settings → Glass tint** to reveal more of the desktop while artwork and controls stay opaque. This is clear transparency, without desktop blur. Ambient glow is subtler, and volume stays visible at the minimum window size.
- Imports no longer stop at 100 tracks. Track selection ignores stale loading results, and next/previous stay with the playing list while you browse elsewhere.

## Limits and validation

Spotify Free still cannot provide standalone audio through Spotify's supported integration. **Show Spotify** is an explicit optional desktop-control mode and requires Spotify to stay running. SoundCloud catalog availability, account restrictions and Go+ requirements remain. Playlist transfers match recordings rather than copying Spotify audio; GlassPlayer does not transfer accounts itself.

Account-free tests and offscreen rendering cover metadata persistence, reference selection without launching Spotify, and default/minimum window layouts. Public SoundCloud search and a CC-BY track import returned metadata successfully. This does not verify audible playback, live transparency over the desktop, resizing or long-duration use. No desktop control or account sign-in was performed for this change.

This community fork preserves [Jonzu00's original work](https://github.com/Jonzu00/GlassPlayer), credits and GPL-3.0 licensing. See README, changelog and third-party notices. These notes describe an unreleased development build; no public release has been published.
