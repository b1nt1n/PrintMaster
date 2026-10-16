using PrintMaster.Models;

namespace PrintMaster.Services;

public enum CropHandle
{
    None, Create, Move, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left
}

/// <summary>
/// Crop-frame geometry in oriented image coordinates. The caller performs source rotation
/// and view transforms. All results stay inside positive, finite bounds; invalid bounds
/// return an empty rectangle, as do ratios whose dimensions cannot be represented inside
/// those bounds. A positive finite aspect ratio means width / height.
/// Corner resizing keeps the opposite corner fixed and projects the pointer onto the
/// ratio direction; side resizing keeps the opposite edge and perpendicular centre fixed.
/// At a boundary, the whole proportional frame stops growing. The minimum size is relaxed
/// only when the available space cannot contain it, and a locked ratio is never relaxed.
/// None and Create normalize the rectangle only; creation from two pointer positions belongs
/// to the caller. Nonfinite deltas are ignored, and invalid ratios mean free proportions.
/// </summary>
public static class CropSelectionGeometry
{
    public static RectD Move(RectD rect, double dx, double dy, RectD bounds)
    {
        if (!ValidBounds(bounds)) return default;
        rect = Normalize(rect, bounds, double.Epsilon);
        return rect with
        {
            X = Clamp(rect.X + FiniteDelta(dx), bounds.X, bounds.Right - rect.Width),
            Y = Clamp(rect.Y + FiniteDelta(dy), bounds.Y, bounds.Bottom - rect.Height)
        };
    }

    public static RectD Resize(RectD rect, CropHandle handle, double dx, double dy,
        RectD bounds, double aspectRatio = 0, double minSize = 1)
    {
        if (!ValidBounds(bounds)) return default;
        if (handle == CropHandle.Move) return Move(rect, dx, dy, bounds);
        rect = Normalize(rect, bounds, minSize);
        if (handle is CropHandle.None or CropHandle.Create) return rect;

        dx = FiniteDelta(dx);
        dy = FiniteDelta(dy);
        var left = handle is CropHandle.TopLeft or CropHandle.Left or CropHandle.BottomLeft;
        var right = handle is CropHandle.TopRight or CropHandle.Right or CropHandle.BottomRight;
        var top = handle is CropHandle.TopLeft or CropHandle.Top or CropHandle.TopRight;
        var bottom = handle is CropHandle.BottomLeft or CropHandle.Bottom or CropHandle.BottomRight;
        if (!(left || right || top || bottom)) return rect;

        if (!ValidRatio(aspectRatio))
        {
            var minimum = ValidMinimum(minSize);
            var minWidth = Math.Min(minimum, bounds.Width);
            var minHeight = Math.Min(minimum, bounds.Height);
            var x = left ? Clamp(rect.X + dx, bounds.X, rect.Right - minWidth) : rect.X;
            var y = top ? Clamp(rect.Y + dy, bounds.Y, rect.Bottom - minHeight) : rect.Y;
            var endX = right ? Clamp(rect.Right + dx, rect.X + minWidth, bounds.Right) : rect.Right;
            var endY = bottom ? Clamp(rect.Bottom + dy, rect.Y + minHeight, bounds.Bottom) : rect.Bottom;
            return new(x, y, endX - x, endY - y);
        }

        var (unitWidth, unitHeight) = RatioUnits(aspectRatio);
        var horizontal = left || right;
        var vertical = top || bottom;
        var anchorX = left ? rect.Right : rect.X;
        var anchorY = top ? rect.Bottom : rect.Y;
        var centreX = rect.X + rect.Width / 2;
        var centreY = rect.Y + rect.Height / 2;
        var maxWidth = horizontal
            ? (left ? anchorX - bounds.X : bounds.Right - anchorX)
            : 2 * Math.Min(centreX - bounds.X, bounds.Right - centreX);
        var maxHeight = vertical
            ? (top ? anchorY - bounds.Y : bounds.Bottom - anchorY)
            : 2 * Math.Min(centreY - bounds.Y, bounds.Bottom - centreY);
        var desiredWidth = rect.Width + (left ? -dx : right ? dx : 0);
        var desiredHeight = rect.Height + (top ? -dy : bottom ? dy : 0);
        double desiredScale;
        if (horizontal && vertical)
        {
            // Orthogonal projection onto the constrained corner's direction avoids
            // discontinuities when a drag switches between its horizontal and vertical axes.
            var denominator = unitWidth * unitWidth + unitHeight * unitHeight;
            desiredScale = desiredWidth * (unitWidth / denominator)
                + desiredHeight * (unitHeight / denominator);
        }
        else desiredScale = horizontal ? desiredWidth / unitWidth : desiredHeight / unitHeight;

        var scale = BoundedScale(desiredScale, maxWidth, maxHeight, unitWidth, unitHeight, minSize);
        var width = scale * unitWidth;
        var height = scale * unitHeight;
        return ProportionalRect(horizontal ? (left ? anchorX - width : anchorX) : centreX - width / 2,
            vertical ? (top ? anchorY - height : anchorY) : centreY - height / 2, width, height);
    }

