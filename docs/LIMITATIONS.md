# Known limitations

[Русский](LIMITATIONS.ru.md) · [Guide](GUIDE.md)

- Windows x64 only. The current UI is Russian; bilingual documentation does not change the interface language.
- HP LaserJet P1102 was the development printer. Other Windows drivers need separate checks, especially custom sizes, hardware margins and manual duplex order. A preview cannot verify paper orientation in the physical tray.
- DOCX uses LibreOffice, not Word. Font substitutions and complex document features can change layout. No Office Interop is used.
- PDF/DOCX pages are rendered pages. Their internal text is not edited as Word content. Added sheet objects remain editable in a project.
- Markdown is not a web browser. Supported HTML tags are `sub`, `sup`, `kbd`, `details`, `summary`, with no attributes. Inline formatting pairs must stay in the same paragraph. A summary belongs inside details. Details always prints expanded. Other HTML remains literal and raises a warning; it does not execute scripts or CSS.
- LaTeX support follows the bundled XamlMath renderer plus PrintMaster's adapters. It is not a full TeX installation and cannot load arbitrary packages. Standard matrix environments and `cases` are supported. The historical direct `\pmatrix{…}` command retains the upstream renderer's behaviour; use `\begin{pmatrix}…\end{pmatrix}` for round parentheses.
- Math is typeset, not solved. Invalid formulas block applying the document and retain their source.
- Code is monospace with a background; there is no syntax highlighting.
- Footnotes use an equal bottom reserve across pages to stabilize pagination. Definitions appear only on referenced pages/columns; repeated references on one page/column share an entry. A footnote too large to fit produces an error. Nested footnote references inside definitions are unsupported.
- Wide tables or formulas can require a smaller font, landscape paper or narrower margins. Screen previews are intentionally low resolution; inspect exported output at full size for print detail.
- Notebook ruling is a nonprinting placement guide. Exact line registration depends on measured paper, driver offsets and feeding variation. A spread does not auto-create another spread on overflow.
- PDF export rasterizes pages; it does not preserve searchable text or vector document structure.
- Project format 10 reads formats 1–10. Markdown pagination from older generations requires review and Apply before output. New projects need the new app; make a copy before opening valuable projects in a different version.
- Recovery can help restore recent work, but it is not a replacement for saving projects or making backups.
