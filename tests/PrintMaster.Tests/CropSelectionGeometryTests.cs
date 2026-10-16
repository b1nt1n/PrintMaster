using PrintMaster.Models;
using PrintMaster.Services;
using Xunit;

namespace PrintMaster.Tests;

public sealed class CropSelectionGeometryTests
{
    private static readonly RectD Bounds = new(0, 0, 100, 100);

    [Theory]
    [InlineData(CropHandle.TopLeft, 30, 35, 30, 25)]
    [InlineData(CropHandle.Top, 20, 35, 40, 25)]
    [InlineData(CropHandle.TopRight, 20, 35, 50, 25)]
    [InlineData(CropHandle.Right, 20, 30, 50, 30)]
    [InlineData(CropHandle.BottomRight, 20, 30, 50, 35)]
    [InlineData(CropHandle.Bottom, 20, 30, 40, 35)]
    [InlineData(CropHandle.BottomLeft, 30, 30, 30, 35)]
    [InlineData(CropHandle.Left, 30, 30, 30, 30)]
    public void Resize_FreeHandles_OnlyMovesRequestedEdges(CropHandle handle,
        double x, double y, double width, double height)
    {
        var result = CropSelectionGeometry.Resize(new(20, 30, 40, 30), handle, 10, 5, Bounds);
        AssertRect(new(x, y, width, height), result);
    }

    [Theory]
    [InlineData(CropHandle.TopLeft, 10, 5, 30, 35, 30, 15)]
    [InlineData(CropHandle.Top, 10, 5, 25, 35, 30, 15)]
    [InlineData(CropHandle.TopRight, 10, -5, 20, 25, 50, 25)]
    [InlineData(CropHandle.Right, 10, 5, 20, 27.5, 50, 25)]
    [InlineData(CropHandle.BottomRight, 10, 5, 20, 30, 50, 25)]
    [InlineData(CropHandle.Bottom, 10, 5, 15, 30, 50, 25)]
    [InlineData(CropHandle.BottomLeft, -10, 5, 10, 30, 50, 25)]
    [InlineData(CropHandle.Left, 10, 5, 30, 32.5, 30, 15)]
    public void Resize_LockedHandles_KeepsOppositeAnchorAndRatio(CropHandle handle,
        double dx, double dy, double x, double y, double width, double height)
    {
        var result = CropSelectionGeometry.Resize(new(20, 30, 40, 20), handle, dx, dy, Bounds, 2);
        AssertRect(new(x, y, width, height), result);
        Assert.Equal(2, result.Width / result.Height, 10);
    }

    [Fact]
    public void Resize_DiagonalLockedDrag_ProjectsBothPointerAxesContinuously()
    {
        var result = CropSelectionGeometry.Resize(new(20, 30, 40, 20),
            CropHandle.TopRight, 10, 5, Bounds, 2);
        AssertRect(new(20, 27, 46, 23), result);
        Assert.Equal(50, result.Bottom, 10);
    }

    [Theory]
    [InlineData(CropHandle.BottomRight, 100, 100, 20, 30, 80, 40)]
    [InlineData(CropHandle.TopLeft, -100, -100, 0, 20, 60, 30)]
    [InlineData(CropHandle.Right, 100, 100, 20, 20, 80, 40)]
    [InlineData(CropHandle.Top, 100, -100, 0, 10, 80, 40)]
    public void Resize_LockedDragPastBoundary_StopsWithoutDistorting(CropHandle handle,
        double dx, double dy, double x, double y, double width, double height)
    {
        var result = CropSelectionGeometry.Resize(new(20, 30, 40, 20), handle, dx, dy, Bounds, 2);
        AssertRect(new(x, y, width, height), result);
        AssertInside(result, Bounds);
    }

    [Fact]
    public void Resize_PointerCrossesOppositeCorner_StopsAtMinimumRatherThanFlipping()
    {
        var result = CropSelectionGeometry.Resize(new(20, 30, 40, 20),
            CropHandle.TopLeft, 100, 100, Bounds, 2, 5);
        AssertRect(new(50, 45, 10, 5), result);
    }

    [Fact]
    public void Resize_FreePointerCrossesOppositeEdges_StillRetainsPositiveSize()
    {
        var result = CropSelectionGeometry.Resize(new(20, 30, 40, 20),
            CropHandle.TopLeft, 100, 100, Bounds, minSize: 3);
        AssertRect(new(57, 47, 3, 3), result);
    }

    [Fact]
    public void Move_NonzeroBounds_PreservesSizeAndClampsBothAxes()
    {
        var bounds = new RectD(10, 20, 50, 40);
        var result = CropSelectionGeometry.Move(new(20, 30, 25, 20), 100, -100, bounds);
        AssertRect(new(35, 20, 25, 20), result);
    }

