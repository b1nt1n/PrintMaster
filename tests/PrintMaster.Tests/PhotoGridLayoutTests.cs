using System.Collections.Immutable;
using PrintMaster.Models;
using PrintMaster.Services;
using Xunit;

namespace PrintMaster.Tests;

public sealed class PhotoGridLayoutTests
{
    private static readonly SheetPaper Paper = new(new(100, 100), false, new(0, 0, 0, 0));
    private static SourceAsset Asset(SizeD? size = null) => new(Guid.NewGuid(), SourceKind.Image,
        "photo.png", "photo.png", 0, 1, size ?? new(1000, 800), new(100, 80));
    private static ContentObject Photo(SourceAsset asset, RectD? crop = null, int turns = 0)
        => new(Guid.NewGuid(), new(new(10, 20, 60, 40), 23), asset.Id,
            new(crop ?? new(100, 50, 600, 400), turns, []));
    private static SheetSnapshot Sheet(ImmutableArray<SourceAsset> assets, ImmutableArray<SheetObject> objects,
        SheetPaper? paper = null) => new(Guid.NewGuid(), paper ?? Paper, assets, objects, null);
    private static PhotoGridRequest Request(SourceAsset asset, ContentObject photo, RectD? safe = null)
        => new(Sheet([asset], [photo]), [photo.Id], safe ?? new(0, 0, 100, 100));
    private static void Rect(RectD expected, RectD actual)
    {
        Assert.Equal(expected.X, actual.X, 8); Assert.Equal(expected.Y, actual.Y, 8);
        Assert.Equal(expected.Width, actual.Width, 8); Assert.Equal(expected.Height, actual.Height, 8);
    }
    private static void Contains(RectD outer, RectD inner)
    {
        Assert.True(inner.X >= outer.X - 1e-8); Assert.True(inner.Y >= outer.Y - 1e-8);
        Assert.True(inner.Right <= outer.Right + 1e-8); Assert.True(inner.Bottom <= outer.Bottom + 1e-8);
    }

    [Fact]
    public void Create_InsetAndGap_ProducesRowMajorNonOverlappingCellsInsideSafeArea()
    {
        var asset = Asset(); var photos = Enumerable.Range(0, 3).Select(_ => Photo(asset)).ToArray();
        var request = new PhotoGridRequest(Sheet([asset], photos.Cast<SheetObject>().ToImmutableArray()),
            photos.Reverse().Select(p => p.Id).ToImmutableArray(), new(5, 7, 90, 80));
        var options = new PhotoGridOptions(2, 3, 4, 6);

        var plan = PhotoGridLayout.Create(request, options);

        Assert.Equal(6, plan.Cells.Length); Assert.Equal(3, plan.Photos.Length); Assert.False(plan.Repeat);
        var width = 70d / 3;
        Rect(new(11, 13, width, 32), plan.Cells[0]);
        Rect(new(11 + width + 4, 13, width, 32), plan.Cells[1]);
        Rect(new(11 + 2 * (width + 4), 49, width, 32), plan.Cells[5]);
        Assert.Equal(89, plan.Cells[^1].Right, 8); Assert.Equal(81, plan.Cells[^1].Bottom, 8);
        for (var i = 0; i < plan.Cells.Length; i++)
        {
            Contains(request.SafeAreaMm, plan.Cells[i]);
            for (var j = i + 1; j < plan.Cells.Length; j++) Assert.True(plan.Cells[i].Intersect(plan.Cells[j]).IsEmpty);
        }
        for (var i = 0; i < plan.Photos.Length; i++) Contains(plan.Cells[i], plan.Photos[i].Placement.BoundsMm);
        Assert.Equal(photos.Select(p => p.Id).ToArray(), plan.OriginalPhotoIds.ToArray());
        Assert.Equal(photos.Select(p => p.Id).ToArray(), plan.Photos.Select(p => p.Id).ToArray());
    }

