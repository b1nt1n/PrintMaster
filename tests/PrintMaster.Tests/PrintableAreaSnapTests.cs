using PrintMaster.Models;
using PrintMaster.Services;
using Xunit;

namespace PrintMaster.Tests;

public sealed class PrintableAreaSnapTests
{
    private static readonly RectD Safe = new(5, 7, 200, 280);

    [Theory]
    [InlineData(6.5, 8, SnapAnchor.Start, SnapAnchor.Start, -1.5, -1, 5, 7)]
    [InlineData(184, 256, SnapAnchor.End, SnapAnchor.End, 1, 1, 205, 287)]
    [InlineData(94, 131.5, SnapAnchor.Center, SnapAnchor.Center, 1, .5, 105, 147)]
    [InlineData(6, 257.5, SnapAnchor.Start, SnapAnchor.End, -1, -.5, 5, 287)]
    [InlineData(184.5, 7.5, SnapAnchor.End, SnapAnchor.Start, .5, -.5, 205, 7)]
    public void Resolve_CornersAndCentres_AlignsCorrespondingAnchors(double x, double y,
        SnapAnchor xAnchor, SnapAnchor yAnchor, double dx, double dy, double guideX, double guideY)
    {
        var result = PrintableAreaSnap.Resolve(new(x, y, 20, 30), Safe, 2, 4);

        Assert.Equal(dx, result.OffsetX, 10);
        Assert.Equal(dy, result.OffsetY, 10);
        Assert.Equal(new SnapGuide(true, guideX, xAnchor), result.VerticalGuide);
        Assert.Equal(new SnapGuide(false, guideY, yAnchor), result.HorizontalGuide);
    }

    [Fact]
    public void Resolve_SeveralCandidatesInsideCapture_SelectsNearestPerAxis()
    {
        var result = PrintableAreaSnap.Resolve(new(8, 15, 90, 80), new(0, 0, 100, 100), 20, 25);

        Assert.Equal(2, result.OffsetX);
        Assert.Equal(5, result.OffsetY);
        Assert.Equal(SnapAnchor.End, result.VerticalGuide!.Anchor);
        Assert.Equal(SnapAnchor.End, result.HorizontalGuide!.Anchor);
    }

    [Theory]
    [InlineData(-2, SnapAnchor.Start, 2)]
    [InlineData(-6, SnapAnchor.End, -2)]
    public void Resolve_EqualEdgeAndCentreDistance_PrefersEdge(double x, SnapAnchor anchor,
        double expectedOffset)
    {
        var result = PrintableAreaSnap.Resolve(new(x, 25, 108, 20), new(0, 0, 100, 100), 5, 7);

        Assert.Equal(expectedOffset, result.OffsetX);
        Assert.Equal(anchor, result.VerticalGuide!.Anchor);
        Assert.Null(result.HorizontalGuide);
    }

    [Fact]
    public void Resolve_AllAnchorsCoincide_PrefersStartEdgeDeterministically()
    {
        var result = PrintableAreaSnap.Resolve(Safe, Safe, 0, 0);

        Assert.Equal(0, result.OffsetX);
        Assert.Equal(0, result.OffsetY);
        Assert.Equal(SnapAnchor.Start, result.VerticalGuide!.Anchor);
        Assert.Equal(SnapAnchor.Start, result.HorizontalGuide!.Anchor);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(-2, true)]
    [InlineData(2.0001, false)]
    [InlineData(-2.0001, false)]
    public void Resolve_CaptureBoundary_IsInclusive(double distance, bool captured)
    {
        var result = PrintableAreaSnap.Resolve(new(5 + distance, 40, 20, 30), Safe, 2, 4);

        Assert.Equal(captured, result.VerticalGuide is not null);
        Assert.Equal(captured ? -distance : 0, result.OffsetX, 10);
        Assert.Null(result.HorizontalGuide);
    }

    [Fact]
    public void Resolve_OnlyOneAxisNearTarget_DoesNotConstrainOtherAxis()
    {
        var result = PrintableAreaSnap.Resolve(new(4.5, -50, 20, 30), Safe, 2, 4);

        Assert.Equal(.5, result.OffsetX);
        Assert.Equal(0, result.OffsetY);
        Assert.Equal(new SnapGuide(true, 5, SnapAnchor.Start), result.VerticalGuide);
        Assert.Null(result.HorizontalGuide);
    }

    [Fact]
    public void Resolve_OutsideCapture_DoesNotClampToPrintableArea()
    {
        var result = PrintableAreaSnap.Resolve(new(-50, 350, 20, 30), Safe, 2, 4);

        AssertUnsnapped(result);
    }

    [Fact]
    public void Resolve_ObjectCentreNearSheetEdge_DoesNotMatchDifferentAnchors()
    {
        var result = PrintableAreaSnap.Resolve(new(-5, -8, 20, 30), Safe, 2, 4);

        // Its centre lies exactly on the top-left printable corner, but half of the
        // object is outside. Only matching anchors may attract it.
        AssertUnsnapped(result);
    }

