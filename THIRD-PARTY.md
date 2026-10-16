# Third-party components / Сторонние компоненты

PrintMaster's own code is MIT-licensed. The following components retain their original licenses; MIT does not replace their terms. License texts and exact package provenance are preserved in [the license inventory](docs/licenses/LICENSE-MANIFEST.md) and `Assets/Licenses` in the application archive.

| Component | Version / use | License / source |
| --- | --- | --- |
| .NET / Windows Desktop | 8.0.26 runtime, WPF and System.Drawing.Common | MIT and third-party notices; [runtime](https://github.com/dotnet/runtime), [WPF](https://github.com/dotnet/wpf) |
| CommunityToolkit.Mvvm | 8.4.0, MVVM | MIT; [CommunityToolkit/dotnet](https://github.com/CommunityToolkit/dotnet) |
| PDFiumCore | 155.0.8057, PDF rendering bindings | Apache-2.0; [Dtronix/PDFiumCore](https://github.com/Dtronix/PDFiumCore) |
| PDFium / bblanchon binaries | Bundled x64 native PDF engine | PDFium BSD and dependency licenses; binary distribution Apache-2.0; [pdfium-binaries](https://github.com/bblanchon/pdfium-binaries) |
| PDFsharp | 6.2.4, raster PDF export | MIT, empira Software GmbH; [PDFsharp](https://github.com/empira/PDFsharp/tree/v6.2.4) |
| Markdig | 1.4.0, Markdown AST | BSD-2-Clause, Alexandre Mutel; [Markdig](https://github.com/xoofx/markdig/tree/1.4.0) |
| WpfMath / XamlMath.Shared | 2.1.0, mathematical layout | MIT; [upstream v2.1.0](https://github.com/ForNeVeR/wpf-math/tree/v2.1.0) |
| Math fonts | Original bundled Latin Modern and Computer Modern fonts | OFL-1.1 and Knuth license, preserved font notices |
| Lucide icons | Bundled SVG icons | ISC, with Feather-derived MIT notices; [Lucide license](https://github.com/lucide-icons/lucide/blob/main/LICENSE) |
| myPrinter / Smart Printer | Adapted manual-duplex ordering model | MIT, 2026 Smart Printer contributors; [original model](https://github.com/ndak79/myPrinter/blob/main/backend/Models/PrintModels.cs) |
| LibreOffice | 26.2.6 x64, DOCX conversion as a separate local process | MPL-2.0 / LGPLv3+ and dependency licenses; [licenses](https://www.libreoffice.org/about-us/licenses/), [corresponding source](https://download.documentfoundation.org/libreoffice/src/26.2.6/) |

Microsoft.Extensions.Logging.Abstractions, System.Security.Cryptography.Pkcs and their Microsoft dependencies retain .NET license notices. Test-only packages (xUnit and Microsoft.NET.Test.Sdk) are referenced in the source project and are not part of the application runtime.

## Local WpfMath adaptation

`src/ThirdParty/WpfMath` is based on upstream commit `d6f576345998777dcf7ad112da1add9bd7ad9674`. Its original algorithms, metrics and five TTF fonts remain unchanged. `StandaloneWpfMath.cs` adds independent embedded-font access after a WPF Application lifecycle. The patch, upstream license, font licenses and notices are included in the source and under `Assets/Licenses/WpfMathFork` in the application.

PrintMaster's Markdown adapters normalize standard matrix and cases environments into existing renderer primitives. They preserve the editable source and do not replace the renderer's mathematical algorithms or fonts.

Manual duplex was adapted from the MIT-licensed model at Git blob `1d9e834c9e8a739ad835acc259bd8f8be8935952`; its server, frontend and Office Interop are not bundled.

## LibreOffice distribution

The application archive includes the unmodified extracted LibreOffice distribution with its license and notice files. The pinned installer source and checksum are in `scripts/Setup-LibreOffice.ps1`. Corresponding source is available from The Document Foundation at the link above. Keep the LibreOffice folder and notices together when redistributing the portable archive.

## По-русски

MIT относится к собственному коду PrintMaster. Библиотеки, иконки, шрифты и LibreOffice сохраняют свои условия. В комплекте находятся тексты лицензий и уведомлений, ссылки на исходники и перечень закреплённых версий. Локальная адаптация WpfMath не меняет математические алгоритмы и шрифты; изменения описаны рядом с её исходниками. LibreOffice запускается отдельным локальным процессом; MS Office/Office Interop в комплекте нет.