    [Theory]
    [InlineData(0, 17.5, 45)]
    [InlineData(1, 30, 20)]
    [InlineData(2, 17.5, 45)]
    [InlineData(3, 30, 20)]
    public void Create_FitUsesCurrentCropAndQuarterTurn_CentresWithoutStretching(int turns,
        double x, double width)
    {
        var asset = Asset(); var photo = Photo(asset, turns: turns);
        var request = Request(asset, photo, new(10, 15, 60, 30));
        var plan = PhotoGridLayout.Create(request, new(1, 1, Scaling: ScaleMode.Fit));
        var updated = Assert.Single(plan.Photos);

        Rect(new(x, 15, width, 30), updated.Placement.BoundsMm);
        Assert.Equal(0, updated.Placement.RotationDegrees); Assert.Equal(PlacementMode.Fixed, updated.Placement.Mode);
        Assert.Same(photo.Edits, updated.Edits); Assert.Equal(photo.Id, updated.Id); Assert.Equal(photo.AssetId, updated.AssetId);
        Assert.Equal(turns, updated.Edits.QuarterTurns);
        var oriented = new SizeD(photo.Edits.Crop.Width, photo.Edits.Crop.Height).Rotated(turns);
        Assert.Equal(oriented.Width / oriented.Height, updated.Placement.BoundsMm.Width / updated.Placement.BoundsMm.Height, 8);
        Assert.Equal(23, photo.Placement.RotationDegrees);
    }

    [Theory]
    [InlineData(0, 60, 30, 100, 100, 600, 300)]
    [InlineData(1, 60, 30, 300, 50, 200, 400)]
    [InlineData(2, 60, 30, 100, 100, 600, 300)]
    [InlineData(3, 60, 30, 300, 50, 200, 400)]
    [InlineData(0, 20, 60, 333.3333333333333, 50, 133.3333333333333, 400)]
    [InlineData(1, 20, 60, 100, 150, 600, 200)]
    [InlineData(2, 20, 60, 333.3333333333333, 50, 133.3333333333333, 400)]
    [InlineData(3, 20, 60, 100, 150, 600, 200)]
    public void Create_FillAllQuarterTurns_CropsCentrallyWithinCurrentCrop(int turns, double width,
        double height, double x, double y, double cropWidth, double cropHeight)
    {
        var asset = Asset(); var photo = Photo(asset, turns: turns);
        var safe = new RectD(10, 15, width, height);
        var plan = PhotoGridLayout.Create(Request(asset, photo, safe), new(1, 1, Scaling: ScaleMode.Fill));
        var updated = Assert.Single(plan.Photos);

        Rect(safe, updated.Placement.BoundsMm);
        Rect(new(x, y, cropWidth, cropHeight), updated.Edits.Crop);
        Contains(photo.Edits.Crop, updated.Edits.Crop);
        Assert.Equal(400, updated.Edits.Crop.X + updated.Edits.Crop.Width / 2, 8);
        Assert.Equal(250, updated.Edits.Crop.Y + updated.Edits.Crop.Height / 2, 8);
        var oriented = CropOrientation.ToOriented(updated.Edits.Crop, asset.LogicalSize, turns);
        Assert.Equal(width / height, oriented.Width / oriented.Height, 8);
        Assert.Equal(turns, updated.Edits.QuarterTurns); Assert.Equal(photo.Id, updated.Id);
    }

