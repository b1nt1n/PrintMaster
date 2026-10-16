# Build from source / Сборка из исходников

## Requirements

- Windows 10/11 x64. WPF and the rendering tests need Windows.
- .NET SDK 8.0.420 or a later 8.0 feature band. `global.json` allows newer installed 8.0 SDKs.
- Internet for the first NuGet restore and, if requested, downloading LibreOffice from The Document Foundation.
- PowerShell 7.2 or later for the scripts. The application runs without PowerShell after extraction.

From the repository root:

```powershell
dotnet restore PrintMaster.sln -r win-x64 -p:Platform=x64 --locked-mode
dotnet build PrintMaster.sln -c Release -p:Platform=x64 --no-restore
dotnet test tests/PrintMaster.Tests/PrintMaster.Tests.csproj -c Release -p:Platform=x64 --no-build --no-restore --filter 'Category!=HeadlessUI&Category!=Diagnostic'
```

Run the separate headless style check in a fresh process:

```powershell
$env:PRINTMASTER_HEADLESS_STYLE = '1'
dotnet test tests/PrintMaster.Tests/PrintMaster.Tests.csproj -c Release -p:Platform=x64 --no-build --no-restore --filter 'Category=HeadlessUI'
Remove-Item Env:PRINTMASTER_HEADLESS_STYLE
```

Some desktop acceptance tests are intentionally skipped until their required fixtures or explicit physical-printer authorization are provided. A skipped paper test is not evidence that a printer worked. Do not run WPF test processes concurrently with desktop interaction tests.

## Portable build

```powershell
./scripts/Publish.ps1 -OutputDirectory artifacts/PrintMaster
```

The script publishes self-contained win-x64, then includes a local LibreOffice distribution. If absent, `Setup-LibreOffice.ps1` downloads the pinned official MSI and checks its SHA-256 before administrative extraction. This step can take time and disk space. For development without bundled DOCX conversion:

```powershell
./scripts/Publish.ps1 -WithoutLibreOffice -OutputDirectory artifacts/PrintMaster
```

Copy the complete output directory when testing on another machine. PDFium is a native x64 dependency. The local WpfMath adaptation and its font notices live in `src/ThirdParty/WpfMath`.

## По-русски

Нужны Windows x64, .NET 8 SDK и PowerShell 7. Команды выше восстанавливают закреплённые NuGet-пакеты, собирают Release и запускают проверки. Отдельный HeadlessUI запускается с переменной окружения, которую затем нужно удалить. Физическая печать не должна выполняться без подготовки бумаги и разрешения пользователя.

`Publish.ps1` собирает автономную программу и добавляет локальный LibreOffice для DOCX. При первом запуске скрипт может скачать и распаковать официальную поставку. `-WithoutLibreOffice` подходит для разработки, но такой каталог не содержит готовую конвертацию DOCX. Сохраняй всю папку сборки, нативный PDFium и лицензии зависимостей.

## Maintainer staging / Подготовка публичной поставки

`Prepare-GitHubRelease.ps1` and `Verify-GitHubRelease.ps1` are optional maintainer tools. They do not create or upload a GitHub repository. Preparation needs a separately curated `publish-content/repository` and `publish-content/release-assets` next to the source tree, plus a complete built application folder. This staging input is not required for building the app and is not stored in the public source snapshot. Regular `Publish.ps1` works directly from a clean checkout. Copy the README, images, public docs and release notes into the staging input if preparing a new public package.

Prepare/Verify предназначены для локальной подготовки следующего релиза и требуют отдельно собранную папку `publish-content`. Для обычной сборки они не нужны. Скрипты не создают репозиторий, ничего не загружают и сохраняют уже существующие папки публикаций.

Prepare reads the version from the working project; Verify reads it from the prepared project. Both accept an explicit `-Version`. Run verification before initializing Git in the staged tree.

Prepare берёт версию из рабочего проекта, Verify из подготовленного. В обоих скриптах её можно задать через `-Version`. Проверяй подготовленную папку перед созданием в ней Git-репозитория.