    [Theory]
    [InlineData(3.5, true)]
    [InlineData(4, true)]
    [InlineData(4.0001, false)]
    public void Resolve_PreviousAnchor_HoldsUntilInclusiveReleaseBoundary(double rawDistance,
        bool held)
    {
        var previous = PrintableAreaSnap.Resolve(new(6, 40, 20, 30), Safe, 2, 4);
        var result = PrintableAreaSnap.Resolve(new(5 + rawDistance, 40, 20, 30), Safe, 2, 4, previous);

        Assert.Equal(held, result.VerticalGuide is not null);
        Assert.Equal(held ? -rawDistance : 0, result.OffsetX, 10);
        Assert.Null(result.HorizontalGuide);
    }

    [Fact]
    public void Resolve_HeldAnchor_RemainsStableWhenAnotherAnchorIsCloser()
    {
        var safe = new RectD(0, 0, 100, 100);
        var previous = PrintableAreaSnap.Resolve(new(0, 35, 92, 20), safe, 1, 6);
        var result = PrintableAreaSnap.Resolve(new(4, 35, 92, 20), safe, 1, 6, previous);

        Assert.Equal(-4, result.OffsetX);
        Assert.Equal(SnapAnchor.Start, result.VerticalGuide!.Anchor);
    }

    [Fact]
    public void Resolve_ReleasedAnchor_CapturesNewClosestAnchor()
    {
        var safe = new RectD(0, 0, 100, 100);
        var previous = PrintableAreaSnap.Resolve(new(0, 35, 80, 20), safe, 2, 4);
        var result = PrintableAreaSnap.Resolve(new(9, 35, 80, 20), safe, 2, 4, previous);

        Assert.Equal(1, result.OffsetX);
        Assert.Equal(SnapAnchor.Center, result.VerticalGuide!.Anchor);
    }

    [Fact]
    public void Resolve_AxesReleaseIndependently_KeepsRemainingGuide()
    {
        var previous = PrintableAreaSnap.Resolve(new(6, 8, 20, 30), Safe, 2, 4);
        var result = PrintableAreaSnap.Resolve(new(10, 10.5, 20, 30), Safe, 2, 4, previous);

        Assert.Equal(0, result.OffsetX);
        Assert.Null(result.VerticalGuide);
        Assert.Equal(-3.5, result.OffsetY);
        Assert.Equal(new SnapGuide(false, 7, SnapAnchor.Start), result.HorizontalGuide);
    }

    [Fact]
    public void Resolve_RepeatedRawBounds_RecomputesCorrectionWithoutAccumulatingOffsets()
    {
        var first = PrintableAreaSnap.Resolve(new(6, 8, 20, 30), Safe, 2, 4);
        var second = PrintableAreaSnap.Resolve(new(7, 9, 20, 30), Safe, 2, 4, first);
        var third = PrintableAreaSnap.Resolve(new(7, 9, 20, 30), Safe, 2, 4, second);

        Assert.Equal(-2, second.OffsetX);
        Assert.Equal(-2, second.OffsetY);
        Assert.Equal(second, third);
    }

    [Fact]
    public void Resolve_PreviousOffsetsAreInvalid_UsesOnlyValidGuides()
    {
        var previous = new SnapResult(double.NaN, double.PositiveInfinity,
            new(true, 5, SnapAnchor.Start), new(false, 7, SnapAnchor.Start));
        var result = PrintableAreaSnap.Resolve(new(8, 10, 20, 30), Safe, 2, 4, previous);

        Assert.Equal(-3, result.OffsetX);
        Assert.Equal(-3, result.OffsetY);
        Assert.NotNull(result.VerticalGuide);
        Assert.NotNull(result.HorizontalGuide);
    }

    [Theory]
    [InlineData(true, 999, SnapAnchor.Start)]
    [InlineData(false, 5, SnapAnchor.Start)]
    [InlineData(true, double.NaN, SnapAnchor.Start)]
    [InlineData(true, double.PositiveInfinity, SnapAnchor.Start)]
    [InlineData(true, 5, (SnapAnchor)99)]
    public void Resolve_InvalidPreviousGuide_DoesNotExtendCapture(bool vertical, double position,
        SnapAnchor anchor)
    {
        var previous = new SnapResult(0, 0, new(vertical, position, anchor), null);
        var result = PrintableAreaSnap.Resolve(new(8, 40, 20, 30), Safe, 2, 4, previous);

        AssertUnsnapped(result);
    }

    [Fact]
    public void Resolve_ChangedPrintableArea_DiscardsOldGuidePosition()
    {
        var previous = PrintableAreaSnap.Resolve(new(6, 40, 20, 30), Safe, 2, 4);
        var result = PrintableAreaSnap.Resolve(new(10, 40, 20, 30), Safe with { X = 7 }, 2, 4, previous);

        AssertUnsnapped(result);
    }

