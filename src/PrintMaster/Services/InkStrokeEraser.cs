using System.Collections.Immutable;
using PrintMaster.Models;

namespace PrintMaster.Services;

public sealed record InkEraseResult<T>(bool Changed, ImmutableArray<T> Fragments);

/// <summary>
/// Removes the interior of a circular or swept circular eraser from a polyline.
/// The core works in world coordinates; model overloads preserve immutable style and placement.
/// </summary>
public static class InkStrokeEraser
{
    private const double Epsilon = 1e-10;

    public static InkEraseResult<ImmutableArray<PointD>> Erase(IReadOnlyList<PointD> points,
        PointD from, PointD to, double radius) => EraseCore(points, from, to, radius);

    public static InkEraseResult<InkObject> Erase(InkObject stroke, PointD fromMm, PointD toMm,
        double radiusMm)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        ValidateEraser(fromMm, toMm, radiusMm);
        if (!stroke.Visible || stroke.Locked || radiusMm == 0)
            return new(false, [stroke]);

        var bounds = stroke.Placement.BoundsMm;
        var center = ObjectTransform.Center(stroke);
        PointD ToWorld(PointD p)
        {
            var rotated = ObjectTransform.RotateVector(new((p.X - .5) * bounds.Width,
                (p.Y - .5) * bounds.Height), stroke.Placement.RotationDegrees);
            return new(center.X + rotated.X, center.Y + rotated.Y);
        }
        PointD ToNormalized(PointD p)
        {
            var local = ObjectTransform.RotateVector(new(p.X - center.X, p.Y - center.Y),
                -stroke.Placement.RotationDegrees);
            return new(Math.Clamp(.5 + local.X / bounds.Width, 0, 1),
                Math.Clamp(.5 + local.Y / bounds.Height, 0, 1));
        }

