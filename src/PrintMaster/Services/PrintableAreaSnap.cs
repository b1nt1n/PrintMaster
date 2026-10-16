using PrintMaster.Models;

namespace PrintMaster.Services;

public enum SnapAnchor { Start, Center, End }

public sealed record SnapGuide(bool Vertical, double Position, SnapAnchor Anchor);

public sealed record SnapResult(double OffsetX, double OffsetY,
    SnapGuide? VerticalGuide, SnapGuide? HorizontalGuide);

/// <summary>
/// Resolves movement against the printable rectangle in millimetres. The input bounds
/// already include the raw pointer movement; previous offsets are never accumulated.
/// Matching anchors align left to left, centre to centre and right to right (and likewise
/// vertically). Movement outside the sheet remains possible. Each axis holds its current
/// anchor up to the release distance, then chooses the closest anchor inside the capture
/// distance. Exact ties favour edges, with the start edge before the end edge.
/// </summary>
public static class PrintableAreaSnap
{
    public static SnapResult Resolve(RectD movingBounds, RectD safeArea,
        double captureDistanceMm, double releaseDistanceMm, SnapResult? previous = null)
    {
        if (!ValidBounds(movingBounds) || !ValidBounds(safeArea)
            || !double.IsFinite(captureDistanceMm) || captureDistanceMm < 0
            || !double.IsFinite(releaseDistanceMm) || releaseDistanceMm < 0)
            return new(0, 0, null, null);

        // A release threshold smaller than capture cannot supply hysteresis. Retain the
        // capture boundary in that case rather than immediately dropping a new snap.
        var release = Math.Max(captureDistanceMm, releaseDistanceMm);
        var horizontal = ResolveAxis(movingBounds.X, movingBounds.Width,
            safeArea.X, safeArea.Width, true, captureDistanceMm, release,
            previous?.VerticalGuide);
        var vertical = ResolveAxis(movingBounds.Y, movingBounds.Height,
            safeArea.Y, safeArea.Height, false, captureDistanceMm, release,
            previous?.HorizontalGuide);
        return new(horizontal.Offset, vertical.Offset, horizontal.Guide, vertical.Guide);
    }

    private static (double Offset, SnapGuide? Guide) ResolveAxis(double start, double size,
        double targetStart, double targetSize, bool vertical, double capture, double release,
        SnapGuide? previous)
    {
        if (previous is not null && previous.Vertical == vertical)
        {
            var target = Position(targetStart, targetSize, previous.Anchor);
            var offset = target - Position(start, size, previous.Anchor);
            // A guide from a different printable area or an invalid enum is stale state.
            if (double.IsFinite(previous.Position) && previous.Position == target
                && double.IsFinite(offset) && Math.Abs(offset) <= release)
                return (offset, new(vertical, target, previous.Anchor));
        }

        var bestOffset = 0d;
        var bestDistance = capture;
        SnapGuide? bestGuide = null;
        ReadOnlySpan<SnapAnchor> priority = [SnapAnchor.Start, SnapAnchor.End, SnapAnchor.Center];
        foreach (var anchor in priority)
        {
            var target = Position(targetStart, targetSize, anchor);
            var offset = target - Position(start, size, anchor);
            if (!double.IsFinite(offset)) continue;
            var distance = Math.Abs(offset);
            if (distance <= capture && (bestGuide is null || distance < bestDistance))
            {
                bestOffset = offset;
                bestDistance = distance;
                bestGuide = new(vertical, target, anchor);
            }
        }
        return (bestOffset, bestGuide);
    }

    private static double Position(double start, double size, SnapAnchor anchor) => anchor switch
    {
        SnapAnchor.Start => start,
        SnapAnchor.Center => start + size / 2,
        SnapAnchor.End => start + size,
        _ => double.NaN
    };

    private static bool ValidBounds(RectD bounds) =>
        double.IsFinite(bounds.X) && double.IsFinite(bounds.Y)
        && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height)
        && double.IsFinite(bounds.Right) && double.IsFinite(bounds.Bottom)
        && bounds.Width > 0 && bounds.Height > 0;
}
