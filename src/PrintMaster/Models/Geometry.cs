namespace PrintMaster.Models;

public readonly record struct PointD(double X, double Y);

public readonly record struct SizeD(double Width, double Height)
{
    public SizeD Rotated(int quarterTurns) => quarterTurns % 2 == 0 ? this : new(Height, Width);
}

public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public bool Contains(PointD p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;
    public static RectD FromPoints(PointD a, PointD b) => new(Math.Min(a.X,b.X), Math.Min(a.Y,b.Y), Math.Abs(a.X-b.X), Math.Abs(a.Y-b.Y));
    public RectD Intersect(RectD b)
    {
        var x = Math.Max(X, b.X); var y = Math.Max(Y, b.Y);
        return new(x, y, Math.Max(0, Math.Min(Right,b.Right)-x), Math.Max(0,Math.Min(Bottom,b.Bottom)-y));
    }
}

public enum SourceKind { Pdf, Docx, Image, Blank }
public enum ScaleMode { Fit, Fill, Stretch }
public enum MonochromeMode { Text, Photo, Auto }
public enum EditorTool { Select, Crop, Arrow, Rectangle, Ellipse, Text, Brush, Marker, Eraser, ObjectEraser }
public enum AnnotationKind { Arrow, Rectangle, Ellipse, Text, Ink, Marker }
public enum PaperPreset { A4, A5, Notebook, Custom }

public sealed record Choice<T>(T Value, string Label);
