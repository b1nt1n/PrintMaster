using PrintMaster.Models;
using System.Drawing;

namespace PrintMaster.Services;

public static class SheetEditing
{
    public const double DipPerMm=96/25.4;
    public static bool SamePaper(SheetPaper paper,SizeD size)=>Math.Abs(paper.OrientedSizeMm.Width-size.Width)<0.3 && Math.Abs(paper.OrientedSizeMm.Height-size.Height)<0.3;

    public static RectD VisualBounds(SheetObject item)
    {
        var b=item.Placement.BoundsMm; var angle=item.Placement.RotationDegrees*Math.PI/180;
        if(item is ShapeObject { Shape:SheetShapeKind.Arrow } arrow)
        {
            var lineAngle=Math.Atan2(b.Height,b.Width); var length=Math.Max(3,arrow.Style.WidthMm*4);
            var points=new[] { new PointD(b.X,b.Y),new(b.Right,b.Bottom),
                new(b.Right-length*Math.Cos(lineAngle-0.5),b.Bottom-length*Math.Sin(lineAngle-0.5)),
                new(b.Right-length*Math.Cos(lineAngle+0.5),b.Bottom-length*Math.Sin(lineAngle+0.5)) };
            var cx=b.X+b.Width/2; var cy=b.Y+b.Height/2;
            var rotated=points.Select(p=>new PointD(cx+(p.X-cx)*Math.Cos(angle)-(p.Y-cy)*Math.Sin(angle),cy+(p.X-cx)*Math.Sin(angle)+(p.Y-cy)*Math.Cos(angle))).ToArray();
            var padding=arrow.Style.WidthMm/2;
            return new(rotated.Min(p=>p.X)-padding,rotated.Min(p=>p.Y)-padding,rotated.Max(p=>p.X)-rotated.Min(p=>p.X)+2*padding,rotated.Max(p=>p.Y)-rotated.Min(p=>p.Y)+2*padding);
        }
        var width=Math.Abs(b.Width*Math.Cos(angle))+Math.Abs(b.Height*Math.Sin(angle));
        var height=Math.Abs(b.Width*Math.Sin(angle))+Math.Abs(b.Height*Math.Cos(angle));
        var stroke=item switch { ShapeObject s=>s.Style.WidthMm/2,InkObject i=>i.Style.WidthMm/2,_=>0 };
        return new(b.X+b.Width/2-width/2-stroke,b.Y+b.Height/2-height/2-stroke,width+2*stroke,height+2*stroke);
    }
    public static bool Outside(SheetObject item,RectD safe,SheetPaper? paper=null)
    {
        if(!item.Visible || item is TextObject { Flow.Notebook:true,Text.Length:0 }) return false;
        if(paper!=null && item is MarkdownObject { Document.Style.Margins:{} margins } markdown)
            return MarkdownOutside(markdown,paper,margins,safe);
        var b=VisualBounds(item);
        return b.X<safe.X-0.01 || b.Y<safe.Y-0.01 || b.Right>safe.Right+0.01 || b.Bottom>safe.Bottom+0.01;
    }
    public static bool HasClipping(PageSnapshot page,RectD safe)
    {
        if(!page.IsComposed) return false;
        var sheet=page.GetSheet();
        return sheet.Objects.Any(o=>o.Visible && o.Placement.Mode==PlacementMode.Fixed && (Outside(o,safe,sheet.Paper) || o is TextObject text && TextLayoutService.Overflows(text)));
    }
    private static bool MarkdownOutside(MarkdownObject item,SheetPaper paper,SheetMargins margins,RectD safe)
    {
        // The object's bounds are a placement reference, not ink: default Markdown
        // has a 5 mm reference frame while its text starts at 10 mm. Match the
        // physical layout area and the DrawMarkdownObject transform without
        // requesting layout or blocking the UI on the Markdown STA worker.
        var size=paper.OrientedSizeMm; var reference=paper.SafeAreaMm;
        var area=new RectD(margins.LeftMm,margins.TopMm,size.Width-margins.LeftMm-margins.RightMm,
            size.Height-margins.TopMm-margins.BottomMm).Intersect(reference);
        if(area.IsEmpty || reference.IsEmpty) return Outside(item,safe);
        IEnumerable<PointD> points;
        if(paper.Notebook!=null)
        {
            try
            {
                var columns=NotebookGeometry.TextRegions(paper,area).Select(region=>region.NativeBounds).ToArray();
                // FlowDocument uses equal widths on a spread, preserving the
                // original left edges before the notebook's optional 90° rotation.
                var width=columns.Min(column=>column.Width);
                points=columns.SelectMany(column=>Corners(column with {Width=width}))
                    .Select(point=>NotebookGeometry.ToPaper(paper,point)).ToArray();
            }
            catch(Exception error) when(error is ArgumentException or InvalidOperationException) { return Outside(item,safe); }
        }
        else points=Corners(area);
        var placement=item.Placement; var b=placement.BoundsMm;
        var radians=placement.RotationDegrees*Math.PI/180; var cosine=Math.Cos(radians); var sine=Math.Sin(radians);
        var cx=b.X+b.Width/2; var cy=b.Y+b.Height/2;
        foreach(var point in points)
        {
            var x=b.X+(point.X-reference.X)*b.Width/reference.Width;
            var y=b.Y+(point.Y-reference.Y)*b.Height/reference.Height;
            var rotatedX=cx+(x-cx)*cosine-(y-cy)*sine;
            var rotatedY=cy+(x-cx)*sine+(y-cy)*cosine;
            if(rotatedX<safe.X-0.01 || rotatedY<safe.Y-0.01 || rotatedX>safe.Right+0.01 || rotatedY>safe.Bottom+0.01) return true;
        }
        return false;
    }
    private static PointD[] Corners(RectD bounds)=>[new(bounds.X,bounds.Y),new(bounds.Right,bounds.Y),new(bounds.X,bounds.Bottom),new(bounds.Right,bounds.Bottom)];

    public static TextObject CreateText(string text,TextStyle style,PointD location,RectD safe,TextObject? existing=null)
    {
        var x=existing?.Placement.BoundsMm.X ?? Math.Clamp(location.X,safe.X,Math.Max(safe.X,safe.Right-20));
        var y=existing?.Placement.BoundsMm.Y ?? Math.Clamp(location.Y,safe.Y,Math.Max(safe.Y,safe.Bottom-5));
        var width=existing?.Placement.BoundsMm.Width ?? Math.Max(1,safe.Right-x-0.2);
        var height=MeasureTextHeight(text,style,width);
        var placement=existing is {} previous ? previous.Placement with { BoundsMm=new(x,y,width,height) } : new ObjectPlacement(new(x,y,width,height));
        return new(existing?.Id ?? Guid.NewGuid(),placement,text,style);
    }
    public static double MeasureTextHeight(string text,TextStyle style,double widthMm)
    {
        if(!double.IsFinite(widthMm) || widthMm<=0) throw new ArgumentException("Ширина текста должна быть положительной.");
        return TextLayoutService.Layout(text,style,widthMm).HeightMm;
    }
    public static ObjectPlacement Align(SheetObject item,RectD safe,string alignment)
    {
        var b=item.Placement.BoundsMm; var visual=VisualBounds(item);
        var (dx,dy)=PrintableAreaAlignment.Offset(visual,safe,alignment);
        return item.Placement with { BoundsMm=b with { X=b.X+dx,Y=b.Y+dy },Mode=PlacementMode.Fixed };
    }
}