    [Fact]
    public void Move_OversizeRectangle_NormalizesToBounds()
    {
        var bounds = new RectD(10, 20, 50, 40);
        AssertRect(bounds, CropSelectionGeometry.Move(new(20, 30, 100, 80), 0, 0, bounds));
    }

    [Fact]
    public void Move_SubunitRectangle_PreservesItsSize()
    {
        var rect = new RectD(20, 30, .25, .5);
        var expected = new RectD(25, 35, .25, .5);
        AssertRect(expected, CropSelectionGeometry.Move(rect, 5, 5, Bounds));
        AssertRect(expected, CropSelectionGeometry.Resize(rect, CropHandle.Move, 5, 5, Bounds));
    }

    [Fact]
    public void FitRatio_LandscapeToSquare_ShrinksAroundCurrentCentre()
    {
        AssertRect(new(30, 30, 20, 20),
            CropSelectionGeometry.FitRatio(new(20, 30, 40, 20), 1, Bounds));
    }

    [Fact]
    public void FitRatio_NonzeroBounds_UsesExistingFrameCentre()
    {
        var bounds = new RectD(10, 20, 100, 80);
        AssertRect(new(32.5, 30, 15, 30),
            CropSelectionGeometry.FitRatio(new(20, 30, 40, 30), .5, bounds));
    }

    [Fact]
    public void ResizeTo_FreeNumericDimensions_RetainsTopLeftAndExactValues()
    {
        AssertRect(new(20, 30, 61.25, 38.75),
            CropSelectionGeometry.ResizeTo(new(20, 30, 40, 20), 61.25, 38.75, Bounds));
    }

    [Theory]
    [InlineData(true, 50, 99, 50, 25)]
    [InlineData(false, 99, 30, 60, 30)]
    public void ResizeTo_LockedNumericDimensions_HonoursAuthoritativeDimension(bool widthPriority,
        double requestedWidth, double requestedHeight, double width, double height)
    {
        AssertRect(new(20, 30, width, height),
            CropSelectionGeometry.ResizeTo(new(20, 30, 40, 20), requestedWidth, requestedHeight,
                Bounds, 2, widthPriority));
    }

    [Fact]
    public void ResizeTo_RequestPastImage_KeepsTopLeftAndRatio()
    {
        AssertRect(new(20, 30, 80, 40),
            CropSelectionGeometry.ResizeTo(new(20, 30, 40, 20), 200, 200, Bounds, 2));
    }

    [Fact]
    public void ResizeTo_NonzeroBounds_UsesActualRemainingSpace()
    {
        var bounds = new RectD(10, 20, 50, 40);
        AssertRect(new(20, 30, 40, 20),
            CropSelectionGeometry.ResizeTo(new(20, 30, 10, 10), 100, 100, bounds, 2));
    }

    [Fact]
    public void FitRatio_TinyBounds_RelaxesMinimumWhileKeepingRatioAndCentre()
    {
        var bounds = new RectD(5, 7, .2, .1);
        var result = CropSelectionGeometry.FitRatio(bounds, 1, bounds, 1);
        AssertRect(new(5.05, 7, .1, .1), result);
        AssertInside(result, bounds);
    }

    [Fact]
    public void Resize_TinyBounds_ClampsRatherThanReturningEmptyOrThrowing()
    {
        var bounds = new RectD(5, 7, .2, .1);
        var result = CropSelectionGeometry.Resize(bounds, CropHandle.BottomRight, -1, -1, bounds, 2, 1);
        AssertRect(bounds, result);
    }

    [Fact]
    public void Move_NonfiniteDeltas_AreIgnored()
    {
        var rect = new RectD(20, 30, 40, 20);
        AssertRect(rect, CropSelectionGeometry.Move(rect, double.NaN, double.PositiveInfinity, Bounds));
    }

    [Fact]
    public void ResizeTo_NonfiniteRequests_KeepCurrentDimensions()
    {
        var rect = new RectD(20, 30, 40, 20);
        AssertRect(rect, CropSelectionGeometry.ResizeTo(rect, double.NaN, double.NegativeInfinity, Bounds));
    }

    [Fact]
    public void Resize_NonfiniteRatio_UsesFreeProportions()
    {
        AssertRect(new(20, 30, 50, 25),
            CropSelectionGeometry.Resize(new(20, 30, 40, 20), CropHandle.BottomRight,
                10, 5, Bounds, double.NaN));
    }

    [Fact]
    public void Move_NonfiniteRectangle_NormalizesToFiniteBounds()
    {
        var bounds = new RectD(10, 20, 50, 40);
        AssertRect(bounds, CropSelectionGeometry.Move(
            new(double.NaN, double.PositiveInfinity, double.NaN, double.NegativeInfinity), 0, 0, bounds));
    }

