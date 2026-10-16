using PrintMaster.Models;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace PrintMaster.Services;

public sealed partial class PageRasterizer
{
    public Bitmap RenderSheet(SheetSnapshot sheet,int dpi,bool forPrint,CancellationToken cancellationToken,int? maxPixelsAcross=null,bool transparentBackground=false)
    {
        var paper=sheet.Paper.OrientedSizeMm; var scale=dpi/25.4;
        if(maxPixelsAcross is int cap){if(cap<1)throw new ArgumentOutOfRangeException(nameof(maxPixelsAcross));scale=Math.Min(scale,cap/Math.Max(paper.Width,paper.Height));}
        var width=Math.Max(1,(int)Math.Ceiling(paper.Width*scale)); var height=Math.Max(1,(int)Math.Ceiling(paper.Height*scale));
        if(maxPixelsAcross is int maximum){width=Math.Min(width,maximum);height=Math.Min(height,maximum);}
        var safe=sheet.Paper.SafeAreaMm; var safePx=new RectD(safe.X*scale,safe.Y*scale,safe.Width*scale,safe.Height*scale);
        var destination=sheet.PrimaryContent!=null ? PageLayout.Place(sheet.ContentSize,safePx,ScaleMode.Fit) : safePx;
        var bitmap=RenderSheetViewport(sheet,width,height,new(0,0,paper.Width,paper.Height),destination,forPrint,cancellationToken,transparentBackground);
        bitmap.SetResolution(dpi,dpi); return bitmap;
    }