    /// <summary>Fits the largest proportional frame inside the current rectangle, retaining its centre.</summary>
    public static RectD FitRatio(RectD rect, double aspectRatio, RectD bounds, double minSize = 1)
    {
        if (!ValidBounds(bounds)) return default;
        rect = Normalize(rect, bounds, minSize);
        if (!ValidRatio(aspectRatio)) return rect;
        var (unitWidth, unitHeight) = RatioUnits(aspectRatio);
        var scale = BoundedScale(double.PositiveInfinity, rect.Width, rect.Height,
            unitWidth, unitHeight, minSize);
        var width = scale * unitWidth;
        var height = scale * unitHeight;
        return ProportionalRect(rect.X + (rect.Width - width) / 2,
            rect.Y + (rect.Height - height) / 2, width, height);
    }

    /// <summary>
    /// Applies numeric dimensions from the top-left corner. With a ratio, widthPriority
    /// selects which dimension is authoritative; the other is computed. A request that
    /// exceeds the image is capped without moving the anchor or breaking the ratio.
    /// Nonfinite dimensions retain the corresponding current dimension.
    /// </summary>
    public static RectD ResizeTo(RectD rect, double width, double height, RectD bounds,
        double aspectRatio = 0, bool widthPriority = true, double minSize = 1)
    {
        if (!ValidBounds(bounds)) return default;
        rect = Normalize(rect, bounds, minSize);
        if (!double.IsFinite(width)) width = rect.Width;
        if (!double.IsFinite(height)) height = rect.Height;
        var maxWidth = bounds.Right - rect.X;
        var maxHeight = bounds.Bottom - rect.Y;
        if (!ValidRatio(aspectRatio))
        {
            var minimum = ValidMinimum(minSize);
            return rect with
            {
                Width = Clamp(width, Math.Min(minimum, maxWidth), maxWidth),
                Height = Clamp(height, Math.Min(minimum, maxHeight), maxHeight)
            };
        }
        var (unitWidth, unitHeight) = RatioUnits(aspectRatio);
        var desiredScale = widthPriority ? width / unitWidth : height / unitHeight;
        var scale = BoundedScale(desiredScale, maxWidth, maxHeight, unitWidth, unitHeight, minSize);
        return ProportionalRect(rect.X, rect.Y, scale * unitWidth, scale * unitHeight);
    }

    private static RectD Normalize(RectD rect, RectD bounds, double minSize)
    {
        var minimum = ValidMinimum(minSize);
        var width = Clamp(double.IsFinite(rect.Width) ? rect.Width : bounds.Width,
            Math.Min(minimum, bounds.Width), bounds.Width);
        var height = Clamp(double.IsFinite(rect.Height) ? rect.Height : bounds.Height,
            Math.Min(minimum, bounds.Height), bounds.Height);
        var x = Clamp(double.IsFinite(rect.X) ? rect.X : bounds.X, bounds.X, bounds.Right - width);
        var y = Clamp(double.IsFinite(rect.Y) ? rect.Y : bounds.Y, bounds.Y, bounds.Bottom - height);
        return new(x, y, width, height);
    }

    private static double BoundedScale(double desired, double maxWidth, double maxHeight,
        double unitWidth, double unitHeight, double minSize)
    {
        var maximum = Math.Min(maxWidth / unitWidth, maxHeight / unitHeight);
        var minimum = Math.Min(maximum,
            Math.Max(ValidMinimum(minSize) / unitWidth, ValidMinimum(minSize) / unitHeight));
        return Clamp(desired, minimum, maximum);
    }

    private static (double Width, double Height) RatioUnits(double ratio) =>
        ratio >= 1 ? (1, 1 / ratio) : (ratio, 1);
    private static RectD ProportionalRect(double x, double y, double width, double height) =>
        width > 0 && height > 0 ? new(x, y, width, height) : default;
    private static double FiniteDelta(double value) => double.IsFinite(value) ? value : 0;
    private static double ValidMinimum(double value) => double.IsFinite(value) && value > 0 ? value : 1;
    private static bool ValidRatio(double value) => double.IsFinite(value) && value > 0;
    private static bool ValidBounds(RectD bounds) =>
        double.IsFinite(bounds.X) && double.IsFinite(bounds.Y)
        && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height)
        && double.IsFinite(bounds.Right) && double.IsFinite(bounds.Bottom)
        && bounds.Width > 0 && bounds.Height > 0;
    private static double Clamp(double value, double minimum, double maximum) =>
        double.IsNaN(value) ? minimum : Math.Max(minimum, Math.Min(maximum, value));
}
