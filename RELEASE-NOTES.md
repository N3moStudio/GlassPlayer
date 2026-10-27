GlassPlayer **0.2.0** brings the custom glass interface to native Windows ARM64 and x64. This is [N3moStudio's](https://github.com/N3moStudio) community fork of [Jonzu00/GlassPlayer](https://github.com/Jonzu00/GlassPlayer), with the original author credited and the GPL-3.0 license preserved.

## Download

| Your PC | Recommended installer | Portable version |
| --- | --- | --- |
| Snapdragon / Windows ARM | [ARM64 setup](https://github.com/N3moStudio/GlassPlayer/releases/download/v0.2.0/GlassPlayer-0.2.0-windows-arm64-setup.exe) | [ARM64 ZIP](https://github.com/N3moStudio/GlassPlayer/releases/download/v0.2.0/GlassPlayer-0.2.0-windows-arm64-portable.zip) |
| Intel / AMD 64-bit | [x64 setup](https://github.com/N3moStudio/GlassPlayer/releases/download/v0.2.0/GlassPlayer-0.2.0-windows-x64-setup.exe) | [x64 ZIP](https://github.com/N3moStudio/GlassPlayer/releases/download/v0.2.0/GlassPlayer-0.2.0-windows-x64-portable.zip) |

**You only need one installer.** Setup adds GlassPlayer to Start, app search and Installed apps, with an uninstaller and optional desktop shortcut. No administrator access or separate .NET installation is required.

`SHA256SUMS.txt` contains verification hashes for all five downloads. The dependency-source ZIP and GitHub's automatic source-code downloads are for developers and are not needed to install the app.

## What's included

- Native ARM64 application, .NET runtime and VLC audio libraries; x64 builds for Intel/AMD.
- Lazy initialization, reduced idle work and virtualized lists while preserving the glass interface.
- Spotify desktop playback controls, tray hiding and an experimental playlist browser.
- Blank Spotify configuration, per-user encrypted sessions and no bundled account credentials.
- Per-user Windows installation, settings-preserving upgrades and single-instance activation.

**Spotify must remain running to provide actual Spotify audio.** Connect the library through Settings using your own account; no developer Client ID is required. The temporary library sign-in window closes after connection.

The app is unsigned. On ARM, the application runs natively and only the installer uses Windows x86 compatibility temporarily. The unofficial library connection and English-label song-selection integration may break when Spotify changes its service or app. Selected-song activation, long-duration playback, ads and session refresh still need further real-account testing.

Local validation passed 24 checks, including native Windows ARM64 media/audio checks. On the development ARM64 PC, installation, Start menu and Installed apps registration, configuration-preserving upgrade, uninstall and reinstall passed, along with packaged startup, single-instance activation and clean shutdown. Real-account library loading and playback resumption while Spotify was hidden were observed. CI fixture and package checks are separate from clean-machine and real-account acceptance.

See the [README](https://github.com/N3moStudio/GlassPlayer#readme), [changelog](https://github.com/N3moStudio/GlassPlayer/blob/main/CHANGELOG.md) and [credits](https://github.com/N3moStudio/GlassPlayer/blob/main/CREDITS.md). Credits include Jonzu00, Shashwat / Anattana Labs, VideoLAN, NAudio and Microsoft/.NET contributors. GlassPlayer source is available at this release tag; the dependency-source ZIP includes the corresponding VLC and LibVLCSharp source archives. **GPL-3.0.**
