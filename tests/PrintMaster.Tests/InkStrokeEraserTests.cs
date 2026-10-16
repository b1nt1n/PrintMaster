using System.Collections.Immutable;
using PrintMaster.Models;
using PrintMaster.Services;
using Xunit;

namespace PrintMaster.Tests;

public sealed class InkStrokeEraserTests
{
    private static void Point(PointD expected, PointD actual)
    { Assert.Equal(expected.X, actual.X, 8); Assert.Equal(expected.Y, actual.Y, 8); }
    private static void Path(IReadOnlyList<PointD> expected, IReadOnlyList<PointD> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++) Point(expected[i], actual[i]);
    }

    [Theory]
    [InlineData(0, 1, 4, 6)]
    [InlineData(.6, 1, 4.2, 5.8)]
    [InlineData(0, 2, 3, 7)]
    public void Erase_CircleCrossesLongSegment_PreservesBothExactIntersections(double y,
        double radius, double left, double right)
    {
        var result = InkStrokeEraser.Erase([new(0, y), new(10, y)], new(5, 0), new(5, 0), radius);

        Assert.True(result.Changed); Assert.Equal(2, result.Fragments.Length);
        Path([new(0, y), new(left, y)], result.Fragments[0]);
        Path([new(right, y), new(10, y)], result.Fragments[1]);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(1.2, 1)]
    public void Erase_WholeSegmentInsideCircle_RemovesOnlyPositiveInterior(double y, double radius)
    {
        var result = InkStrokeEraser.Erase([new(4.8, y), new(5.2, y)], new(5, 0), new(5, 0), radius);
        if (y < radius) { Assert.True(result.Changed); Assert.Empty(result.Fragments); }
        else { Assert.False(result.Changed); Path([new(4.8, y), new(5.2, y)], Assert.Single(result.Fragments)); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void Erase_CircleTangent_DoesNotSplitOrRemoveStroke(double y)
    {
        PointD[] source = [new(0, y), new(10, y)];
        var result = InkStrokeEraser.Erase(source, new(5, 0), new(5, 0), 1);
        Assert.False(result.Changed); Path(source, Assert.Single(result.Fragments));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Erase_FastSweepBetweenPointerEvents_CutsCapsuleNotOnlyEndpoints(bool reverse)
    {
        var from = new PointD(5, -5); var to = new PointD(5, 5);
        var result = InkStrokeEraser.Erase([new(0, 0), new(10, 0)], reverse ? to : from,
            reverse ? from : to, 1);
        Assert.True(result.Changed); Assert.Equal(2, result.Fragments.Length);
        Path([new(0, 0), new(4, 0)], result.Fragments[0]);
        Path([new(6, 0), new(10, 0)], result.Fragments[1]);
    }

    [Theory]
    [InlineData(0, 1, 2, 8)]
    [InlineData(.6, 1, 2.2, 7.8)]
    public void Erase_StrokeParallelToSweep_CutsBodyAndBothRoundedEnds(double y,
        double radius, double left, double right)
    {
        var result = InkStrokeEraser.Erase([new(0, y), new(10, y)], new(3, 0), new(7, 0), radius);
        Assert.True(result.Changed); Assert.Equal(2, result.Fragments.Length);
        Path([new(0, y), new(left, y)], result.Fragments[0]);
        Path([new(right, y), new(10, y)], result.Fragments[1]);
    }

    [Fact]
    public void Erase_StrokeAlongCapsuleBoundary_DoesNotChangeStroke()
    {
        PointD[] source = [new(0, 1), new(10, 1)];
        var result = InkStrokeEraser.Erase(source, new(3, 0), new(7, 0), 1);
        Assert.False(result.Changed); Path(source, Assert.Single(result.Fragments));
    }

    [Fact]
    public void Erase_EndpointCapIntersectionBeyondSweepBody_CutsAtCircleIntersections()
    {
        var result = InkStrokeEraser.Erase([new(4.8, -3), new(4.8, 3)], new(0, 0), new(4, 0), 1);
        Assert.Equal(2, result.Fragments.Length);
        Path([new(4.8, -3), new(4.8, -.6)], result.Fragments[0]);
        Path([new(4.8, .6), new(4.8, 3)], result.Fragments[1]);
    }

    [Fact]
    public void Erase_MultiSegmentCrossing_DoesNotJoinAcrossErasedMiddle()
    {
        var result = InkStrokeEraser.Erase([new(0, 0), new(4.5, 0), new(5.5, 0), new(10, 0)],
            new(5, 0), new(5, 0), 1);
        Assert.Equal(2, result.Fragments.Length);
        Path([new(0, 0), new(4, 0)], result.Fragments[0]);
        Path([new(6, 0), new(10, 0)], result.Fragments[1]);
    }

    [Fact]
    public void Erase_VerticesOnBoundary_DoesNotLeaveIsolatedBoundaryDots()
    {
        var result = InkStrokeEraser.Erase([new(4, 0), new(5, 0), new(6, 0)], new(5, 0), new(5, 0), 1);
        Assert.True(result.Changed); Assert.Empty(result.Fragments);
    }

    [Fact]
    public void Erase_BoundaryVertexWithOutsideNeighbours_RemainsInItsOutsideFragment()
    {
        var result = InkStrokeEraser.Erase([new(0, 0), new(4, 0), new(5, 0), new(6, 0), new(10, 0)],
            new(5, 0), new(5, 0), 1);
        Assert.Equal(2, result.Fragments.Length);
        Path([new(0, 0), new(4, 0)], result.Fragments[0]);
        Path([new(6, 0), new(10, 0)], result.Fragments[1]);
    }

    [Fact]
    public void Erase_ClosedStrokeWithSingleGap_ConnectsSurvivingPathAcrossOriginalStart()
    {
        var result = InkStrokeEraser.Erase([new(0, 0), new(10, 0), new(10, 10), new(0, 10), new(0, 0)],
            new(5, 0), new(5, 0), 1);
        Assert.True(result.Changed);
        Path([new(6, 0), new(10, 0), new(10, 10), new(0, 10), new(0, 0), new(4, 0)],
            Assert.Single(result.Fragments));
    }

    [Fact]
    public void Erase_TwoCutsInClosedStroke_ProducesTwoContinuousSurvivors()
    {
        var result = InkStrokeEraser.Erase([new(0, 0), new(10, 0), new(10, 10), new(0, 10), new(0, 0)],
            new(5, -2), new(5, 12), 1);
        Assert.Equal(2, result.Fragments.Length);
        Path([new(4, 10), new(0, 10), new(0, 0), new(4, 0)], result.Fragments[0]);
        Path([new(6, 0), new(10, 0), new(10, 10), new(6, 10)], result.Fragments[1]);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(.8, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    public void Erase_SinglePointOrRepeatedPoint_HandlesDotWithoutDivisionByZero(double x, bool changed)
    {
        foreach (var points in new PointD[][] { [new(x, 0)], [new(x, 0), new(x, 0), new(x, 0)] })
        {
            var result = InkStrokeEraser.Erase(points, new(0, 0), new(0, 0), 1);
            Assert.Equal(changed, result.Changed);
            if (changed) Assert.Empty(result.Fragments);
            else Path(points, Assert.Single(result.Fragments));
        }
    }

    [Fact]
    public void Erase_ConsecutiveRepeatedVerticesDuringCrossing_ProducesFiniteCleanPaths()
    {
        var result = InkStrokeEraser.Erase([new(0, 0), new(0, 0), new(5, 0), new(5, 0), new(10, 0)],
            new(5, 0), new(5, 0), 1);
        Assert.Equal(2, result.Fragments.Length);
        Path([new(0, 0), new(4, 0)], result.Fragments[0]);
        Path([new(6, 0), new(10, 0)], result.Fragments[1]);
    }

    [Fact]
    public void Erase_SequentialPointerCapsules_LeavesAllRemainingFragmentsOutsideSweptRoute()
    {
        var first = InkStrokeEraser.Erase([new(0, 0), new(20, 0)], new(5, 0), new(5, 0), 1);
        var second = first.Fragments.SelectMany(p => InkStrokeEraser.Erase(p, new(5, 0), new(15, 0), 1).Fragments).ToArray();
        Assert.Equal(2, second.Length);
        Path([new(0, 0), new(4, 0)], second[0]);
        Path([new(16, 0), new(20, 0)], second[1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(-45)]
    public void Erase_NormalizedRotatedInk_PreservesPlacementStyleAndExactGap(double angle)
    {
        var source = new InkObject(Guid.NewGuid(), new(new(10, 20, 40, 20), angle),
            [new(0, .5), new(1, .5)], new("#123456", 2, .35));
        var result = InkStrokeEraser.Erase(source, new(30, 30), new(30, 30), 2);

        Assert.True(result.Changed); Assert.Equal(2, result.Fragments.Length);
        Assert.Equal(source.Id, result.Fragments[0].Id); Assert.NotEqual(source.Id, result.Fragments[1].Id);
        Assert.NotEqual(result.Fragments[0].Id, result.Fragments[1].Id);
        foreach (var fragment in result.Fragments)
        {
            Assert.Equal(source.Placement, fragment.Placement); Assert.Equal(source.Style, fragment.Style);
            Assert.Equal(source.Visible, fragment.Visible); Assert.Equal(source.Locked, fragment.Locked);
            Assert.All(fragment.Points, p => { Assert.InRange(p.X, 0, 1); Assert.InRange(p.Y, 0, 1); });
        }
        Path([new(0, .5), new(.425, .5)], result.Fragments[0].Points);
        Path([new(.575, .5), new(1, .5)], result.Fragments[1].Points);
        Path([new(0, .5), new(1, .5)], source.Points);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Erase_InvisibleOrLockedInk_DoesNotMutateSource(bool visible, bool locked)
    {
        var source = new InkObject(Guid.NewGuid(), new(new(10, 20, 40, 20)),
            [new(0, .5), new(1, .5)], new(), visible, locked);
        var result = InkStrokeEraser.Erase(source, new(30, 30), new(30, 30), 10);
        Assert.False(result.Changed); Assert.Same(source, Assert.Single(result.Fragments));
    }

    [Fact]
    public void Erase_UnchangedInk_ReturnsSameInstanceAndDoesNotRewriteRepeatedPoints()
    {
        var source = new InkObject(Guid.NewGuid(), new(new(10, 20, 40, 20)),
            [new(0, .5), new(0, .5), new(1, .5)], new());
        var result = InkStrokeEraser.Erase(source, new(100, 100), new(200, 100), 10);
        Assert.False(result.Changed); Assert.Same(source, Assert.Single(result.Fragments));
    }

    [Theory]
    [InlineData(AnnotationKind.Ink)]
    [InlineData(AnnotationKind.Marker)]
    public void Erase_SourceAnnotation_PreservesAppearanceAndUpdatesEndpoints(AnnotationKind kind)
    {
        var source = Annotation.Create(kind, new(0, 0), new(10, 0), "#123456", 2,
            points: [new(0, 0), new(10, 0)]) with { Opacity = .27, FillColor = "#ABCDEF" };
        var result = InkStrokeEraser.Erase(source, new(5, 0), new(5, 0), 1);
        Assert.True(result.Changed); Assert.Equal(2, result.Fragments.Length);
        foreach (var fragment in result.Fragments)
        {
            Assert.Equal(source with { Id = fragment.Id, Start = fragment.Start, End = fragment.End, Points = fragment.Points }, fragment);
            Assert.Equal(fragment.Points[0], fragment.Start); Assert.Equal(fragment.Points[^1], fragment.End);
        }
        Assert.Equal(source.Id, result.Fragments[0].Id);
        Path([new(0, 0), new(3, 0)], result.Fragments[0].Points);
        Path([new(7, 0), new(10, 0)], result.Fragments[1].Points);
    }

    [Theory]
    [InlineData(AnnotationKind.Arrow)]
    [InlineData(AnnotationKind.Rectangle)]
    [InlineData(AnnotationKind.Ellipse)]
    [InlineData(AnnotationKind.Text)]
    public void Erase_NonInkAnnotation_DoesNotEraseObjectsOrText(AnnotationKind kind)
    {
        var source = Annotation.Create(kind, new(0, 0), new(10, 0), "#123456", 2, "Оригинал");
        var result = InkStrokeEraser.Erase(source, new(5, 0), new(5, 0), 100);
        Assert.False(result.Changed); Assert.Same(source, Assert.Single(result.Fragments));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 30)]
    [InlineData(2, 90)]
    [InlineData(3, -45)]
    public void Erase_TransformedAnnotation_InvertsQuarterTurnCropAndObjectRotation(int quarterTurns, double angle)
    {
        var annotation = Annotation.Create(AnnotationKind.Marker, new(100, 200), new(500, 200),
            "#112233", 2, points: [new(100, 200), new(500, 200)]);
        var content = new ContentObject(Guid.NewGuid(), new(new(10, 20, 80, 60), angle),
            Guid.NewGuid(), new(new(100, 100, 400, 300), quarterTurns, [annotation]));
        var transform = AnnotationTextLayout.Transform(content, content.Placement.BoundsMm);
        var center = transform.Transform(new(300, 200));
        var result = InkStrokeEraser.Erase(content, center, center, 2);
        Assert.True(result.Changed);
        var updated = Assert.Single(result.Fragments);
        Assert.Equal(content.Placement, updated.Placement); Assert.Equal(content.AssetId, updated.AssetId);
        Assert.Equal(content.Edits.Crop, updated.Edits.Crop); Assert.Equal(quarterTurns, updated.Edits.QuarterTurns);
        Assert.Equal(2, updated.Edits.Annotations.Length);
        var scaleX = Math.Sqrt(transform.M11 * transform.M11 + transform.M12 * transform.M12);
        var scaleY = Math.Sqrt(transform.M21 * transform.M21 + transform.M22 * transform.M22);
        var halfGap = (2 + Math.Max(scaleX, scaleY)) / scaleX;
        Path([new(100, 200), new(300 - halfGap, 200)], updated.Edits.Annotations[0].Points);
        Path([new(300 + halfGap, 200), new(500, 200)], updated.Edits.Annotations[1].Points);
        Assert.Equal(85d / 255, updated.Edits.Annotations[1].EffectiveOpacity);
    }

    [Fact]
    public void Erase_ContentVisibleCrop_PreservesHiddenInkEvenWhenEraserCrossesCropEdge()
    {
        var ink = Annotation.Create(AnnotationKind.Ink, new(0, 10), new(100, 10), "#000000", 1,
            points: [new(0, 10), new(100, 10)]);
        var text = Annotation.Create(AnnotationKind.Text, new(40, 10), new(50, 20), "#000000", 1, "Текст");
        var content = new ContentObject(Guid.NewGuid(), new(new(10, 20, 40, 20)), Guid.NewGuid(),
            new(new(30, 0, 40, 20), 0, [ink, text]));
        var result = InkStrokeEraser.Erase(content, new(-5, 30), new(-5, 30), 25);
        var updated = Assert.Single(result.Fragments);
        Assert.True(result.Changed); Assert.Equal(3, updated.Edits.Annotations.Length);
        Path([new(0, 10), new(30, 10)], updated.Edits.Annotations[0].Points);
        Path([new(40.5, 10), new(100, 10)], updated.Edits.Annotations[1].Points);
        Assert.Same(text, updated.Edits.Annotations[2]);
        Assert.Equal(content.Id, updated.Id); Assert.Equal(content.Visible, updated.Visible); Assert.Equal(content.Locked, updated.Locked);
        Assert.Equal(2, content.Edits.Annotations.Length);
    }

    [Fact]
    public void Erase_StrokeEntirelyOutsideCrop_ReturnsUnchangedContentIdentity()
    {
        var ink = Annotation.Create(AnnotationKind.Ink, new(0, 10), new(20, 10), "#000000", 1,
            points: [new(0, 10), new(20, 10)]);
        var content = new ContentObject(Guid.NewGuid(), new(new(10, 20, 40, 20)), Guid.NewGuid(),
            new(new(30, 0, 40, 20), 0, [ink]));
        var result = InkStrokeEraser.Erase(content, new(-10, 30), new(-10, 30), 100);
        Assert.False(result.Changed); Assert.Same(content, Assert.Single(result.Fragments));
        Assert.Same(ink, content.Edits.Annotations[0]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Erase_InvisibleOrLockedContent_ReturnsOriginalInstance(bool visible, bool locked)
    {
        var ink = Annotation.Create(AnnotationKind.Ink, new(0, 10), new(20, 10), "#000000", 1,
            points: [new(0, 10), new(20, 10)]);
        var content = new ContentObject(Guid.NewGuid(), new(new(0, 0, 20, 20)), Guid.NewGuid(),
            new(new(0, 0, 20, 20), 0, [ink]), visible, locked);
        var result = InkStrokeEraser.Erase(content, new(10, 10), new(10, 10), 100);
        Assert.False(result.Changed); Assert.Same(content, Assert.Single(result.Fragments));
    }

    [Fact]
    public void Erase_EmptyPathOrZeroRadius_IsNoOp()
    {
        var empty = InkStrokeEraser.Erase([], new(0, 0), new(0, 0), 1);
        Assert.False(empty.Changed); Assert.Empty(empty.Fragments);
        var zero = InkStrokeEraser.Erase([new(0, 0), new(10, 0)], new(5, 0), new(5, 0), 0);
        Assert.False(zero.Changed); Path([new(0, 0), new(10, 0)], Assert.Single(zero.Fragments));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Erase_InvalidRadius_RejectsInvalidInput(double radius)
        => Assert.Throws<ArgumentOutOfRangeException>(() => InkStrokeEraser.Erase([new(0, 0)], new(0, 0), new(1, 1), radius));

    [Fact]
    public void Erase_InvalidPointOrTransform_RejectsInvalidInput()
    {
        Assert.Throws<ArgumentException>(() => InkStrokeEraser.Erase([new(double.NaN, 0)], new(0, 0), new(0, 0), 1));
        Assert.Throws<ArgumentException>(() => InkStrokeEraser.Erase([new(0, 0)], new(double.PositiveInfinity, 0), new(0, 0), 1));
        var ink = Annotation.Create(AnnotationKind.Ink, new(0, 0), new(1, 1), "#000000", 1);
        Assert.Throws<InvalidOperationException>(() => InkStrokeEraser.Erase(ink, new Affine(), new(0, 0), new(0, 0), 1));
    }
}
