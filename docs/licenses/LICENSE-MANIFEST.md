# Bundled software license provenance

PrintMaster source is MIT licensed. Each dependency retains its own license; the root MIT license does not relicense third-party software. Texts below are copied without modification from the exact official NuGet package or upstream source. SHA-256 values are also recorded in [license-files.json](license-files.json).

| Component | Version | License files | Source |
|---|---|---|---|
| bblanchon.PDFium.Win32 | 155.0.8057 | [LICENSE-APACHE-2.0.txt](bblanchon.PDFium.Win32-155.0.8057/LICENSE-APACHE-2.0.txt) | [Official package/source](https://www.apache.org/licenses/LICENSE-2.0.txt) |
| CommunityToolkit.Mvvm | 8.4.0 | [License.md](CommunityToolkit.Mvvm-8.4.0/License.md), [ThirdPartyNotices.txt](CommunityToolkit.Mvvm-8.4.0/ThirdPartyNotices.txt) | [Official package/source](https://api.nuget.org/v3-flatcontainer/communitytoolkit.mvvm/8.4.0/communitytoolkit.mvvm.8.4.0.nupkg) |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 | [LICENSE.TXT](Microsoft.Extensions.DependencyInjection.Abstractions-8.0.2/LICENSE.TXT), [THIRD-PARTY-NOTICES.TXT](Microsoft.Extensions.DependencyInjection.Abstractions-8.0.2/THIRD-PARTY-NOTICES.TXT) | [Official package/source](https://api.nuget.org/v3-flatcontainer/microsoft.extensions.dependencyinjection.abstractions/8.0.2/microsoft.extensions.dependencyinjection.abstractions.8.0.2.nupkg) |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 | [LICENSE.TXT](Microsoft.Extensions.Logging.Abstractions-8.0.3/LICENSE.TXT), [THIRD-PARTY-NOTICES.TXT](Microsoft.Extensions.Logging.Abstractions-8.0.3/THIRD-PARTY-NOTICES.TXT) | [Official package/source](https://api.nuget.org/v3-flatcontainer/microsoft.extensions.logging.abstractions/8.0.3/microsoft.extensions.logging.abstractions.8.0.3.nupkg) |
| Microsoft.NETCore.App.Runtime.win-x64 | 8.0.26 | [LICENSE.TXT](Microsoft.NETCore.App.Runtime.win-x64-8.0.26/LICENSE.TXT), [THIRD-PARTY-NOTICES.TXT](Microsoft.NETCore.App.Runtime.win-x64-8.0.26/THIRD-PARTY-NOTICES.TXT) | [Official package/source](https://api.nuget.org/v3-flatcontainer/microsoft.netcore.app.runtime.win-x64/8.0.26/microsoft.netcore.app.runtime.win-x64.8.0.26.nupkg) |
| Microsoft.Win32.SystemEvents | 8.0.0 | [LICENSE.TXT](Microsoft.Win32.SystemEvents-8.0.0/LICENSE.TXT), [THIRD-PARTY-NOTICES.TXT](Microsoft.Win32.SystemEvents-8.0.0/THIRD-PARTY-NOTICES.TXT) | [Official package/source](https://api.nuget.org/v3-flatcontainer/microsoft.win32.systemevents/8.0.0/microsoft.win32.systemevents.8.0.0.nupkg) |
| Microsoft.WindowsDesktop.App.Runtime.win-x64 | 8.0.26 | [LICENSE](Microsoft.WindowsDesktop.App.Runtime.win-x64-8.0.26/LICENSE) | [Official package/source](https://api.nuget.org/v3-flatcontainer/microsoft.windowsdesktop.app.runtime.win-x64/8.0.26/microsoft.windowsdesktop.app.runtime.win-x64.8.0.26.nupkg) |
| PDFiumCore | 155.0.8057 | [LICENSE](PDFiumCore-155.0.8057/LICENSE) | [Official package/source](https://raw.githubusercontent.com/Dtronix/PDFiumCore/2d3d545889888a1c1bace599210446cd62038709/LICENSE) |
| System.Drawing.Common | 8.0.26 | [LICENSE.TXT](System.Drawing.Common-8.0.26/LICENSE.TXT), [THIRD-PARTY-NOTICES.TXT](System.Drawing.Common-8.0.26/THIRD-PARTY-NOTICES.TXT) | [Official package/source](https://api.nuget.org/v3-flatcontainer/system.drawing.common/8.0.26/system.drawing.common.8.0.26.nupkg) |
| System.Security.Cryptography.Pkcs | 8.0.1 | [LICENSE.TXT](System.Security.Cryptography.Pkcs-8.0.1/LICENSE.TXT), [THIRD-PARTY-NOTICES.TXT](System.Security.Cryptography.Pkcs-8.0.1/THIRD-PARTY-NOTICES.TXT) | [Official package/source](https://api.nuget.org/v3-flatcontainer/system.security.cryptography.pkcs/8.0.1/system.security.cryptography.pkcs.8.0.1.nupkg) |

Additional notices are retained in the source tree and portable application:

- `src/PrintMaster/Assets/Licenses/`: Markdig, PDFsharp, PDFium and its transitive libraries, WpfMath, XamlMath.Shared, and the adapted myPrinter logic.
- `src/PrintMaster/Assets/Icons/LICENSE`: Lucide and its Feather-derived assets.
- `src/ThirdParty/WpfMath/`: complete MIT license, fork notice, patch description, byte-exact upstream manifest, and font licenses. The local adaptation remains marked as modified.
- In the portable ZIP, corresponding application notices remain under `Assets/`.
- The self-contained .NET 8 runtime retains the Microsoft MIT and full third-party notice files above.

## LibreOffice executable and source availability

The portable Windows package includes the separately executed, unmodified LibreOffice 26.2.6 x64 installed program tree. The duplicate administrative MSI installer is not included. Its `license.txt`, `LICENSE.html`, `NOTICE`, `CREDITS.fodt`, and component license resources remain in `tools/LibreOffice/`. The component licenses in those files govern LibreOffice and its dependencies.

LibreOffice is available under the [Mozilla Public License 2.0 and other component licenses](https://www.libreoffice.org/licenses/). Corresponding official source archives are available from [The Document Foundation, release 26.2.6](https://download.documentfoundation.org/libreoffice/src/26.2.6/). This source link is supplied with the executable distribution; PrintMaster does not modify LibreOffice or claim ownership of it.

## PDFium package metadata

The exact `bblanchon.PDFium.Win32` 155.0.8057 NuGet metadata declares Apache-2.0; that license is included here. The PDFium distribution also includes an MIT wrapper notice and individual Chromium/PDFium dependency licenses, retained under `Assets/Licenses/pdfium/`. Both sets are preserved rather than treating the package expression as the license of every dependency.

## Integrity

| File | SHA-256 |
|---|---|
| `CommunityToolkit.Mvvm-8.4.0/License.md` | `651997ef19dbb9ecb8dd660dbc86444a65af21f18cfe08daa69bf201cd5cfdfc` |
| `CommunityToolkit.Mvvm-8.4.0/ThirdPartyNotices.txt` | `7774f8b0ab66bfb47ec154ce24a2d6d574febc44a74d940b8a037d1c56756163` |
| `Microsoft.NETCore.App.Runtime.win-x64-8.0.26/LICENSE.TXT` | `d7a68596ab69b06f51ca278a6545148e4269a9381c26d597c13df5d88e08cf5b` |
| `Microsoft.NETCore.App.Runtime.win-x64-8.0.26/THIRD-PARTY-NOTICES.TXT` | `b60b2912da28eaa6518593c9e2efb5334ee062d3c42e80d8fdfa806b3dc52977` |
| `Microsoft.WindowsDesktop.App.Runtime.win-x64-8.0.26/LICENSE` | `a89886665765362eb77e0f8e26602c924520041d1711b2eedc136434fe4d01ab` |
| `System.Drawing.Common-8.0.26/LICENSE.TXT` | `a89886665765362eb77e0f8e26602c924520041d1711b2eedc136434fe4d01ab` |
| `System.Drawing.Common-8.0.26/THIRD-PARTY-NOTICES.TXT` | `093e2589a27ed137e519b9856e425efa7982221a7b3d9e7cea3b5153f711905e` |
| `Microsoft.Extensions.Logging.Abstractions-8.0.3/LICENSE.TXT` | `d7a68596ab69b06f51ca278a6545148e4269a9381c26d597c13df5d88e08cf5b` |
| `Microsoft.Extensions.Logging.Abstractions-8.0.3/THIRD-PARTY-NOTICES.TXT` | `b60b2912da28eaa6518593c9e2efb5334ee062d3c42e80d8fdfa806b3dc52977` |
| `Microsoft.Extensions.DependencyInjection.Abstractions-8.0.2/LICENSE.TXT` | `d7a68596ab69b06f51ca278a6545148e4269a9381c26d597c13df5d88e08cf5b` |
| `Microsoft.Extensions.DependencyInjection.Abstractions-8.0.2/THIRD-PARTY-NOTICES.TXT` | `b60b2912da28eaa6518593c9e2efb5334ee062d3c42e80d8fdfa806b3dc52977` |
| `System.Security.Cryptography.Pkcs-8.0.1/LICENSE.TXT` | `d7a68596ab69b06f51ca278a6545148e4269a9381c26d597c13df5d88e08cf5b` |
| `System.Security.Cryptography.Pkcs-8.0.1/THIRD-PARTY-NOTICES.TXT` | `b60b2912da28eaa6518593c9e2efb5334ee062d3c42e80d8fdfa806b3dc52977` |
| `Microsoft.Win32.SystemEvents-8.0.0/LICENSE.TXT` | `d7a68596ab69b06f51ca278a6545148e4269a9381c26d597c13df5d88e08cf5b` |
| `Microsoft.Win32.SystemEvents-8.0.0/THIRD-PARTY-NOTICES.TXT` | `19c19dcac9f3ee6302cfbc6745bb8e79f08efc4c933eb8dc5509bf14b88347ec` |
| `PDFiumCore-155.0.8057/LICENSE` | `c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4` |
| `bblanchon.PDFium.Win32-155.0.8057/LICENSE-APACHE-2.0.txt` | `cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30` |
