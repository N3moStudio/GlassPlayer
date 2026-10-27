Native Windows ARM64 and x64 preview of **N3moStudio's community fork of [Jonzu00/GlassPlayer](https://github.com/Jonzu00/GlassPlayer)**. The original glass interface, attribution, Git history and GPL-3.0 license are preserved.

Download `windows-arm64-setup.exe` for Snapdragon/Windows ARM, or `windows-x64-setup.exe` for Intel/AMD. Setup adds GlassPlayer to Start, app search and Installed apps, with an uninstaller and optional desktop shortcut. Portable ZIPs are also available. No administrator access or separate .NET installation is required.

This release adds native ARM64 audio libraries, lazy initialization, reduced idle work, virtualized lists, Spotify desktop media controls and tray hiding, and an experimental playlist browser. **Spotify must remain running to provide actual Spotify audio.** Connect the library through Settings; no developer Client ID is required. Your own account is used, and no personal credentials are included.

The ARM64 application runs natively; its installer uses Windows x64 compatibility temporarily. The preview is unsigned. Verify downloads using the attached `.sha256` files.

Local validation passed 24 checks, including native Windows ARM64 media/audio checks. Real-account library loading and playback resumption while Spotify was hidden were observed. CI checks are separate from clean-machine, long-duration and real-account acceptance. The unofficial library connection and English-label song-selection integration can change or fail when Spotify changes its app. See the [README](https://github.com/N3moStudio/GlassPlayer#readme) and [changelog](https://github.com/N3moStudio/GlassPlayer/blob/main/CHANGELOG.md) for limitations.

Source for this fork is attached automatically by GitHub at this tag. Upstream VLC and LibVLCSharp source archives accompany the binaries. Credits include Jonzu00, Shashwat / Anattana Labs (Stopify protocol references), VideoLAN, NAudio and Microsoft/.NET contributors. **GPL-3.0, not MIT.**