    private Bitmap RenderSheetViewport(SheetSnapshot sheet,int width,int height,RectD viewportMm,RectD primaryDestination,
        bool forPrint,CancellationToken cancellationToken,bool transparentBackground=false)
    {
        BitmapLimits.Validate(width,height);
        if(viewportMm.IsEmpty) throw new ArgumentException("Пустая область листа.");
        var bitmap=new Bitmap(width,height,PixelFormat.Format32bppArgb);
        try
        {
            using var graphics=Graphics.FromImage(bitmap); graphics.Clear(transparentBackground ? Color.Transparent : Color.White);
            graphics.SmoothingMode=SmoothingMode.AntiAlias; graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode=PixelOffsetMode.HighQuality; graphics.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
            var sx=width/viewportMm.Width; var sy=height/viewportMm.Height;
            using var paperMatrix=new Matrix((float)sx,0,0,(float)sy,(float)(-viewportMm.X*sx),(float)(-viewportMm.Y*sy));
            graphics.Transform=paperMatrix;
            foreach(var item in sheet.Objects)
            {
                cancellationToken.ThrowIfCancellationRequested(); if(!item.Visible) continue;
                var bounds=item.Placement.BoundsMm;
                if(item is ContentObject { Placement.Mode:PlacementMode.Automatic })
                    bounds=new(viewportMm.X+primaryDestination.X/sx,viewportMm.Y+primaryDestination.Y/sy,primaryDestination.Width/sx,primaryDestination.Height/sy);
                var state=graphics.Save();
                try
                {
                    var angle=item.Placement.RotationDegrees;
                    if(angle!=0)
                    {
                        var cx=(float)(bounds.X+bounds.Width/2); var cy=(float)(bounds.Y+bounds.Height/2);
                        graphics.TranslateTransform(cx,cy); graphics.RotateTransform((float)angle); graphics.TranslateTransform(-cx,-cy);
                    }
                    switch(item)
                    {
                        case ContentObject content: DrawContent(graphics,sheet,content,bounds,sx,sy,forPrint,cancellationToken); break;
                        case TextObject text: DrawTextObject(graphics,text,bounds,cancellationToken); break;
                        case ShapeObject shape: DrawShapeObject(graphics,shape,bounds); break;
                        case InkObject ink: DrawInkObject(graphics,ink,bounds); break;
                        case MarkdownObject markdown: DrawMarkdownObject(graphics,sheet,markdown,bounds,cancellationToken);break;
                    }
                }
                finally { graphics.Restore(state); }
            }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private void DrawContent(Graphics g,SheetSnapshot sheet,ContentObject content,RectD bounds,double sx,double sy,
        bool forPrint,CancellationToken cancellationToken)
    {
        var asset=sheet.Assets.First(a=>a.Id==content.AssetId);
        if(string.IsNullOrEmpty(asset.WorkingPath)) throw new InvalidOperationException("У исходника отсутствует рабочий файл.");
        g.SetClip(ToDrawingRect(bounds),CombineMode.Intersect);
        if(asset.Kind!=SourceKind.Image)
        {
            var width=Math.Max(1,(int)Math.Ceiling(bounds.Width*sx)); var height=Math.Max(1,(int)Math.Ceiling(bounds.Height*sy));
            using var rendered=pdf.Render(asset.WorkingPath,asset.PageIndex,width,height,content.Edits.QuarterTurns,forPrint,cancellationToken);
            g.DrawImage(rendered,ToDrawingRect(bounds),new RectangleF(0,0,width,height),GraphicsUnit.Pixel); return;
        }
        using Image image=Math.Abs(bounds.Width*sx)<=1600 && Math.Abs(bounds.Height*sy)<=1600
            ? BitmapBridge.ToGdi(BitmapBridge.LoadPreview(asset.WorkingPath,Math.Max(1,(int)Math.Min(1600,1600*asset.LogicalSize.Width/asset.LogicalSize.Height))))
            : Image.FromFile(asset.WorkingPath);
        var affine=ImageGeometry.CreateTransform(content.Edits.Crop,content.Edits.QuarterTurns,bounds);
        using var sourceMatrix=new Matrix((float)affine.M11,(float)affine.M12,(float)affine.M21,(float)affine.M22,(float)affine.OffsetX,(float)affine.OffsetY);
        g.MultiplyTransform(sourceMatrix,MatrixOrder.Prepend);
        g.DrawImage(image,new RectangleF(0,0,(float)asset.LogicalSize.Width,(float)asset.LogicalSize.Height));
        foreach(var annotation in content.Edits.Annotations) { cancellationToken.ThrowIfCancellationRequested(); DrawAnnotation(g,annotation); }
    }
    private static void DrawTextObject(Graphics g,TextObject item,RectD bounds,CancellationToken token)
    {
        var style=item.Style; var layout=TextLayoutService.Layout(item);
        g.TextRenderingHint=TextRenderingHint.AntiAlias;
        using var font=TextLayoutService.CreateFont(style,out _,25.4/72);
        using var brush=new SolidBrush(ColorTranslator.FromHtml(style.Color));
        using var format=TextLayoutService.CreateFormat();
        g.SetClip(ToDrawingRect(bounds),CombineMode.Intersect);
        var visible=g.VisibleClipBounds;
        for(var line=0;line<layout.Lines.Length;line++)
        {
            token.ThrowIfCancellationRequested();
            var y=bounds.Y+line*layout.LineHeightMm;
            if(y+layout.LineHeightMm<visible.Top) continue;
            if(y>visible.Bottom) break;
            var value=layout.Lines[line]; var x=bounds.X+(style.Alignment switch { Models.TextAlignment.Center=>(bounds.Width-value.WidthMm)/2,Models.TextAlignment.Right=>bounds.Width-value.WidthMm,_=>0 });
            g.DrawString(value.DisplayText,font,brush,new PointF((float)x,(float)y),format);
        }
    }
    private static Color StrokeColor(StrokeStyle style)=>Color.FromArgb((int)Math.Round(255*style.Opacity),ColorTranslator.FromHtml(style.Color));
    private static void DrawShapeObject(Graphics g,ShapeObject item,RectD bounds)
    {
        using var pen=new Pen(StrokeColor(item.Style),(float)item.Style.WidthMm) { StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round };
        using var fill=item.Style.FillColor!=null ? new SolidBrush(Color.FromArgb((int)Math.Round(255*item.Style.Opacity),ColorTranslator.FromHtml(item.Style.FillColor))) : null;
        var rect=ToDrawingRect(bounds);
        switch(item.Shape)
        {
            case SheetShapeKind.Rectangle:
                if(fill!=null) g.FillRectangle(fill,rect); g.DrawRectangle(pen,rect.X,rect.Y,rect.Width,rect.Height); break;
            case SheetShapeKind.Ellipse:
                if(fill!=null) g.FillEllipse(fill,rect); g.DrawEllipse(pen,rect); break;
            case SheetShapeKind.Arrow:
                var start=new PointF(rect.X,rect.Y); var end=new PointF(rect.Right,rect.Bottom); g.DrawLine(pen,start,end);
                var angle=Math.Atan2(end.Y-start.Y,end.X-start.X); var length=Math.Max(3,item.Style.WidthMm*4);
                g.DrawLine(pen,end,new((float)(end.X-length*Math.Cos(angle-0.5)),(float)(end.Y-length*Math.Sin(angle-0.5))));
                g.DrawLine(pen,end,new((float)(end.X-length*Math.Cos(angle+0.5)),(float)(end.Y-length*Math.Sin(angle+0.5)))); break;
        }
    }
    private static void DrawInkObject(Graphics g,InkObject item,RectD bounds)
    {
        using var pen=new Pen(StrokeColor(item.Style),(float)item.Style.WidthMm) { StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round };
        var points=item.Points.Select(p=>new PointF((float)(bounds.X+p.X*bounds.Width),(float)(bounds.Y+p.Y*bounds.Height))).ToArray();
        if(points.Length>1) g.DrawLines(pen,points); else { using var brush=new SolidBrush(pen.Color); g.FillEllipse(brush,points[0].X-pen.Width/2,points[0].Y-pen.Width/2,pen.Width,pen.Width); }
    }
    private static RectangleF ToDrawingRect(RectD bounds)=>new((float)bounds.X,(float)bounds.Y,(float)bounds.Width,(float)bounds.Height);
}
