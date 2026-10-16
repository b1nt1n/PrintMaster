using PrintMaster.Models;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfPoint=System.Windows.Point;
using WpfMatrix=System.Windows.Media.Matrix;
using WpfBrush=System.Windows.Media.Brush;
using PixelFormat=System.Drawing.Imaging.PixelFormat;

namespace PrintMaster.Services;

// Graphics must be in 96-DPI document units. Frozen WPF geometry is read safely from an MTA print worker.
public static class MarkdownDrawingRenderer
{
    public static Bitmap Render(MarkdownRenderPage page,int dpi,CancellationToken token,int? maxPixelsAcross=1600)
    {
        token.ThrowIfCancellationRequested();if(dpi<1 || dpi>2400)throw new ArgumentOutOfRangeException(nameof(dpi));
        var scale=dpi/25.4;
        if(maxPixelsAcross is {} cap) {if(cap<1)throw new ArgumentOutOfRangeException(nameof(maxPixelsAcross));scale=Math.Min(scale,cap/Math.Max(page.PaperMm.Width,page.PaperMm.Height));}
        var width=Math.Max(1,(int)Math.Ceiling(page.PaperMm.Width*scale));var height=Math.Max(1,(int)Math.Ceiling(page.PaperMm.Height*scale));
        if(maxPixelsAcross is {} pixelCap){width=Math.Min(pixelCap,width);height=Math.Min(pixelCap,height);}
        BitmapLimits.Validate(width,height);var bitmap=new Bitmap(width,height,PixelFormat.Format32bppArgb);
        try
        {
            bitmap.SetResolution(dpi,dpi);using var graphics=Graphics.FromImage(bitmap);graphics.Clear(System.Drawing.Color.White);
            graphics.SmoothingMode=SmoothingMode.AntiAlias;graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;graphics.PixelOffsetMode=PixelOffsetMode.HighQuality;
            graphics.ScaleTransform((float)(scale/MarkdownLayoutService.DipPerMm),(float)(scale/MarkdownLayoutService.DipPerMm));
            Draw(graphics,page,token);return bitmap;
        }
        catch {bitmap.Dispose();throw;}
    }
    public static void Draw(Graphics graphics,MarkdownRenderPage page,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(graphics);ArgumentNullException.ThrowIfNull(page);
        if(!page.Drawing.IsFrozen)throw new InvalidOperationException("Страница Markdown не подготовлена для фоновой печати.");
        token.ThrowIfCancellationRequested();Paint(graphics,page.Drawing,page.ImageFiles,1,token);
    }
    private static void Paint(Graphics graphics,Drawing drawing,IReadOnlyDictionary<ImageSource,string> images,double opacity,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        switch(drawing)
        {
            case DrawingGroup group:
                if(group.OpacityMask!=null)throw new InvalidOperationException("Маски прозрачности в Markdown не поддерживаются.");
                var saved=graphics.Save();
                try
                {
                    var matrix=group.Transform?.Value ?? WpfMatrix.Identity;
                    using(var transform=Matrix(matrix)) graphics.MultiplyTransform(transform,MatrixOrder.Prepend);
                    if(group.ClipGeometry is {} clip) {using var path=Path(clip,graphics,token);graphics.SetClip(path,CombineMode.Intersect);}
                    foreach(var child in group.Children)Paint(graphics,child,images,opacity*group.Opacity,token);
                }
                finally {graphics.Restore(saved);}
                break;
            case GeometryDrawing geometry:
                if(geometry.Brush is {} fill)
                {
                    var color=Color(fill,opacity);if(color.A>0) {using var path=Path(geometry.Geometry,graphics,token,true);using var brush=new SolidBrush(color);graphics.FillPath(brush,path);}
                }
                if(geometry.Pen is {} stroke)
                {
                    var color=Color(stroke.Brush,opacity);if(color.A==0 || stroke.Thickness<=0)break;
                    using var path=Path(geometry.Geometry,graphics,token);using var pen=new System.Drawing.Pen(color,(float)stroke.Thickness)
                    {StartCap=Cap(stroke.StartLineCap),EndCap=Cap(stroke.EndLineCap),LineJoin=stroke.LineJoin switch {PenLineJoin.Round=>LineJoin.Round,PenLineJoin.Bevel=>LineJoin.Bevel,_=>LineJoin.Miter},MiterLimit=(float)Math.Max(1,stroke.MiterLimit)};
                    if(stroke.DashStyle.Dashes.Count>0) {pen.DashPattern=stroke.DashStyle.Dashes.Select(d=>(float)Math.Max(.001,d)).ToArray();pen.DashOffset=(float)stroke.DashStyle.Offset;}
                    graphics.DrawPath(pen,path);
                }
                break;
            case ImageDrawing image:
                if(!images.TryGetValue(image.ImageSource,out var file) || !File.Exists(file))throw new IOException("Исходный файл картинки Markdown не найден.");
                using(var original=System.Drawing.Image.FromFile(file))
                {
                    token.ThrowIfCancellationRequested();var destination=new RectangleF((float)image.Rect.X,(float)image.Rect.Y,(float)image.Rect.Width,(float)image.Rect.Height);
                    if(opacity>=.99999)graphics.DrawImage(original,destination,new RectangleF(0,0,original.Width,original.Height),GraphicsUnit.Pixel);
                    else
                    {
                        using var attributes=new ImageAttributes();attributes.SetColorMatrix(new ColorMatrix {Matrix33=(float)opacity});
                        graphics.DrawImage(original,[new PointF(destination.Left,destination.Top),new PointF(destination.Right,destination.Top),new PointF(destination.Left,destination.Bottom)],new RectangleF(0,0,original.Width,original.Height),GraphicsUnit.Pixel,attributes);
                    }
                }
                break;
            case GlyphRunDrawing:throw new InvalidOperationException("Страница содержит незапеченный векторный текст.");
            default:throw new InvalidOperationException("Не поддерживается рисунок Markdown: "+drawing.GetType().Name);
        }
    }
    private static System.Drawing.Color Color(WpfBrush brush,double opacity)
    {
        if(brush is not SolidColorBrush solid)throw new InvalidOperationException("Градиенты Markdown не поддерживаются.");
        var c=solid.Color;return System.Drawing.Color.FromArgb((int)Math.Clamp(Math.Round(c.A*solid.Opacity*opacity),0,255),c.R,c.G,c.B);
    }
    private static LineCap Cap(PenLineCap cap)=>cap switch {PenLineCap.Round=>LineCap.Round,PenLineCap.Square=>LineCap.Square,PenLineCap.Triangle=>LineCap.Triangle,_=>LineCap.Flat};
    private static System.Drawing.Drawing2D.Matrix Matrix(WpfMatrix value)=>new((float)value.M11,(float)value.M12,(float)value.M21,(float)value.M22,(float)value.OffsetX,(float)value.OffsetY);
    private static GraphicsPath Path(Geometry geometry,Graphics graphics,CancellationToken token,bool filledOnly=false)
    {
        var source=PathGeometry.CreateFromGeometry(geometry);
        var path=new GraphicsPath(source.FillRule==FillRule.EvenOdd ? FillMode.Alternate : FillMode.Winding);
        static PointF Point(WpfPoint p)=>new((float)p.X,(float)p.Y);
        try
        {
            foreach(var figure in source.Figures)
            {
                token.ThrowIfCancellationRequested();if(filledOnly && !figure.IsFilled)continue;path.StartFigure();var previous=figure.StartPoint;
                foreach(var segment in figure.Segments)
                {
                    token.ThrowIfCancellationRequested();
                    switch(segment)
                    {
                        case LineSegment line:path.AddLine(Point(previous),Point(line.Point));previous=line.Point;break;
                        case PolyLineSegment lines:
                            foreach(var p in lines.Points){path.AddLine(Point(previous),Point(p));previous=p;}break;
                        case BezierSegment curve:path.AddBezier(Point(previous),Point(curve.Point1),Point(curve.Point2),Point(curve.Point3));previous=curve.Point3;break;
                        case PolyBezierSegment curves:
                            for(var i=0;i<curves.Points.Count;i+=3){path.AddBezier(Point(previous),Point(curves.Points[i]),Point(curves.Points[i+1]),Point(curves.Points[i+2]));previous=curves.Points[i+2];}break;
                        case QuadraticBezierSegment quadratic:
                            Quadratic(path,previous,quadratic.Point1,quadratic.Point2);previous=quadratic.Point2;break;
                        case PolyQuadraticBezierSegment quadratics:
                            for(var i=0;i<quadratics.Points.Count;i+=2){Quadratic(path,previous,quadratics.Points[i],quadratics.Points[i+1]);previous=quadratics.Points[i+1];}break;
                        case ArcSegment arc:
                            // Arc approximation uses an output-pixel tolerance; glyph Bezier curves remain exact.
                            var piece=new PathGeometry([new PathFigure(previous,[arc.CloneCurrentValue()],false)]);
                            var m=graphics.Transform.Elements;var magnification=Math.Max(Math.Sqrt(m[0]*m[0]+m[1]*m[1]),Math.Sqrt(m[2]*m[2]+m[3]*m[3]));
                            var flat=piece.GetFlattenedPathGeometry(.025/Math.Max(.001,magnification),ToleranceType.Absolute);
                            foreach(var f in flat.Figures)foreach(var s in f.Segments)
                            {
                                if(s is PolyLineSegment poly)foreach(var p in poly.Points){path.AddLine(Point(previous),Point(p));previous=p;}
                                else if(s is LineSegment line){path.AddLine(Point(previous),Point(line.Point));previous=line.Point;}
                            }
                            previous=arc.Point;break;
                        default:throw new InvalidOperationException("Не поддерживается контур Markdown: "+segment.GetType().Name);
                    }
                }
                if(figure.IsClosed)path.CloseFigure();
            }
            using var matrix=Matrix(source.Transform?.Value ?? WpfMatrix.Identity);path.Transform(matrix);return path;
        }
        catch {path.Dispose();throw;}
    }
    private static void Quadratic(GraphicsPath path,WpfPoint from,WpfPoint control,WpfPoint to)
    {
        var a=new PointF((float)(from.X+2d/3*(control.X-from.X)),(float)(from.Y+2d/3*(control.Y-from.Y)));
        var b=new PointF((float)(to.X+2d/3*(control.X-to.X)),(float)(to.Y+2d/3*(control.Y-to.Y)));
        path.AddBezier(new((float)from.X,(float)from.Y),a,b,new((float)to.X,(float)to.Y));
    }
}
