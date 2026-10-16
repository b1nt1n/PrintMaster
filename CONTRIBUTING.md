# Contributing / Участие в разработке

Thank you for helping improve PrintMaster. Start with a small reproducible issue or a focused change. Read [BUILD.md](docs/BUILD.md) before building.

- Describe the problem and the expected result. Use a small generated document instead of a private file.
- Keep print geometry in millimetres and preserve editable source data.
- Use WPF/MVVM and the existing services. Keep high-resolution rendering out of the UI thread.
- Test affected behaviour and compatibility. Do not send physical printer jobs as part of an automated check.
- Keep third-party license and font notices. New dependencies need a clear reason and a license entry.
- Application text is Russian. Update English and Russian documentation when user-visible behaviour changes.

## По-русски

Начни с воспроизводимой ошибки или небольшой целевой правки. Подготовь пример без личных данных и опиши ожидаемый результат. Не меняй исходные материалы пользователя, не отправляй физическую печать из автотестов и сохраняй лицензии зависимостей. Для изменения формата проектов или пагинации нужны проверки совместимости. Инструкции сборки находятся в [BUILD.md](docs/BUILD.md).
