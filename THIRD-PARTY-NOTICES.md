# Third-party components

GlassPlayer remains GPL-3.0. Dependency licenses remain their own; this document does not relicense them.

| Component | Version | License / author | Source |
| --- | --- | --- | --- |
| Original GlassPlayer | `600c7ef` | GPL-3.0, Jonzu00 | https://github.com/Jonzu00/GlassPlayer |
| LibVLCSharp | 3.10.2 | LGPL-2.1-or-later, VideoLAN | https://code.videolan.org/videolan/LibVLCSharp |
| VideoLAN.LibVLC.Windows | 3.0.24 | LGPL-2.1-or-later package; bundled components retain their notices | https://code.videolan.org/videolan/libvlc-nuget |
| NAudio.Wasapi / NAudio.Core | 2.2.1 | MIT, Mark Heath and contributors | https://github.com/naudio/NAudio |
| Microsoft.Web.WebView2 SDK | 1.0.4314.35 | Microsoft redistribution license | https://www.nuget.org/packages/Microsoft.Web.WebView2 |
| .NET / WPF / Windows SDK projections | 10.0 runtime and resolved SDK packages | Microsoft and contributors; included notices | https://github.com/dotnet/runtime and https://github.com/dotnet/wpf |
| Stopify protocol references | `38da838` | MIT, Shashwat | https://github.com/Anattana-Labs/Stopify |
| Inno Setup | 6.x build tool | Jordan Russell / Martijn Laan, Inno Setup license | https://jrsoftware.org/ |

License texts are included under `licenses/`, alongside the runtime's own notices. Native VLC and LibVLCSharp are dynamically linked and remain replaceable. The release includes the upstream VLC source archive and LibVLCSharp source snapshot alongside GlassPlayer's tagged source. VLC's source contains its build instructions and third-party dependency recipes; see its `contrib/` directory.

The optional yt-dlp executable is downloaded only when a non-Spotify provider needs it. It is not included in the installer. See https://github.com/yt-dlp/yt-dlp for its source and licensing. Spotify and WebView2 Runtime are external prerequisites and are not bundled.
