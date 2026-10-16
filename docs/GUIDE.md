# Getting started

[Русский](GUIDE.ru.md) · [Home](../README.md)

## Queue, canvas, properties

The left panel contains pages. The centre shows the selected sheet; the right panel shows its properties or the selected object's controls. Use **Страницы** and **Свойства** to show or hide side panels.

Drag PDF, DOCX, PNG, JPEG or BMP files into the application, or choose **Добавить файлы**. PDF and DOCX are split into pages. DOCX is converted through bundled LibreOffice; fonts and unusual Word layouts can differ from Microsoft Word, so review the result.

Select which pages will print with their checkboxes. Set copies per page. Highlighting pages for editing and including them for printing are separate actions. Reorder pages with the queue commands or its context menu.

## Create a sheet

Choose **Создать лист** and a paper format. Add a text object, draw a shape or insert a picture. Objects can be selected, moved, resized and aligned. The safe-area frame represents the usable printing area; printer hardware margins still apply.

Use the image crop tool's handles and aspect-ratio controls. Cropping is editable; it does not rewrite the original image. Use **Сохранить копию** or export when you want a flattened result. Saving a `.printmaster` project preserves editable content and its materials.

**Fit** keeps the complete image and its proportions. **Fill** fills the target area and can crop content. **Stretch** fills the area without cropping, but changes proportions. Check the result in the preview.

## Markdown and mathematics

Choose **Проект → Создать Markdown…**, import a `.md` file or use **Правка → Вставить как Markdown…**. Edit the source, check its paginated preview and press **Применить**.

```markdown
# A printed note

Inline mathematics: $a^2+b^2=c^2$.

$$
\int x^n\,dx=\frac{x^{n+1}}{n+1}+C,\quad n\ne -1
$$

- [x] Prepared
- [ ] Printed

Water: H<sub>2</sub>O. Shortcut: <kbd>Ctrl</kbd>+<kbd>P</kbd>.

A footnote[^note].

[^note]: This definition belongs at the bottom of the referenced page.
```

Supported matrices include `matrix`, `pmatrix`, `bmatrix`, `Bmatrix`, `vmatrix` and `Vmatrix`. `cases` formats piecewise expressions. The supported HTML subset is `sub`, `sup`, `kbd`, `details` and `summary`, without attributes. Details content is always expanded on paper. Unsupported or unpaired tags remain literal with a warning. Code fences and escaped examples stay literal.

A formula error shows its source line and blocks applying the invalid document. Correct the source or save it as `.md`; cancelling leaves previously applied pages available. PrintMaster formats supplied solutions and does not perform algebra or solve exercises.

If an older project requests a Markdown update, reopen the editor, review the source and apply it. [Complete limits](LIMITATIONS.md).

## Notebook paper

Choose **Тетрадный лист**, then grid/ruled and single/spread. Starting sizes are approximately 167 × 208 mm for a single sheet and 334 × 208 mm for a spread. They are presets, not a universal notebook standard. Adjust size, ruling, red margins, fold and writing offsets to your own paper.

Notebook guides are screen-only. Text can continue from the left half of a spread to the right. Overflow is shown; it does not silently create another spread. Printing only the part that fits requires confirmation.

For the P1102, feed the spread with the 208 mm edge first. Follow the application's paper-loading diagram and centre the paper between the guides. Use flat, loose paper within the printer's supported media limits. The app cannot verify how paper was physically loaded.

## Review and print

Open **Печать…**. Select the printer, paper, orientation, scaling, DPI and monochrome mode. Individual page settings can override inherited job settings; prepared sheets retain their layout dimensions.

- **Auto:** uses the page content to choose text/photo handling.
- **Text:** thresholded black and white for sharp text and line art; grey details can disappear.
- **Photo:** error-diffusion dithering for halftones; it cannot restore missing source detail.

Only a lightweight screen preview is kept for display. Print rendering uses one high-resolution page at a time. A sleeping laser printer can take time to warm up; sending a job does not prove the sheet has physically printed.

For manual duplex, follow the two-pass assistant and wait for every first-side sheet before reloading. The P1102 order was calibrated during development; other drivers or feed arrangements need a fresh two-sheet test.

## Keyboard and mouse

| Shortcut | Meaning |
| --- | --- |
| Ctrl+A | Select pages, canvas objects or text in the focused area |
| Ctrl+C / Ctrl+V | Copy/paste according to focus; paste into the canvas to add to the current sheet |
| Delete | Delete highlighted pages or selected objects; edits text when a text field has focus |
| Ctrl+Z / Ctrl+Y | Undo / redo; text fields retain their own editing shortcuts |
| Ctrl+mouse wheel | Zoom the canvas |
| V / C | Selection / crop tool when the canvas has focus |
| B / M / E / Shift+E | Brush / marker / partial eraser / whole-object deletion |
| I / Esc | Colour sampler / cancel the current transient action |
| Alt while dragging | Temporarily disable snapping |
| Right mouse button | Context actions for the queue or selected objects |

## Save and export

Save a project to return to editable sheets. Export PDF or images for sharing or final printing elsewhere. PDF export rasterizes the prepared pages at the selected resolution: it is not a searchable or editable-text PDF.

For printer or layout problems, report a small generated example, app version, paper dimensions, driver and settings. Keep private documents out of public reports.
