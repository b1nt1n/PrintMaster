using PrintMaster.Models;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Media.Imaging;

namespace PrintMaster.Services;

public sealed partial class PageRasterizer(PdfiumRenderer pdf)
{
    // Shared print/print-preview pipeline: edits -> layout -> monochrome, in that order.
    public Bitmap RenderProcessed(PageSnapshot page,int width,int height,RectD destination,MonochromeMode mode,
        int threshold,int dpi,CancellationToken cancellationToken,RectD? paperViewportMm=null)
    {
        var bitmap=RenderViewport(page,width,height,destination,true,cancellationToken,paperViewportMm);
        try
        {
            bitmap.SetResolution(dpi,dpi);
            MonochromeRasterizer.Apply(bitmap,MonochromeRasterizer.Resolve(mode,page.HasImages ? SourceKind.Image : page.Kind),threshold,cancellationToken);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    public BitmapSource RenderPrintPreview(PageSnapshot page,PrintSettingsSnapshot settings,RectD safeMm,CancellationToken cancellationToken)
    {
        const int dpi=120;
        var scale=Math.Min(dpi/25.4,1600/Math.Max(safeMm.Width,safeMm.Height));
        var width=Math.Max(1,Math.Min(1600,(int)Math.Ceiling(safeMm.Width*scale)));
        var height=Math.Max(1,Math.Min(1600,(int)Math.Ceiling(safeMm.Height*scale)));
        var source=PrintViewport.SourceArea(settings,safeMm);
        var destination=PageLayout.Place(page.ContentSize,new(0,0,width,height),settings.Scaling);
        destination=destination with { X=destination.X+(safeMm.X-source.X)/source.Width*width,
            Y=destination.Y+(safeMm.Y-source.Y)/source.Height*height };
        using var bitmap=RenderProcessed(page,width,height,destination,settings.Monochrome,settings.Threshold,dpi,cancellationToken,source);
        if(settings.PageRotationDegrees==180) bitmap.RotateFlip(RotateFlipType.Rotate180FlipNone);
        return BitmapBridge.ToWpf(bitmap);
    }

    public Bitmap Render(PageSnapshot page,int width,int height,bool forPrint,CancellationToken cancellationToken)
        => RenderViewport(page,width,height,new(0,0,width,height),forPrint,cancellationToken);

    public Bitmap RenderViewport(PageSnapshot page,int width,int height,RectD destination,bool forPrint,CancellationToken cancellationToken,RectD? paperViewportMm=null)
    {
        BitmapLimits.Validate(width,height); cancellationToken.ThrowIfCancellationRequested();
        if(page.IsComposed)
        {
            var sheet=page.GetSheet(); var size=sheet.Paper.OrientedSizeMm;
            return RenderSheetViewport(sheet,width,height,paperViewportMm ?? new RectD(0,0,size.Width,size.Height),destination,forPrint,cancellationToken);
        }
        if(page.Kind!=SourceKind.Image) return pdf.Render(page.WorkingPath,page.PageIndex,width,height,page.RotationQuarterTurns,forPrint,cancellationToken,destination);
        var output=new Bitmap(width,height,PixelFormat.Format32bppArgb);
        try
        {
            // Print/export source and destination are owned GDI images, disposed in this call.
            // Never upsample a full source into another high-DPI WPF bitmap.
            using Image source = width<=1600 && height<=1600
                ? BitmapBridge.ToGdi(BitmapBridge.LoadPreview(page.WorkingPath,Math.Max(1,(int)Math.Min(1600,1600*page.SourceWidth/page.SourceHeight))))
                : Image.FromFile(page.WorkingPath);
            using var graphics=Graphics.FromImage(output); graphics.Clear(Color.White);
            graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode=PixelOffsetMode.HighQuality; graphics.SmoothingMode=SmoothingMode.AntiAlias;
            graphics.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
            graphics.SetClip(new RectangleF((float)destination.X,(float)destination.Y,(float)destination.Width,(float)destination.Height));
            var transform=ImageGeometry.CreateTransform(page.Edits.Crop,page.RotationQuarterTurns,destination);
            using var matrix=new Matrix((float)transform.M11,(float)transform.M12,(float)transform.M21,(float)transform.M22,(float)transform.OffsetX,(float)transform.OffsetY);
            graphics.Transform=matrix;
            graphics.DrawImage(source,new RectangleF(0,0,(float)page.SourceWidth,(float)page.SourceHeight));
            foreach(var annotation in page.Edits.Annotations) { cancellationToken.ThrowIfCancellationRequested(); DrawAnnotation(graphics,annotation); }
            return output;
        }
        catch { output.Dispose(); throw; }
    }

    public BitmapSource RenderPreview(PageSnapshot page,int maxWidth,int maxHeight,CancellationToken cancellationToken)
    {
        if(page.IsComposed)
        {
            using var rendered=RenderSheet(page.GetSheet(),120,false,cancellationToken,Math.Max(maxWidth,maxHeight));
            return BitmapBridge.ToWpf(rendered);
        }
        var content=page.ContentSize; var scale=Math.Min(maxWidth/content.Width,maxHeight/content.Height);
        // Native PDF units are points. UI previews never exceed 120 DPI.
        if(page.Kind!=SourceKind.Image) scale=Math.Min(scale,120.0/72);
        var width=Math.Max(1,(int)Math.Round(content.Width*scale)); var height=Math.Max(1,(int)Math.Round(content.Height*scale));
        using var bitmap=Render(page,width,height,false,cancellationToken); return BitmapBridge.ToWpf(bitmap);
    }

    public BitmapSource LoadEditorSource(PageSnapshot page,CancellationToken cancellationToken,SheetPaper? paperOverride=null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(page.IsComposed)
        {
            var sheet=page.GetSheet(); if(paperOverride!=null) sheet=sheet with { Paper=paperOverride };
            using var rendered=RenderSheet(sheet,120,false,cancellationToken,1600); return BitmapBridge.ToWpf(rendered);
        }
        if(page.Kind==SourceKind.Image)
        {
            var maxWidth=(int)Math.Max(1,Math.Min(page.WidthMm/25.4*120,Math.Min(1600,1600*page.SourceWidth/page.SourceHeight)));
            return BitmapBridge.LoadPreview(page.WorkingPath,maxWidth);
        }
        return RenderPreview(page,1600,1600,cancellationToken);
    }

    public static void DrawAnnotation(Graphics g,Annotation a)
    {
        var color=ColorTranslator.FromHtml(a.Color);
        if(a.Kind!=AnnotationKind.Text) color=Color.FromArgb((int)Math.Round(255*a.EffectiveOpacity),color);
        using var pen=new Pen(color,(float)a.Thickness) { StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round };
        using var fill=a.Kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse && a.FillColor is {} fillColor
            ? new SolidBrush(Color.FromArgb((int)Math.Round(255*a.EffectiveOpacity),ColorTranslator.FromHtml(fillColor))) : null;
        var start=new PointF((float)a.Start.X,(float)a.Start.Y); var end=new PointF((float)a.End.X,(float)a.End.Y);
        var rect=RectD.FromPoints(a.Start,a.End);
        switch(a.Kind)
        {
            case AnnotationKind.Arrow:
                g.DrawLine(pen,start,end);
                var angle=Math.Atan2(a.End.Y-a.Start.Y,a.End.X-a.Start.X); var length=Math.Max(14,a.Thickness*4);
                g.DrawLine(pen,end,new((float)(end.X-length*Math.Cos(angle-0.5)),(float)(end.Y-length*Math.Sin(angle-0.5))));
                g.DrawLine(pen,end,new((float)(end.X-length*Math.Cos(angle+0.5)),(float)(end.Y-length*Math.Sin(angle+0.5)))); break;
            case AnnotationKind.Rectangle:
                if(fill!=null) g.FillRectangle(fill,(float)rect.X,(float)rect.Y,(float)rect.Width,(float)rect.Height);
                g.DrawRectangle(pen,(float)rect.X,(float)rect.Y,(float)rect.Width,(float)rect.Height); break;
            case AnnotationKind.Ellipse:
                if(fill!=null) g.FillEllipse(fill,(float)rect.X,(float)rect.Y,(float)rect.Width,(float)rect.Height);
                g.DrawEllipse(pen,(float)rect.X,(float)rect.Y,(float)rect.Width,(float)rect.Height); break;
            case AnnotationKind.Text:
                if(a.TextFormat!=null) { DrawFormattedAnnotation(g,a); break; }
                using(var brush=new SolidBrush(color))
                using(var font=new Font("Segoe UI",(float)a.FontSize,FontStyle.Regular,GraphicsUnit.Pixel))
                using(var format=(StringFormat)StringFormat.GenericTypographic.Clone())
                { format.FormatFlags|=StringFormatFlags.MeasureTrailingSpaces; g.DrawString(a.Text,font,brush,start,format); } break;
            case AnnotationKind.Ink:
            case AnnotationKind.Marker:
                if(a.Points.Count>1) g.DrawLines(pen,a.Points.Select(p=>new PointF((float)p.X,(float)p.Y)).ToArray());
                else if(a.Points.Count==1){using var dotBrush=new SolidBrush(color);g.FillEllipse(dotBrush,(float)(a.Points[0].X-a.Thickness/2),(float)(a.Points[0].Y-a.Thickness/2),(float)a.Thickness,(float)a.Thickness);} break;
        }
    }
    private static void DrawFormattedAnnotation(Graphics g,Annotation a)
    {
        var logical=AnnotationTextLayout.LogicalText(a); var layout=TextLayoutService.Layout(logical);
        var layoutScale=AnnotationTextLayout.LayoutScale(a);
        using var font=TextLayoutService.CreateFont(logical.Style,out _,25.4/72/layoutScale);
        using var brush=new SolidBrush(ColorTranslator.FromHtml(a.Color)); using var format=TextLayoutService.CreateFormat();
        var state=g.Save();
        try
        {
            g.SetClip(new RectangleF((float)a.Start.X,(float)a.Start.Y,(float)Math.Max(.01,a.End.X-a.Start.X),(float)Math.Max(.01,a.End.Y-a.Start.Y)),CombineMode.Intersect);
            foreach(var (line,index) in layout.Lines.Select((line,index)=>(line,index)))
            {
                var width=line.WidthMm/layoutScale;
                var x=a.Start.X+(logical.Style.Alignment switch { Models.TextAlignment.Center=>(a.End.X-a.Start.X-width)/2,Models.TextAlignment.Right=>a.End.X-a.Start.X-width,_=>0 });
                g.DrawString(line.DisplayText,font,brush,new PointF((float)x,(float)(a.Start.Y+index*layout.LineHeightMm/layoutScale)),format);
            }
        }
        finally { g.Restore(state); }
    }
}
