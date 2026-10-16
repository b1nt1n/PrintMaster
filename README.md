<p align="center">
  <img src="images/logo.png" width="104" height="104" alt="PrintMaster icon">
</p>

<h1 align="center">PrintMaster</h1>
<p align="center"><a href="https://github.com/b1nt1n/PrintMaster/releases/download/v1.0.36/PrintMaster-1.0.36-win-x64.zip"><strong>Download Windows x64</strong></a> · <a href="https://github.com/b1nt1n/PrintMaster/releases/tag/v1.0.36">Release notes</a> · <a href="https://github.com/b1nt1n/PrintMaster/issues">Report a bug</a></p>
<p align="center"><strong>Your files. Your layout. Ready for paper.</strong></p>
<p align="center">A Windows workspace for batch printing, image annotation and Markdown with mathematics.<br>Built around the HP LaserJet Pro P1102.</p>

<p align="center">
  <img src="images/badge-version.svg" alt="Version 1.0.36">
  <img src="images/badge-platform.svg" alt="Windows x64">
  <img src="images/badge-license.svg" alt="MIT license for PrintMaster code">
</p>

<p align="center"><strong>English</strong> · <a href="README.ru.md">Русский</a></p>
<p align="center"><a href="#download-and-run">Download & run</a> · <a href="#a-look-inside">Screenshots</a> · <a href="#what-you-can-do">Features</a> · <a href="#questions">FAQ</a> · <a href="docs/BUILD.md">Build from source</a></p>

![PrintMaster with a prepared sheet and page queue](images/workspace.jpg)

## What's changed in 1.0.36

- Consistent Lucide icons across workspace commands, dialogs and right-click menus, with readable labels and matching button states.
- Safer page moves and notebook copy/cut operations: text flow, unselected content and overflow are preserved.
- Correct camera-photo rotation and mirroring on new imports; original files stay unchanged.
- Fixed copy-count boundaries, large text replacements, single-dot selection and cleanup after failed imports.

[Full release notes](CHANGELOG.md)

## What you can do

| Prepare | Arrange | Print |
| --- | --- | --- |
| Import PDF, DOCX, PNG, JPEG and BMP | Place pictures, text, arrows, shapes and freehand strokes on a sheet | Set copies separately for every page |
| Write Markdown with LaTeX mathematics | Crop with handles, rotate, resize and align content | Preview sharp text or photo dithering |
| Start from a blank A4, A5 or custom sheet | Flow text across notebook spreads | Use a paper-loading guide and manual duplex assistant |
| Reopen editable projects with their materials | Undo changes and paste into the focused workspace | Export prepared pages to PDF or images |

The application works locally. No account, cloud service or Microsoft Office installation is required. The current application interface is **Russian**; documentation is available in English and Russian.

## Download and run

1. Open this repository's **Releases** section and download `PrintMaster-1.0.36-win-x64.zip` from **Assets**.
2. Extract the entire archive to a writable folder. Keep its files and subfolders together.
3. Run `PrintMaster.exe`.
4. Install the Windows driver for your printer, then add files or choose **Создать лист** (Create sheet).
5. Open **Печать…** (Print), check the selected printer and paper, and print a small test first.

**Requirements:** Windows 10 or 11, x64, and an installed Windows printer driver. The portable archive includes the .NET desktop runtime, PDFium and LibreOffice for DOCX conversion. Download the application archive, rather than GitHub's automatically generated source-code archive.

[Getting started](docs/GUIDE.md) · [Known limitations](docs/LIMITATIONS.md) · [Changes in 1.0.36](CHANGELOG.md)

## A look inside

### Markdown that becomes a printed document

Keep the editable source beside a paginated preview. Inline and display mathematics, matrices, task lists, tables and page-bottom footnotes are rendered locally. PrintMaster formats the solution you provide; it does not solve equations.

![Markdown source and its print preview](images/markdown.jpg)

### A notebook sheet, with the right boundaries

Choose grid or ruled paper, single sheets or spreads, and adjust measurements to your notebook. Guides show the fold, ruling and red margins. They stay on screen; only the content you add is printed.

![A notebook spread with writing guides](images/notebook.jpg)

### Review before the printer starts

Check paper, copies, scaling and monochrome mode together. Saved page settings can override the job defaults. Printing a notebook sheet shows a loading guide; the duplex assistant pauses before the second pass.

![Print review and page settings](images/print-review.jpg)

## A short path from file to paper

```mermaid
flowchart LR
    A["Files or blank sheet"] --> B["Page queue"]
    B --> C["Layout and preview"]
    C --> D["Print or export"]
```

Shortcuts follow focus: **Ctrl+A**, **Ctrl+C**, **Ctrl+V** and **Delete** act on the queue, canvas or active text field. **Ctrl+wheel** zooms the canvas. **Ctrl+Z / Ctrl+Y** undo and redo. [More in the guide](docs/GUIDE.md#keyboard-and-mouse).

## Questions

<details>
<summary><strong>Is this only for the HP P1102?</strong></summary>

It was developed and physically tested around that printer. Other installed Windows printers can be selected, but their custom paper sizes, margins and manual duplex order need their own checks. This is an independent project, not HP software.
</details>

<details>
<summary><strong>Will notebook lines be printed again?</strong></summary>

No. Notebook ruling, fold and margin guides are for placement on already ruled paper. Only added text, pictures and drawings are printed. Measure your own notebook: dimensions and offsets vary between manufacturers.
</details>

<details>
<summary><strong>Why do old Markdown pages ask me to apply the source again?</strong></summary>

Pagination changed. Open the Markdown editor, review the full source, and press **Применить** (Apply). This explicitly regenerates the pages instead of silently losing text or restoring pages you intentionally deleted. Project format 10 reads formats 1–10; new projects require the new application.

Version 1.0.36 retains project format 10 and Markdown layout generation 3 from 1.0.35. Upgrading from 1.0.35 alone does not require another Apply step.
</details>

<details>
<summary><strong>Why is the download fairly large?</strong></summary>

It includes the Windows .NET runtime and a local LibreOffice distribution for DOCX conversion. You can run the extracted application without installing either separately. Keep the complete folder when moving it.
</details>

<details>
<summary><strong>Does it support arbitrary HTML or every LaTeX package?</strong></summary>

No. It supports a documented Markdown subset, common math commands and a small set of HTML formatting tags. Unsupported formulas produce a visible error and retain the editable source. Code uses a monospace font without syntax highlighting. See [limitations](docs/LIMITATIONS.md).
</details>

## Development and feedback

[Build and test](docs/BUILD.md) · [Contributing](CONTRIBUTING.md) · [Release history](CHANGELOG.md)

When reporting a problem, include the app version, steps, expected result and a small non-private example. For printing issues, add the printer driver, paper dimensions, orientation and monochrome mode. A preview screenshot helps; never post private documents.

## License and acknowledgements

PrintMaster's original code is available under the [MIT license](LICENSE). Bundled libraries, fonts, icons and LibreOffice retain their respective licenses. See [third-party components](THIRD-PARTY.md) and [license inventory](docs/licenses/LICENSE-MANIFEST.md).
