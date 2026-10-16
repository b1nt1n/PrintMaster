# Changelog / Изменения

## 1.0.36 — code review and UI consistency / ревью кода и единый интерфейс

### English

- Use consistent Lucide icons and labels in workspace commands, dialogs and context menus. Match foreground, disabled and selected states; wrap long captions in narrow controls.
- Apply the shared window style explicitly so dialogs use the PrintMaster title-bar icon and consistent base typography.
- Correct Russian quantity forms for pages, impressions and sheets in queue, print, export and paper-loading summaries.
- Fixed page moves and right-click group selection after editing text with continuation. Pages keep their order and unique IDs; shortcuts respect focus outside the workspace.
- Text in duplicated notebook pages can be edited independently of the original. Copied fragments become standalone text; cutting removes only the selected text and preserves other columns and hidden overflow.
- Guard the 1–999 copy-count boundaries. Correct the text-paste limit for replacement selections and make single-point brush strokes selectable and erasable.
- Apply EXIF rotation/mirroring to new camera-image imports before creating crop coordinates. Originals and existing project materials remain unchanged; normalized working copies use PNG with the correct physical resolution.
- Discard failed/cancelled import transactions, clear missing LibreOffice paths without losing other preferences, and prevent diagnostic-log failures from replacing the original application error.
- Keep project format 10, Markdown layout generation 3 and Markdown clipboard protocol 5 unchanged from 1.0.35. No new reflow is required solely for this upgrade.

### Русский

- Добавлены единые значки Lucide и подписи команд в рабочем окне, диалогах и меню ПКМ. Согласованы цвета и состояния кнопок; длинные подписи переносятся в узких элементах.
- Общий стиль окон применяется явно: диалоги используют иконку PrintMaster в заголовке и согласованные базовые параметры шрифта.
- Исправлены русские формы количества страниц, оттисков и листов в очереди, печати, экспорте и подсказках загрузки бумаги.
- Исправлены перестановка страниц и сохранение выбранной группы через ПКМ после редактирования текста с продолжением. Страницы сохраняют порядок и уникальные ID; горячие клавиши учитывают фокус вне рабочей области.
- Текст в дубликате тетрадного листа редактируется независимо от оригинала. Отдельные фрагменты копируются как самостоятельный текст; вырезание удаляет только выделенный текст, сохраняя другие колонки и невидимое переполнение.
- Защищены границы количества копий 1–999. Исправлены лимит вставки поверх выделения и выбор/удаление одиночной точки кисти.
- Поворот и отражение EXIF применяются при новом импорте фото до создания координат кропа. Оригиналы и материалы старых проектов не меняются; рабочая копия сохраняется в PNG с корректным физическим разрешением.
- Незавершённый импорт очищает собственные временные файлы. Отсутствующий LibreOffice больше не мешает сохранить другие настройки; ошибка журнала не подменяет исходную ошибку приложения.
- Сохранены формат проекта 10, поколение разметки Markdown 3 и протокол буфера Markdown 5 из 1.0.35. Повторно применять Markdown только из-за обновления не нужно.

## 1.0.35 — release candidate / подготовка первого публичного релиза

### English

- Correct standard matrix delimiters for `matrix`, `pmatrix`, `bmatrix`, `Bmatrix`, `vmatrix` and `Vmatrix` without rewriting the editable source.
- Support a small HTML formatting subset in Markdown: subscripts, superscripts, key labels and expanded details with summaries.
- Preserve code literals, unsupported HTML warnings, formula error locations, images and page-bottom footnotes.
- Explicitly reapply older Markdown pagination before printing or exporting. Project format 10 reads 1–10; Markdown object clipboard protocol 5 reads 1–5.
- Prepare a clean source distribution, bilingual documentation, real application screenshots and an offline Windows x64 archive.

### Русский

- Исправлены скобки стандартных матриц `matrix`, `pmatrix`, `bmatrix`, `Bmatrix`, `vmatrix`, `Vmatrix` без изменения редактируемого исходника.
- Добавлены нижние/верхние индексы, обозначения клавиш и раскрытые блоки details с заголовками в Markdown.
- Сохранены буквальный код, предупреждения о неподдерживаемом HTML, номера строк ошибок, изображения и сноски внизу страниц.
- Старый Markdown требует явного повторного применения перед печатью или экспортом. Проект 10 читает 1–10; буфер Markdown 5 читает 1–5.
- Подготовлены чистые исходники, документация RU/EN, реальные скриншоты и автономный архив Windows x64.

## Before the public release / До публичного релиза

PrintMaster grew through local 1.0.x versions: a page queue and on-demand printing, editable sheets, image cropping and annotation, focus-aware shortcuts, project recovery, export, notebook paper profiles and manual duplex, Markdown and per-page print settings. Version 1.0.34 added task-list checkboxes, table alignment, strikethrough, code tab stops, page-bottom footnotes, formula error warnings and display-math centring.

Локальные версии 1.0.x добавили очередь и постраничную печать, редактируемые листы, кроп и аннотации, горячие клавиши с учётом фокуса, проекты и восстановление, экспорт, тетрадные профили и ручной переворот, Markdown и отдельные параметры страниц. В 1.0.34 появились чекбоксы задач, выравнивание таблиц, зачёркивание, корректная табуляция, постраничные сноски, предупреждения об ошибках формул и центрирование блочных формул.