        // Round stroke caps must end outside the erased disk, rather than bleed back into it.
        var result = Erase(stroke.Points.Select(ToWorld).ToArray(), fromMm, toMm,
            radiusMm + stroke.Style.WidthMm / 2);
        if (!result.Changed) return new(false, [stroke]);
        return new(true, result.Fragments.Select((p, index) => stroke with
        {
            Id = index == 0 ? stroke.Id : Guid.NewGuid(),
            Points = p.Select(ToNormalized).ToImmutableArray()
        }).ToImmutableArray());
    }

    public static InkEraseResult<Annotation> Erase(Annotation stroke, PointD from, PointD to,
        double radius) => Erase(stroke, new Affine(1, 0, 0, 1, 0, 0), from, to, radius);

    public static InkEraseResult<Annotation> Erase(Annotation stroke, Affine sourceToWorld,
        PointD from, PointD to, double radius, RectD? visibleCrop = null)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        ValidateEraser(from, to, radius);
        if (stroke.Kind is not (AnnotationKind.Ink or AnnotationKind.Marker) || radius == 0)
            return new(false, [stroke]);

        ValidateTransform(sourceToWorld);
        var points = stroke.Points.Count == 0 ? new[] { stroke.Start } : stroke.Points.ToArray();
        var scaleX = Math.Sqrt(sourceToWorld.M11 * sourceToWorld.M11 + sourceToWorld.M12 * sourceToWorld.M12);
        var scaleY = Math.Sqrt(sourceToWorld.M21 * sourceToWorld.M21 + sourceToWorld.M22 * sourceToWorld.M22);
        var result = EraseCore(points.Select(sourceToWorld.Transform).ToArray(), from, to,
            radius + stroke.Thickness * Math.Max(scaleX, scaleY) / 2, points, visibleCrop);
        if (!result.Changed) return new(false, [stroke]);
        return new(true, result.Fragments.Select((fragment, index) =>
        {
            var source = fragment.Select(sourceToWorld.Inverse).ToImmutableArray();
            return stroke with
            {
                Id = index == 0 ? stroke.Id : Guid.NewGuid(),
                Start = source[0], End = source[^1], Points = source
            };
        }).ToImmutableArray());
    }

    public static InkEraseResult<ContentObject> Erase(ContentObject content, PointD fromMm,
        PointD toMm, double radiusMm)
    {
        ArgumentNullException.ThrowIfNull(content);
        ValidateEraser(fromMm, toMm, radiusMm);
        if (!content.Visible || content.Locked || radiusMm == 0) return new(false, [content]);
        var transform = AnnotationTextLayout.Transform(content, content.Placement.BoundsMm);
        var annotations = ImmutableArray.CreateBuilder<Annotation>();
        var changed = false;
        foreach (var annotation in content.Edits.Annotations)
        {
            var result = Erase(annotation, transform, fromMm, toMm, radiusMm, content.Edits.Crop);
            annotations.AddRange(result.Fragments);
            changed |= result.Changed;
        }
        return changed ? new(true, [content with { Edits = content.Edits with { Annotations = annotations.ToImmutable() } }])
            : new(false, [content]);
    }

    private static InkEraseResult<ImmutableArray<PointD>> EraseCore(IReadOnlyList<PointD> points,
        PointD from, PointD to, double radius, IReadOnlyList<PointD>? cropPoints = null,
        RectD? visibleCrop = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        ValidateEraser(from, to, radius);
        if (points.Any(p => !Finite(p))) throw new ArgumentException("Точки штриха должны быть конечными.", nameof(points));
        if (visibleCrop is {} crop && (!Finite(new(crop.X, crop.Y)) || !Finite(new(crop.Width, crop.Height)) || crop.IsEmpty))
            throw new ArgumentException("Видимый кроп должен иметь конечный положительный размер.", nameof(visibleCrop));
        if (points.Count == 0) return new(false, []);
        if (radius == 0) return new(false, [points.ToImmutableArray()]);

        var distinct = new List<PointD> { points[0] };
        var originalIndexes = new List<int> { 0 };
        for (var i = 1; i < points.Count; i++)
            if (!SamePoint(points[i], distinct[^1])) { distinct.Add(points[i]); originalIndexes.Add(i); }
        if (distinct.Count == 1)
        {
            var inCrop = visibleCrop is not {} clip || clip.Contains(cropPoints![0]);
            var eraseDot = inCrop && InsideCapsule(distinct[0], from, to, radius);
            return eraseDot ? new(true, []) : new(false, [points.ToImmutableArray()]);
        }

        var fragments = new List<ImmutableArray<PointD>>();
        var run = new List<PointD> { distinct[0] };
        var changed = false;
        void Add(PointD point) { if (run.Count == 0 || !SamePoint(run[^1], point)) run.Add(point); }
        void Flush(bool keepDot = true)
        {
            if (run.Count > 1 || keepDot && run.Count == 1) fragments.Add(run.ToImmutableArray());
            run.Clear();
        }

        for (var i = 1; i < distinct.Count; i++)
        {
            var start = distinct[i - 1]; var end = distinct[i];
            if (!CutInterval(start, end, from, to, radius, out var low, out var high))
            { Add(start); Add(end); continue; }

            if (visibleCrop is {} clip)
            {
                if (!RectangleInterval(cropPoints![originalIndexes[i - 1]], cropPoints[originalIndexes[i]],
                    clip, out var clipLow, out var clipHigh)) { Add(start); Add(end); continue; }
                low = Math.Max(low, clipLow); high = Math.Min(high, clipHigh);
                if (high - low <= Epsilon) { Add(start); Add(end); continue; }
            }

            changed = true;
            if (low > Epsilon) { Add(start); Add(Lerp(start, end, low)); }
            // An isolated boundary point is not a remaining painted fragment.
            Flush(keepDot: false);
            if (high < 1 - Epsilon) { Add(Lerp(start, end, high)); Add(end); }
        }
        Flush();
        if (!changed) return new(false, [points.ToImmutableArray()]);

        // A closed pen stroke is one continuous path across its original start point.
        if (fragments.Count > 1 && SamePoint(distinct[0], distinct[^1]) &&
            SamePoint(fragments[0][0], fragments[^1][^1]))
        {
            var joined = fragments[^1].AddRange(fragments[0].Skip(1));
            fragments[0] = joined; fragments.RemoveAt(fragments.Count - 1);
        }
        return new(true, fragments.ToImmutableArray());
    }

    private static bool CutInterval(PointD start, PointD end, PointD from, PointD to,
        double radius, out double low, out double high)
    {
        var lower = double.PositiveInfinity; var upper = double.NegativeInfinity;
        var a = new PointD(to.X - from.X, to.Y - from.Y);
        var length = Math.Sqrt(Dot(a, a));
        void Union(double left, double right)
        { lower = Math.Min(lower, left); upper = Math.Max(upper, right); }

        if (CircleInterval(start, end, from, radius, out var circleLow, out var circleHigh)) Union(circleLow, circleHigh);
        if (length > Epsilon)
        {
            if (CircleInterval(start, end, to, radius, out circleLow, out circleHigh)) Union(circleLow, circleHigh);
            var unit = new PointD(a.X / length, a.Y / length);
            PointD Local(PointD p)
            {
                var delta = new PointD(p.X - from.X, p.Y - from.Y);
                return new(Dot(delta, unit), delta.Y * unit.X - delta.X * unit.Y);
            }
            var localStart = Local(start); var localEnd = Local(end);
            if (RectangleInterval(localStart, localEnd, new(0, -radius, length, 2 * radius),
                out var boxLow, out var boxHigh) && boxHigh - boxLow > Epsilon &&
                InsideCapsule(Lerp(start, end, (boxLow + boxHigh) / 2), from, to, radius)) Union(boxLow, boxHigh);
        }
        low = lower; high = upper;
        return high - low > Epsilon;
    }

    private static bool CircleInterval(PointD start, PointD end, PointD center, double radius,
        out double low, out double high)
    {
        low = high = 0;
        var delta = new PointD(end.X - start.X, end.Y - start.Y);
        var relative = new PointD(start.X - center.X, start.Y - center.Y);
        var lengthSquared = Dot(delta, delta);
        var cross = relative.X * delta.Y - relative.Y * delta.X;
        var distanceSquared = cross * cross / lengthSquared;
        var remaining = radius * radius - distanceSquared;
        // Tangency has no positive length to remove.
        if (remaining <= Epsilon * Math.Max(1, radius * radius)) return false;
        var middle = -Dot(relative, delta) / lengthSquared;
        var half = Math.Sqrt(remaining / lengthSquared);
        low = Math.Max(0, middle - half); high = Math.Min(1, middle + half);
        return high - low > Epsilon;
    }

    private static bool RectangleInterval(PointD start, PointD end, RectD rectangle,
        out double low, out double high)
    {
        low = 0; high = 1;
        return Slab(start.X, end.X - start.X, rectangle.X, rectangle.Right, ref low, ref high) &&
            Slab(start.Y, end.Y - start.Y, rectangle.Y, rectangle.Bottom, ref low, ref high) && high >= low;
    }
    private static bool Slab(double start, double delta, double min, double max, ref double low, ref double high)
    {
        if (Math.Abs(delta) < Epsilon) return start >= min && start <= max;
        var first = (min - start) / delta; var second = (max - start) / delta;
        low = Math.Max(low, Math.Min(first, second)); high = Math.Min(high, Math.Max(first, second));
        return high >= low;
    }
    private static bool InsideCapsule(PointD p, PointD from, PointD to, double radius)
    {
        var delta = new PointD(to.X - from.X, to.Y - from.Y);
        var lengthSquared = Dot(delta, delta);
        var t = lengthSquared == 0 ? 0 : Math.Clamp(Dot(new(p.X - from.X, p.Y - from.Y), delta) / lengthSquared, 0, 1);
        var nearest = Lerp(from, to, t); var dx = p.X - nearest.X; var dy = p.Y - nearest.Y;
        return dx * dx + dy * dy < radius * radius - Epsilon * Math.Max(1, radius * radius);
    }
    private static void ValidateEraser(PointD from, PointD to, double radius)
    {
        if (!Finite(from) || !Finite(to)) throw new ArgumentException("Положение ластика должно быть конечным.");
        if (!double.IsFinite(radius) || radius < 0) throw new ArgumentOutOfRangeException(nameof(radius), "Радиус должен быть конечным и неотрицательным.");
    }
    private static void ValidateTransform(Affine transform)
    {
        if (!new[] { transform.M11, transform.M12, transform.M21, transform.M22, transform.OffsetX, transform.OffsetY }.All(double.IsFinite))
            throw new ArgumentException("Преобразование должно быть конечным.", nameof(transform));
        _ = transform.Inverse(new(0, 0));
    }
    private static bool Finite(PointD p) => double.IsFinite(p.X) && double.IsFinite(p.Y);
    private static double Dot(PointD first, PointD second) => first.X * second.X + first.Y * second.Y;
    private static PointD Lerp(PointD from, PointD to, double t) => new(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);
    private static bool SamePoint(PointD first, PointD second) => Math.Abs(first.X - second.X) <= Epsilon && Math.Abs(first.Y - second.Y) <= Epsilon;
}