    [Fact]
    public void FitRatio_UnrepresentableDimension_ReturnsEmptyInsteadOfChangingRatio()
    {
        var bounds = new RectD(0, 0, .1, .1);
        Assert.Equal(default, CropSelectionGeometry.FitRatio(bounds, double.Epsilon, bounds));
    }

    [Theory]
    [InlineData(0, 0, 0, 10)]
    [InlineData(0, 0, 10, -1)]
    [InlineData(double.NaN, 0, 10, 10)]
    [InlineData(0, 0, double.PositiveInfinity, 10)]
    public void AllOperations_InvalidBounds_ReturnEmpty(double x, double y, double width, double height)
    {
        var bounds = new RectD(x, y, width, height);
        var rect = new RectD(1, 2, 3, 4);
        Assert.Equal(default, CropSelectionGeometry.Move(rect, 2, 3, bounds));
        Assert.Equal(default, CropSelectionGeometry.Resize(rect, CropHandle.Right, 2, 3, bounds));
        Assert.Equal(default, CropSelectionGeometry.FitRatio(rect, 1, bounds));
        Assert.Equal(default, CropSelectionGeometry.ResizeTo(rect, 5, 5, bounds));
    }

    [Theory]
    [InlineData(0, 20, 40, 120, 80)]
    [InlineData(1, 180, 20, 80, 120)]
    [InlineData(2, 260, 180, 120, 80)]
    [InlineData(3, 40, 260, 80, 120)]
    [InlineData(-1, 40, 260, 80, 120)]
    [InlineData(5, 180, 20, 80, 120)]
    [InlineData(8, 20, 40, 120, 80)]
    [InlineData(-5, 40, 260, 80, 120)]
    public void CropOrientation_QuarterTurns_MapsCropAndRestoresOriginalCoordinates(int turns,
        double x, double y, double width, double height)
    {
        var source = new SizeD(400, 300);
        var original = new RectD(20, 40, 120, 80);
        var oriented = CropOrientation.ToOriented(original, source, turns);
        AssertRect(new(x, y, width, height), oriented);
        AssertRect(original, CropOrientation.ToSource(oriented, source, turns));
    }

    [Theory]
    [InlineData(0, 20, 40)]
    [InlineData(1, 260, 20)]
    [InlineData(2, 380, 260)]
    [InlineData(3, 40, 380)]
    public void CropOrientation_QuarterTurns_MapsPointerToVisibleImage(int turns, double x, double y)
    {
        var result = CropOrientation.ToOriented(new PointD(20, 40), new SizeD(400, 300), turns);
        Assert.Equal(x, result.X, 9);
        Assert.Equal(y, result.Y, 9);
    }

    [Theory]
    [InlineData(0, 400, 300)]
    [InlineData(1, 300, 400)]
    [InlineData(2, 400, 300)]
    [InlineData(3, 300, 400)]
    public void CropOrientation_FullImage_KeepsZeroOriginAndSwapsDimensions(int turns,
        double width, double height)
    {
        AssertRect(new(0, 0, width, height),
            CropOrientation.ToOriented(new RectD(0, 0, 400, 300), new SizeD(400, 300), turns));
    }

    [Fact]
    public void CropOrientation_VisibleRightHandleAfterClockwiseTurn_ChangesOriginalTopEdge()
    {
        var source = new SizeD(400, 300);
        var oriented = CropOrientation.ToOriented(new RectD(20, 40, 120, 80), source, 1);
        var resized = CropSelectionGeometry.Resize(oriented, CropHandle.Right, 10, 0,
            new RectD(0, 0, 300, 400));
        AssertRect(new(20, 30, 120, 90), CropOrientation.ToSource(resized, source, 1));
    }

    private static void AssertRect(RectD expected, RectD actual)
    {
        Assert.Equal(expected.X, actual.X, 9);
        Assert.Equal(expected.Y, actual.Y, 9);
        Assert.Equal(expected.Width, actual.Width, 9);
        Assert.Equal(expected.Height, actual.Height, 9);
    }

    private static void AssertInside(RectD rect, RectD bounds)
    {
        Assert.True(rect.Width > 0 && rect.Height > 0);
        Assert.InRange(rect.X, bounds.X - 1e-9, bounds.Right + 1e-9);
        Assert.InRange(rect.Y, bounds.Y - 1e-9, bounds.Bottom + 1e-9);
        Assert.InRange(rect.Right, bounds.X - 1e-9, bounds.Right + 1e-9);
        Assert.InRange(rect.Bottom, bounds.Y - 1e-9, bounds.Bottom + 1e-9);
    }
}
