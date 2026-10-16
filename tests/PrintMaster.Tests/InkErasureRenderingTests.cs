using System.Collections.Immutable;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using PrintMaster.Models;
using PrintMaster.Services;
using Xunit;

namespace PrintMaster.Tests;

public sealed class InkErasureRenderingTests : IDisposable
{
    private static readonly SheetPaper Paper = new(new(60, 60), false, new(0, 0, 0, 0));
    private readonly PageRasterizer _rasterizer = new(new PdfiumRenderer());
    private readonly string _root = Path.Combine(Fixtures.Root, "pm012-ink-render-" + Guid.NewGuid().ToString("N"));
    private string WhiteImage()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "white.png");
        if (!File.Exists(path))
        {
            using var bitmap = new Bitmap(600, 600, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.White);
            bitmap.Save(path, ImageFormat.Png);
        }
        return path;
    }
    private PrintPageItem Photo(Annotation annotation)
    {
        var path = WhiteImage();
        var page = PrintPageItem.FromSource(new(Guid.NewGuid(), SourceKind.Image, path, path, 0, 1,
            new(600, 600), new(60, 60)), Paper);
        page.Edits.Add(annotation); return page;
    }
    private static int Pixels(int dpi) => (int)Math.Ceiling(60d / 25.4 * dpi);
    private static Color At(Bitmap bitmap, double xMm, double yMm)
        => bitmap.GetPixel((int)Math.Round(xMm / 60 * bitmap.Width), (int)Math.Round(yMm / 60 * bitmap.Height));
    private static void Near(Color expected, Color actual)
    {
        Assert.Equal(255, actual.A);
        Assert.InRange(actual.R, Math.Max(0, expected.R - 2), Math.Min(255, expected.R + 2));
        Assert.InRange(actual.G, Math.Max(0, expected.G - 2), Math.Min(255, expected.G + 2));
        Assert.InRange(actual.B, Math.Max(0, expected.B - 2), Math.Min(255, expected.B + 2));
    }
    private static Annotation Line(AnnotationKind kind = AnnotationKind.Ink, double? opacity = null)
        => Annotation.Create(kind, new(100, 250), new(500, 250), "#000000", 20,
            points: [new(100, 250), new(500, 250)]) with { Opacity = opacity };

    [Theory]
    [InlineData(300)]
    [InlineData(600)]
    public void RenderSheet_ErasedInkAtPrintDpi_PreservesBothSidesAndWhiteGap(int dpi)
    {
        var page = PrintPageItem.CreateBlank(Paper);
        var stroke = new InkObject(Guid.NewGuid(), new(new(10, 20, 40, 10)),
            [new(0, .5), new(1, .5)], new("#000000", 2));
        var erased = InkStrokeEraser.Erase(stroke, new(30, 25), new(30, 25), 3);
        Assert.True(erased.Changed); page.Document.AddObjects(erased.Fragments);

        using var bitmap = _rasterizer.RenderSheet(page.Document.Snapshot(), dpi, true, default);
        Assert.Equal(Pixels(dpi), bitmap.Width); Assert.Equal(Pixels(dpi), bitmap.Height);
        Assert.Equal(dpi, bitmap.HorizontalResolution, 2); Assert.Equal(dpi, bitmap.VerticalResolution, 2);
        Near(Color.Black, At(bitmap, 20, 25)); Near(Color.Black, At(bitmap, 40, 25));
        Near(Color.White, At(bitmap, 30, 25)); Near(Color.White, At(bitmap, 27.5, 25));
        Near(Color.White, At(bitmap, 32.5, 25)); Near(Color.White, At(bitmap, 30, 10));
    }

    [Theory]
    [InlineData(300)]
    [InlineData(600)]
    public void RenderProcessed_ErasedSheetInk_GapSurvivesActualMonochromePrintPipeline(int dpi)
    {
        var page = PrintPageItem.CreateBlank(Paper);
        var stroke = new InkObject(Guid.NewGuid(), new(new(10, 20, 40, 10)),
            [new(0, .5), new(1, .5)], new("#000000", 2, .4));
        page.Document.AddObjects(InkStrokeEraser.Erase(stroke, new(30, 25), new(30, 25), 3).Fragments);
        var pixels = Pixels(dpi);

        using var bitmap = _rasterizer.RenderProcessed(page.Snapshot(), pixels, pixels, new(0, 0, pixels, pixels),
            MonochromeMode.Text, 160, dpi, default, new(0, 0, 60, 60));
        Assert.Equal(dpi, bitmap.HorizontalResolution, 2);
        Near(Color.Black, At(bitmap, 20, 25)); Near(Color.Black, At(bitmap, 40, 25));
        Near(Color.White, At(bitmap, 30, 25));
    }

    [Theory]
    [InlineData(300, .4)]
    [InlineData(600, .4)]
    [InlineData(300, .3333333333333333)]
    [InlineData(600, .3333333333333333)]
    public void RenderSheet_ErasedMarker_PreservesCompositeAlphaOnBothSurvivors(int dpi, double opacity)
    {
        var stroke = new InkObject(Guid.NewGuid(), new(new(10, 20, 40, 10)),
            [new(0, .5), new(1, .5)], new("#000000", 2, opacity));
        var page = PrintPageItem.CreateBlank(Paper); page.Document.AddObject(stroke);
        using var before = _rasterizer.RenderSheet(page.Document.Snapshot(), dpi, false, default);
        var leftBefore = At(before, 20, 25); var rightBefore = At(before, 40, 25);
        var fragments = InkStrokeEraser.Erase(stroke, new(30, 25), new(30, 25), 3).Fragments;
        page.Document.SetErasedDrawingObjects(fragments.Cast<SheetObject>().ToImmutableArray());

        using var after = _rasterizer.RenderSheet(page.Document.Snapshot(), dpi, false, default);
        var gray = 255 - (int)Math.Round(255 * opacity);
        Near(Color.FromArgb(gray, gray, gray), leftBefore);
        Near(leftBefore, At(after, 20, 25)); Near(rightBefore, At(after, 40, 25));
        Near(Color.White, At(after, 30, 25));
        Assert.All(fragments, fragment => Assert.Equal(opacity, fragment.Style.Opacity));
    }

    [Theory]
    [InlineData(300)]
    [InlineData(600)]
    public void RenderViewport_ErasedSourceAnnotation_RendersAtRequestedPixelResolution(int dpi)
    {
        var page = Photo(Line());
        var photo = page.Document.PrimaryContent!;
        var fragments = InkStrokeEraser.Erase(page.Edits.Annotations.Single(), new(300, 250), new(300, 250), 30).Fragments;
        page.Document.SetContentEdits(photo.Id, photo.Edits with { Annotations = fragments }, "Стереть часть рисунка");
        Assert.False(page.IsComposed);
        var pixels = Pixels(dpi);

        using var bitmap = _rasterizer.RenderViewport(page.Snapshot(), pixels, pixels, new(0, 0, pixels, pixels), true, default);
        Assert.Equal(pixels, bitmap.Width); Assert.Equal(pixels, bitmap.Height);
        Near(Color.Black, At(bitmap, 20, 25)); Near(Color.Black, At(bitmap, 40, 25));
        Near(Color.White, At(bitmap, 30, 25)); Near(Color.White, At(bitmap, 27.5, 25));
        Near(Color.White, At(bitmap, 32.5, 25));
    }

    [Theory]
    [InlineData(300, false)]
    [InlineData(600, false)]
    [InlineData(300, true)]
    [InlineData(600, true)]
    public void RenderSheet_ErasedNestedAnnotation_PreservesPhotoAndLegacyOrExplicitMarkerAlpha(int dpi, bool explicitOpacity)
    {
        var page = Photo(Line(AnnotationKind.Marker, explicitOpacity ? .4 : null));
        var original = page.Document.PrimaryContent!;
        page.Document.SetPlacement(original.Id, new(new(0, 0, 60, 60)));
        var content = page.Document.PrimaryContent!;
        var sourceHash = SHA256.HashData(File.ReadAllBytes(page.WorkingPath));
        var erased = InkStrokeEraser.Erase(content, new(30, 25), new(30, 25), 3);
        page.Document.ReplaceObject(Assert.Single(erased.Fragments), "Стереть часть рисунка");
        Assert.True(page.IsComposed);

        using var bitmap = _rasterizer.RenderSheet(page.Document.Snapshot(), dpi, true, default);
        var alpha = explicitOpacity ? 102 : 85;
        Near(Color.FromArgb(255 - alpha, 255 - alpha, 255 - alpha), At(bitmap, 20, 25));
        Near(Color.FromArgb(255 - alpha, 255 - alpha, 255 - alpha), At(bitmap, 40, 25));
        Near(Color.White, At(bitmap, 30, 25)); Near(Color.White, At(bitmap, 30, 10));
        Assert.Equal(sourceHash, SHA256.HashData(File.ReadAllBytes(page.WorkingPath)));
        Assert.Equal(content.AssetId, page.Document.PrimaryContent!.AssetId);
        Assert.Equal(content.Edits.Crop, page.Document.PrimaryContent.Edits.Crop);
    }

    [Theory]
    [InlineData(300, AnnotationKind.Ink)]
    [InlineData(600, AnnotationKind.Ink)]
    [InlineData(300, AnnotationKind.Marker)]
    [InlineData(600, AnnotationKind.Marker)]
    public void RenderViewport_SinglePointStroke_IsFilledCircleCentredOnPointRatherThanMetadataStart(int dpi, AnnotationKind kind)
    {
        var annotation = Annotation.Create(kind, new(100, 100), new(500, 500), "#000000", 20,
            points: [new(300, 300)]) with { Opacity = 1 };
        var page = Photo(annotation); var pixels = Pixels(dpi);
        using var bitmap = _rasterizer.RenderViewport(page.Snapshot(), pixels, pixels, new(0, 0, pixels, pixels), false, default);

        Near(Color.Black, At(bitmap, 30, 30)); Near(Color.Black, At(bitmap, 30.7, 30));
        Near(Color.Black, At(bitmap, 30, 30.7));
        Near(Color.White, At(bitmap, 30.8, 30.8));
        Near(Color.White, At(bitmap, 31.3, 30)); Near(Color.White, At(bitmap, 10, 10));
    }

    public void Dispose()
    {
        var path = Path.Combine(_root, "white.png");
        if (File.Exists(path)) File.Delete(path);
        if (Directory.Exists(_root)) Directory.Delete(_root);
    }
}