    [Theory]
    [InlineData(ScaleMode.Fit)]
    [InlineData(ScaleMode.Fill)]
    public void Create_PreservesAnnotationsResizePreferenceSourceAndInputSnapshot(ScaleMode scaling)
    {
        var asset = Asset(); var marker = Annotation.Create(AnnotationKind.Marker, new(200, 100), new(400, 200),
            "#123456", 7, points: [new(200, 100), new(400, 200)]) with { Opacity = .27 };
        var photo = Photo(asset) with { KeepAspectRatio = false, Edits = new(new(100, 50, 600, 400), 0, [marker]) };
        var unrelated = new TextObject(Guid.NewGuid(), new(new(80, 80, 10, 10)), "Не трогать", new());
        var sheet = Sheet([asset], [photo, unrelated]);
        var request = new PhotoGridRequest(sheet, [photo.Id], new(10, 15, 60, 30));

        var plan = PhotoGridLayout.Create(request, new(1, 1, Scaling: scaling));
        var updated = Assert.Single(plan.Photos);

        Assert.False(updated.KeepAspectRatio); Assert.Equal(photo.Visible, updated.Visible); Assert.Equal(photo.Locked, updated.Locked);
        Assert.Equal(photo.AssetId, updated.AssetId); Assert.Equal(photo.Edits.Annotations, updated.Edits.Annotations);
        Assert.Same(marker.Points, updated.Edits.Annotations[0].Points); Assert.Equal(.27, updated.Edits.Annotations[0].Opacity);
        Assert.Same(photo, sheet.Objects[0]); Assert.Same(unrelated, sheet.Objects[1]); Assert.Same(asset, sheet.Assets[0]);
        Rect(new(100, 50, 600, 400), photo.Edits.Crop); Assert.Equal(23, photo.Placement.RotationDegrees);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Create_ExactFillAndRepeatedFill_DoNotAccumulateCropChanges(int turns)
    {
        var asset = Asset(); var photo = Photo(asset, new(100, 50, 600, 300), turns);
        var safe = turns % 2 == 0 ? new RectD(0, 0, 60, 30) : new RectD(0, 0, 30, 60);
        var first = PhotoGridLayout.Create(Request(asset, photo, safe), new(1, 1, Scaling: ScaleMode.Fill));
        Assert.Same(photo.Edits, first.Photos[0].Edits);
        var second = PhotoGridLayout.Create(Request(asset, first.Photos[0], safe), new(1, 1, Scaling: ScaleMode.Fill));
        Assert.Equal(first.Photos[0], second.Photos[0]); Assert.Same(first.Photos[0].Edits, second.Photos[0].Edits);
    }

    [Theory]
    [InlineData(ScaleMode.Fit)]
    [InlineData(ScaleMode.Fill)]
    public void Create_Repeat_ReusesSingleAssetAndPreservesFirstIdWithUniqueCloneIds(ScaleMode scaling)
    {
        var asset = Asset(); var source = Photo(asset); var unselected = Photo(asset);
        var sheet = Sheet([asset], [source, unselected]);
        var plan = PhotoGridLayout.Create(new(sheet, [source.Id], new(0, 0, 100, 100)),
            new(2, 3, 2, 1, scaling, true));

        Assert.True(plan.Repeat); Assert.Equal(6, plan.Photos.Length); Assert.Equal(6, plan.Cells.Length);
        Assert.Equal(source.Id, plan.Photos[0].Id); Assert.Equal(6, plan.Photos.Select(p => p.Id).Distinct().Count());
        Assert.DoesNotContain(unselected.Id, plan.Photos.Select(p => p.Id));
        Assert.All(plan.Photos, p => { Assert.Equal(asset.Id, p.AssetId); Assert.Equal(source.Edits.Annotations, p.Edits.Annotations); });
        Assert.Equal(new[] { source.Id }, plan.OriginalPhotoIds.ToArray());
        Assert.Single(sheet.Assets); Assert.Equal(2, sheet.Objects.Length); Assert.Same(unselected, sheet.Objects[1]);
    }

    [Fact]
    public void Create_SelectionOrder_FollowsSheetStackInsteadOfSelectionArrayOrAssetOrder()
    {
        var firstAsset = Asset(); var secondAsset = Asset(); var first = Photo(firstAsset); var second = Photo(secondAsset);
        var text = new TextObject(Guid.NewGuid(), new(new(10, 10, 10, 10)), "Текст", new());
        var unselected = Photo(firstAsset);
        var request = new PhotoGridRequest(Sheet([secondAsset, firstAsset], [second, text, unselected, first]),
            [first.Id, second.Id], new(0, 0, 100, 100));

        var plan = PhotoGridLayout.Create(request, new(1, 2));

        Assert.Equal(new[] { second.Id, first.Id }, plan.OriginalPhotoIds.ToArray());
        Assert.Equal(new[] { second.Id, first.Id }, plan.Photos.Select(p => p.Id).ToArray());
        Assert.Same(text, request.Sheet.Objects[1]); Assert.Same(unselected, request.Sheet.Objects[2]);
    }

    [Fact]
    public void Create_MorePhotosThanCells_RejectsRatherThanSilentlyDroppingPhoto()
    {
        var asset = Asset(); var photos = Enumerable.Range(0, 3).Select(_ => Photo(asset)).ToArray();
        var request = new PhotoGridRequest(Sheet([asset], photos.Cast<SheetObject>().ToImmutableArray()),
            photos.Select(p => p.Id).ToImmutableArray(), new(0, 0, 100, 100));

        var error = Assert.Throws<ArgumentException>(() => PhotoGridLayout.Create(request, new(1, 2)));
        Assert.Contains("3 фотографий", error.Message); Assert.Contains("не будут удалены", error.Message);
        Assert.Equal(photos.Cast<SheetObject>().ToArray(), request.Sheet.Objects.ToArray());
    }

    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    public void Create_HundredCellsAndOneMillimetreBoundary_AcceptsMaximumGrid(double safeSize)
    {
        var asset = Asset(); var photo = Photo(asset);
        var plan = PhotoGridLayout.Create(Request(asset, photo, new(0, 0, safeSize, safeSize)), new(10, 10, 0, 0, Repeat: true));
        Assert.Equal(100, plan.Cells.Length); Assert.Equal(100, plan.Photos.Length);
        Assert.Equal(safeSize / 10, plan.Cells[0].Width); Assert.Equal(safeSize / 10, plan.Cells[0].Height);
        Assert.Equal(safeSize, plan.Cells[^1].Right, 8); Assert.Equal(safeSize, plan.Cells[^1].Bottom, 8);
        Assert.Equal(100, plan.Photos.Select(p => p.Id).Distinct().Count());
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(1, 0)] [InlineData(-1, 2)] [InlineData(2, -1)]
    [InlineData(11, 1)] [InlineData(1, 11)] [InlineData(int.MaxValue, int.MaxValue)]
    public void Create_InvalidGridDimensions_RejectsBeforeOverflow(int rows, int columns)
    {
        var asset = Asset(); var photo = Photo(asset);
        Assert.Throws<ArgumentException>(() => PhotoGridLayout.Create(Request(asset, photo), new(rows, columns)));
    }

    [Theory]
    [InlineData(-1, 0)] [InlineData(0, -1)] [InlineData(double.NaN, 0)] [InlineData(0, double.NaN)]
    [InlineData(double.PositiveInfinity, 0)] [InlineData(0, double.PositiveInfinity)]
    [InlineData(100, 0)] [InlineData(0, 50)] [InlineData(0, 49.6)]
    public void Create_InvalidSpacingOrTooSmallCells_RejectsWithExplanation(double gap, double inset)
    {
        var asset = Asset(); var photo = Photo(asset);
        Assert.Throws<ArgumentException>(() => PhotoGridLayout.Create(Request(asset, photo), new(2, 2, gap, inset)));
    }

    [Theory]
    [InlineData(ScaleMode.Stretch)]
    [InlineData((ScaleMode)99)]
    public void Create_StretchOrUnknownMode_RejectsDistortedPhotoLayout(ScaleMode scaling)
    {
        var asset = Asset(); var photo = Photo(asset);
        var error = Assert.Throws<ArgumentException>(() => PhotoGridLayout.Create(Request(asset, photo), new(Scaling: scaling)));
        Assert.Contains("без растягивания", error.Message);
    }

    [Theory]
    [InlineData(-1, 0, 10, 10)] [InlineData(0, -1, 10, 10)]
    [InlineData(90, 0, 11, 10)] [InlineData(0, 90, 10, 11)]
    [InlineData(0, 0, 0, 10)] [InlineData(0, 0, 10, 0)]
    [InlineData(double.NaN, 0, 10, 10)] [InlineData(0, 0, double.PositiveInfinity, 10)]
    public void Create_InvalidPhysicalSafeArea_Rejects(double x, double y, double width, double height)
    {
        var asset = Asset(); var photo = Photo(asset);
        Assert.Throws<ArgumentException>(() => PhotoGridLayout.Create(Request(asset, photo, new(x, y, width, height)), new()));
    }

    [Fact]
    public void Create_LandscapePaper_UsesOrientedPhysicalLimits()
    {
        var asset = Asset(); var photo = Photo(asset);
        var paper = new SheetPaper(new(100, 60), true, new(0, 0, 0, 0));
        var request = new PhotoGridRequest(Sheet([asset], [photo], paper), [photo.Id], new(0, 0, 60, 100));
        var plan = PhotoGridLayout.Create(request, new(1, 1));
        Rect(new(0, 0, 60, 100), Assert.Single(plan.Cells));
        Assert.Throws<ArgumentException>(() => PhotoGridLayout.Create(request with { SafeAreaMm = new(0, 0, 100, 60) }, new()));
    }

    [Theory]
    [InlineData("shape")] [InlineData("hidden")] [InlineData("locked")]
    [InlineData("missing")] [InlineData("pdf")] [InlineData("docx")]
    [InlineData("emptySource")] [InlineData("cropZero")] [InlineData("cropOutside")]
    [InlineData("cropNaN")] [InlineData("turns")] [InlineData("logicalSize")]
    [InlineData("placement")] [InlineData("annotation")] [InlineData("defaultAnnotations")]
    [InlineData("emptyID")] [InlineData("assetID")] [InlineData("invalidAssetPages")]
    public void Create_UnsupportedOrDamagedSelectedPhoto_Rejects(string kind)
    {
        var asset = Asset(); var photo = Photo(asset); SheetObject selected = photo;
        switch (kind)
        {
            case "shape": selected = new ShapeObject(photo.Id, photo.Placement, SheetShapeKind.Rectangle, new()); break;
            case "hidden": selected = photo with { Visible = false }; break;
            case "locked": selected = photo with { Locked = true }; break;
            case "missing": selected = photo with { AssetId = Guid.NewGuid() }; break;
            case "pdf": asset = asset with { Kind = SourceKind.Pdf }; break;
            case "docx": asset = asset with { Kind = SourceKind.Docx }; break;
            case "emptySource": asset = asset with { WorkingPath = null }; break;
            case "cropZero": selected = photo with { Edits = photo.Edits with { Crop = new(0, 0, 0, 100) } }; break;
            case "cropOutside": selected = photo with { Edits = photo.Edits with { Crop = new(900, 0, 200, 100) } }; break;
            case "cropNaN": selected = photo with { Edits = photo.Edits with { Crop = new(double.NaN, 0, 100, 100) } }; break;
            case "turns": selected = photo with { Edits = photo.Edits with { QuarterTurns = 4 } }; break;
            case "logicalSize": asset = asset with { LogicalSize = new(double.PositiveInfinity, 800) }; break;
            case "placement": selected = photo with { Placement = photo.Placement with { RotationDegrees = double.NaN } }; break;
            case "annotation": selected = photo with { Edits = photo.Edits with { Annotations = [Annotation.Create(AnnotationKind.Ink, new(0, 0), new(1, 1), "#000000", -1)] } }; break;
            case "defaultAnnotations": selected = photo with { Edits = photo.Edits with { Annotations = default } }; break;
            case "emptyID": selected = photo with { Id = Guid.Empty }; break;
            case "assetID": asset = asset with { Id = Guid.Empty }; selected = photo with { AssetId = Guid.Empty }; break;
            case "invalidAssetPages": asset = asset with { PageIndex = 1, PageCount = 1 }; break;
        }
        var request = new PhotoGridRequest(Sheet([asset], [selected]), [selected.Id], new(0, 0, 100, 100));
        Assert.Throws<ArgumentException>(() => PhotoGridLayout.Create(request, new()));
    }

    [Fact]
    public void Create_InvalidSelectionOrDuplicateSheetIds_RejectsAmbiguity()
    {
        var asset = Asset(); var photo = Photo(asset); var request = Request(asset, photo);
        Assert.Throws<ArgumentException>(() => PhotoGridLayout.Create(request with { PhotoIds = [] }, new()));
        Assert.Throws<ArgumentException>(() => PhotoGridLayout.Create(request with { PhotoIds = default }, new()));
        Assert.Throws<ArgumentException>(() => PhotoGridLayout.Create(request with { PhotoIds = [photo.Id, photo.Id] }, new()));
        Assert.Throws<ArgumentException>(() => PhotoGridLayout.Create(request with { PhotoIds = [Guid.NewGuid()] }, new()));
        Assert.Throws<ArgumentException>(() => PhotoGridLayout.Create(request with { Sheet = request.Sheet with { Objects = [photo, photo] } }, new()));
        Assert.Throws<ArgumentException>(() => PhotoGridLayout.Create(request with { Sheet = request.Sheet with { Assets = [asset, asset] } }, new()));
    }

    [Fact]
    public void Create_RepeatWithMultiplePhotos_RejectsChoosingSourceImplicitly()
    {
        var asset = Asset(); var first = Photo(asset); var second = Photo(asset);
        var request = new PhotoGridRequest(Sheet([asset], [first, second]), [first.Id, second.Id], new(0, 0, 100, 100));
        var error = Assert.Throws<ArgumentException>(() => PhotoGridLayout.Create(request, new(Repeat: true)));
        Assert.Contains("ровно одну", error.Message);
    }

    [Fact]
    public void Create_GeometryDoesNotOpenOriginalOrWorkingFile()
    {
        // The fake path does not need to exist: import/VM validates file availability, this pure service plans geometry.
        var asset = Asset() with { WorkingPath = "nonexistent-grid-fixture.png" }; var photo = Photo(asset);
        var plan = PhotoGridLayout.Create(Request(asset, photo), new(1, 1));
        Assert.Equal(asset.Id, Assert.Single(plan.Photos).AssetId);
    }
}
