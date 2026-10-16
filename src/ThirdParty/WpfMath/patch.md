# Minimal standalone font-provider patch

Upstream baseline: `v2.1.0`, commit `d6f576345998777dcf7ad112da1add9bd7ad9674`.

The released `WpfTeXEnvironment.Create` constructs an internal `DefaultTexFont` using an internal provider whose font URIs use `pack://application`. After a WPF Application has shut down, a cold font load can fail in `GlyphTypeface.Initialize` with `NotSupportedException`. The internal `WpfGlyphTypeface` marker and internal DefaultTexFont prevent an external application from replacing only that provider through the released public API.

Local additions:

1. `Rendering/StandaloneWpfMath.cs` (two public methods):
   - `WrapTypeface(GlyphTypeface)` returns the existing internal font marker, with no reflection.
   - `CreateEnvironment(IFontProvider,...)` constructs the existing math/text fonts using a supplied provider.
2. Standalone `WpfMath.csproj`: net8.0-windows/x64, AssemblyName WpfMath (retains XamlMath.Shared's friend access), XamlMath.Shared 2.1.0 NuGet, unchanged fonts embedded as WPF resources.

All upstream .cs/.xaml files and all .ttf bytes are unchanged. Parsing, TeX font metrics, formula layout, glyph rendering, and system text-font behavior remain upstream implementations. PrintMaster's external provider reads the bundled resource bytes through Assembly/ResourceReader, verifies SHA-256, writes unchanged font bytes to an owned content-addressed temporary directory, and creates file URI GlyphTypeface objects. There is no system font installation, application resource mutation, private-member access, or startup warmup dependency.

## Verification

`upstream-files.json` records 24 unchanged upstream files with Git blob SHA-1 and SHA-256; all five font byte sequences match the exact released commit.

The isolated stock reproduction creates a windowless WPF Application, runs its shutdown, then loads the first formula on a fresh STA. The unmodified loader fails at `GlyphTypeface.Initialize(packUri)` with the same `NotSupportedException` as the full test suite. The production child process does the same shutdown before its first font access and renders all five required formulas successfully, including Cyrillic `\text`. A second child compares each formula's vector ink width/height with the unmodified release path before shutdown: all match within 0.0001 DIP. No visible windows or physical print jobs are created.

Regression host: `tests/PrintMaster.FontLifecycle`; assertions: `tests/PrintMaster.Tests/MarkdownFontLifecycleTests.cs`. Evidence reports: `artifacts/verification/pm014/font-lifecycle-stock-red.json`, `font-lifecycle-production-cold-green.json`, `font-lifecycle-production-compare-green.json`, and `pm014-font-lifecycle-green.trx` (35 engine/lifecycle tests passed).
