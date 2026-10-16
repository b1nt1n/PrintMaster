using System.Collections.Immutable;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using PrintMaster.Models;
using PrintMaster.Services;
using Xunit;

namespace PrintMaster.Tests;

public sealed class PhotoGridRenderingTests : IDisposable
{
    private readonly string _root = Path.Combine(Fixtures.Root, "pm013-grid-render-" + Guid.NewGuid().ToString("N"));
    private readonly PageRasterizer _rasterizer = new(new PdfiumRenderer());
    private static readonly Color[] Colors =
        [Color.FromArgb(240, 32, 48), Color.FromArgb(40, 96, 224), Color.FromArgb(32, 160, 80), Color.FromArgb(224, 144, 16)];

    private string Image()
    {
        Directory.CreateDirectory(_root); var path = Path.Combine(_root, "quadrants.png");
        using var bitmap = new Bitmap(600, 400, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
            for (var i = 0; i < Colors.Length; i++)
            {
                using var brush = new SolidBrush(Colors[i]);
                graphics.FillRectangle(brush, i % 2 * 300, i / 2 * 200, 300, 200);
            }
        bitmap.Save(path, ImageFormat.Png); return path;
    }
    private static Color At(Bitmap bitmap, SizeD paper, PointD point)
        => bitmap.GetPixel((int)Math.Round(point.X / paper.Width * bitmap.Width),
            (int)Math.Round(point.Y / paper.Height * bitmap.Height));
    private static void Near(Color expected, Color actual)
    {
        Assert.Equal(255, actual.A);
        Assert.InRange(actual.R, Math.Max(0, expected.R - 2), Math.Min(255, expected.R + 2));
        Assert.InRange(actual.G, Math.Max(0, expected.G - 2), Math.Min(255, expected.G + 2));
        Assert.InRange(actual.B, Math.Max(0, expected.B - 2), Math.Min(255, expected.B + 2));
    }

    [Theory]
    [InlineData(300, ScaleMode.Fit, 0, false)]
    [InlineData(300, ScaleMode.Fit, 1, true)]
    [InlineData(300, ScaleMode.Fit, 2, false)]
    [InlineData(300, ScaleMode.Fit, 3, true)]
    [InlineData(300, ScaleMode.Fill, 0, false)]
    [InlineData(300, ScaleMode.Fill, 1, true)]
    [InlineData(300, ScaleMode.Fill, 2, false)]
    [InlineData(300, ScaleMode.Fill, 3, true)]
    [InlineData(600, ScaleMode.Fit, 0, false)]
    [InlineData(600, ScaleMode.Fit, 1, true)]
    [InlineData(600, ScaleMode.Fit, 2, false)]
    [InlineData(600, ScaleMode.Fit, 3, true)]
    [InlineData(600, ScaleMode.Fill, 0, false)]
    [InlineData(600, ScaleMode.Fill, 1, true)]
    [InlineData(600, ScaleMode.Fill, 2, false)]
    [InlineData(600, ScaleMode.Fill, 3, true)]
    public void Render_PhotoGridAtPrintDpi_KeepsMappedColoursAnnotationsWhitespaceAndMonochrome(int dpi,
        ScaleMode scaling, int turns, bool landscape)
    {
        var path = Image(); var hash = SHA256.HashData(File.ReadAllBytes(path));
        var paper = new SheetPaper(new(60, 80), landscape, new(3, 3, 3, 3));
        var size = paper.OrientedSizeMm;
        var asset = new SourceAsset(Guid.NewGuid(), SourceKind.Image, path, path, 0, 1,
            new(600, 400), new(60, 40));
        var annotation = Annotation.Create(AnnotationKind.Rectangle, new(290, 190), new(310, 210),
            "#000000", 3) with { FillColor = "#000000" };
        var original = new ContentObject(Guid.NewGuid(), new(new(5, 5, 40, 30), 47), asset.Id,
            new(new(60, 40, 480, 320), turns, [annotation]));
        var sheet = new SheetSnapshot(Guid.NewGuid(), paper, [asset], [original], null);
        var plan = PhotoGridLayout.Create(new(sheet, [original.Id], paper.SafeAreaMm),
            new(2, 2, 3, 2, scaling, true));
        var arranged = sheet with { Objects = plan.Photos.Cast<SheetObject>().ToImmutableArray() };

        using var bitmap = _rasterizer.RenderSheet(arranged, dpi, true, default);
        Assert.Equal((int)Math.Ceiling(size.Width / 25.4 * dpi), bitmap.Width);
        Assert.Equal((int)Math.Ceiling(size.Height / 25.4 * dpi), bitmap.Height);
        Assert.Equal(dpi, bitmap.HorizontalResolution, 2); Assert.Equal(dpi, bitmap.VerticalResolution, 2);
        var expectedPoints = new List<PointD>();
        for (var index = 0; index < plan.Photos.Length; index++)
        {
            var photo = plan.Photos[index]; var bounds = photo.Placement.BoundsMm; var cell = plan.Cells[index];
            var crop = photo.Edits.Crop; var transform = AnnotationTextLayout.Transform(photo, bounds);
            for (var quadrant = 0; quadrant < Colors.Length; quadrant++)
            {
                var source = new PointD(crop.X + crop.Width * (quadrant % 2 == 0 ? .25 : .75),
                    crop.Y + crop.Height * (quadrant / 2 == 0 ? .25 : .75));
                var world = transform.Transform(source); expectedPoints.Add(world);
                Near(Colors[quadrant], At(bitmap, size, world));
            }
            var annotationCenter = transform.Transform(new(300, 200));
            Near(Color.Black, At(bitmap, size, annotationCenter)); expectedPoints.Add(annotationCenter);
            if (scaling == ScaleMode.Fill)
                Assert.Equal(cell, bounds);
            else
            {
                var blank = bounds.X > cell.X + .01
                    ? new PointD((cell.X + bounds.X) / 2, cell.Y + cell.Height / 2)
                    : new PointD(cell.X + cell.Width / 2, (cell.Y + bounds.Y) / 2);
                Near(Color.White, At(bitmap, size, blank));
            }
            Assert.Equal(0, photo.Placement.RotationDegrees);
            Assert.Equal(original.Edits.Annotations, photo.Edits.Annotations);
        }
        var verticalGap = new PointD((plan.Cells[0].Right + plan.Cells[1].X) / 2,
            plan.Cells[0].Y + plan.Cells[0].Height / 2);
        var horizontalGap = new PointD(plan.Cells[0].X + plan.Cells[0].Width / 2,
            (plan.Cells[0].Bottom + plan.Cells[2].Y) / 2);
        Near(Color.White, At(bitmap, size, verticalGap)); Near(Color.White, At(bitmap, size, horizontalGap));
        Near(Color.White, At(bitmap, size, new(1, 1)));
        Near(Color.White, At(bitmap, size, new(size.Width - 1, size.Height - 1)));

        // Exercise the same composed raster + threshold stage that PrintEngine uses.
        var page = new PrintPageItem(SheetDocument.Restore(arranged)).Snapshot();
        using var monochrome = _rasterizer.RenderProcessed(page, bitmap.Width, bitmap.Height,
            new(0, 0, bitmap.Width, bitmap.Height), MonochromeMode.Text, 180, dpi, default,
            new(0, 0, size.Width, size.Height));
        Assert.Equal(dpi, monochrome.HorizontalResolution, 2);
        Assert.All(expectedPoints, point => Near(Color.Black, At(monochrome, size, point)));
        Near(Color.White, At(monochrome, size, verticalGap)); Near(Color.White, At(monochrome, size, horizontalGap));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Single(arranged.Assets); Assert.Equal(original.Edits.Crop, sheet.Objects.OfType<ContentObject>().Single().Edits.Crop);
    }

    public void Dispose()
    {
        var path = Path.Combine(_root, "quadrants.png");
        if (File.Exists(path)) File.Delete(path);
        if (Directory.Exists(_root)) Directory.Delete(_root);
    }
}