    [Fact]
    public void Resolve_ZeroCapture_OnlyExactAlignmentCaptures()
    {
        var aligned = PrintableAreaSnap.Resolve(new(5, 40, 20, 30), Safe, 0, 0);
        var nearby = PrintableAreaSnap.Resolve(new(5.0001, 40, 20, 30), Safe, 0, 0);

        Assert.Equal(0, aligned.OffsetX);
        Assert.NotNull(aligned.VerticalGuide);
        AssertUnsnapped(nearby);
    }

    [Fact]
    public void Resolve_ReleaseSmallerThanCapture_RetainsCaptureBoundary()
    {
        var previous = PrintableAreaSnap.Resolve(new(6, 40, 20, 30), Safe, 2, 1);
        var result = PrintableAreaSnap.Resolve(new(7, 40, 20, 30), Safe, 2, 1, previous);

        Assert.Equal(-2, result.OffsetX);
        Assert.Equal(SnapAnchor.Start, result.VerticalGuide!.Anchor);
    }

    [Fact]
    public void Resolve_OversizedObject_KeepsItsSizeAndAlignsNearestMatchingAnchor()
    {
        var result = PrintableAreaSnap.Resolve(new(4, -34, 250, 320), Safe, 2, 4);

        Assert.Equal(1, result.OffsetX);
        Assert.Equal(1, result.OffsetY);
        Assert.Equal(SnapAnchor.Start, result.VerticalGuide!.Anchor);
        Assert.Equal(SnapAnchor.End, result.HorizontalGuide!.Anchor);
    }

    [Fact]
    public void Resolve_NegativeSafeOrigin_UsesActualPrintableCoordinates()
    {
        var result = PrintableAreaSnap.Resolve(new(-18, -20, 20, 30), new(-20, -60, 80, 100), 2, 4);

        Assert.Equal(-2, result.OffsetX);
        Assert.Equal(0, result.OffsetY);
        Assert.Equal(-20, result.VerticalGuide!.Position);
        Assert.Null(result.HorizontalGuide);
    }

    [Theory]
    [InlineData(double.NaN, 4)]
    [InlineData(double.PositiveInfinity, 4)]
    [InlineData(double.NegativeInfinity, 4)]
    [InlineData(2, double.NaN)]
    [InlineData(2, double.PositiveInfinity)]
    [InlineData(2, double.NegativeInfinity)]
    [InlineData(-1, 4)]
    [InlineData(2, -1)]
    public void Resolve_InvalidThresholds_ReturnsNoCorrection(double capture, double release)
    {
        var previous = new SnapResult(-1, -1, new(true, 5, SnapAnchor.Start), new(false, 7, SnapAnchor.Start));

        AssertUnsnapped(PrintableAreaSnap.Resolve(new(6, 8, 20, 30), Safe, capture, release, previous));
    }

    public static IEnumerable<object[]> InvalidBounds()
    {
        yield return [new RectD(double.NaN, 7, 20, 30)];
        yield return [new RectD(5, double.PositiveInfinity, 20, 30)];
        yield return [new RectD(5, 7, double.NegativeInfinity, 30)];
        yield return [new RectD(5, 7, 20, double.NaN)];
        yield return [new RectD(5, 7, 0, 30)];
        yield return [new RectD(5, 7, 20, 0)];
        yield return [new RectD(5, 7, -20, 30)];
        yield return [new RectD(5, 7, 20, -30)];
        yield return [new RectD(double.MaxValue, 7, double.MaxValue, 30)];
        yield return [new RectD(5, double.MaxValue, 20, double.MaxValue)];
    }

    [Theory]
    [MemberData(nameof(InvalidBounds))]
    public void Resolve_InvalidMovingBounds_ReturnsNoCorrection(RectD bounds)
    {
        AssertUnsnapped(PrintableAreaSnap.Resolve(bounds, Safe, 2, 4));
    }

    [Theory]
    [MemberData(nameof(InvalidBounds))]
    public void Resolve_InvalidPrintableBounds_ReturnsNoCorrection(RectD bounds)
    {
        AssertUnsnapped(PrintableAreaSnap.Resolve(new(6, 8, 20, 30), bounds, 2, 4));
    }

    [Fact]
    public void Resolve_FiniteBoundsWhoseDifferenceOverflows_ReturnsFiniteNoCorrection()
    {
        var result = PrintableAreaSnap.Resolve(new(-double.MaxValue, 40, 20, 30),
            new(double.MaxValue / 2, 7, 200, 280), 2, 4);

        AssertUnsnapped(result);
    }

    private static void AssertUnsnapped(SnapResult result)
    {
        Assert.Equal(0, result.OffsetX);
        Assert.Equal(0, result.OffsetY);
        Assert.Null(result.VerticalGuide);
        Assert.Null(result.HorizontalGuide);
    }
}
