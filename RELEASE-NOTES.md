GlassPlayer **0.3.1** fixes slow song starts, Spotify matching failures, SoundCloud preview playback and confusing library navigation.

Download the **ARM64 setup.exe** for Snapdragon/Windows ARM or **x64 setup.exe** for Intel/AMD. Setup upgrades the existing app and preserves settings, sign-in and saved playlists. Each user connects their own account through **Settings → Connect library → Spotify → Sync playlists**. Downloads contain no personal account data.

## Playback fixes

- Audio streams after resolution instead of waiting for a complete download. Switching tracks cancels abandoned helper processes, with bounded timeouts and small stream caches.
- Spotify matching now handles translated title prefixes, accents, artist-name order and multiple artists. Identical YouTube Music catalog copies can play; covers, version differences and conflicting durations remain guarded.
- SoundCloud previews are not treated as full songs. Older 30-second imports retrieve the full recording duration for YouTube matching. Some SoundCloud songs still play natively in full; availability is checked per recording.

## Navigation and offline listening

- Switching tabs clears the previous source's search. **Playlists** returns to the saved collection, and catalog search can find songs outside your playlists.
- Spotify shows the first page while the rest loads. A small recent-playlist cache speeds up revisits; **Refresh** checks for changes.
- **Download** saves available, unprotected audio under **Music\GlassPlayer**, with original metadata and optional cover art. Click again to cancel. **Downloads** provides local browsing/search, and original playlist entries prefer their saved file on replay. Matched downloads use the displayed YouTube audio source.

## Resource use and validation

Artwork is decoded at 96 pixels for thumbnails and 512 pixels for the player. Caches have fixed limits, disabled glow releases its image, and scrolling stops rendering when it reaches the end. These changes reduce avoidable work; no universal RAM percentage is claimed.

50 account-free checks cover matching, previews, offline persistence, cancellation, metadata, bounded caches and offscreen navigation/layout. Live muted native playback checks passed for Shinzo wo Sasageyo!, Escapism. and the full-length She Looks So Perfect match, with resolution around four seconds on the test connection. Offline playback is checked separately. Results do not establish every recording, other users' sign-ins, audible quality or long-duration stability.

SoundCloud and YouTube connections remain unofficial and can change. Matching is metadata-based, not proof of identical audio. Protected recordings and account/region restrictions are not unlocked. Permanently ad-free provider behavior is not guaranteed. This community fork preserves [Jonzu00's original work](https://github.com/Jonzu00/GlassPlayer), credits and GPL-3.0 licensing.
