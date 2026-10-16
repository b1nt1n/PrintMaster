using PrintMaster.Models;
using System.Collections.Immutable;

namespace PrintMaster.Services;

public static class ClipboardAnnotationService
{
    public static Annotation Clone(Annotation source,SizeD original,SizeD target,int index=1)
    {
        var sx=target.Width/original.Width; var sy=target.Height/original.Height; var scale=Math.Min(sx,sy); var offset=10*index;
        PointD Point(PointD p)=>new(p.X*sx+offset,p.Y*sy+offset);
        return source with { Id=Guid.NewGuid(),Start=Point(source.Start),End=Point(source.End),Thickness=source.Thickness*scale,FontSize=source.FontSize*scale,Points=source.Points.Select(Point).ToImmutableArray() };
    }
    public static SheetObject ToSheet(Annotation source,SizeD original,RectD safe,int index=1)
    {
        var scale=Math.Min(safe.Width/original.Width,safe.Height/original.Height); var origin=new PointD(safe.X+3*index,safe.Y+3*index);
        PointD Point(PointD p)=>new(origin.X+p.X*scale,origin.Y+p.Y*scale);
        var start=Point(source.Start); var end=Point(source.End); var width=Math.Max(0.01,Math.Abs(end.X-start.X)); var height=Math.Max(0.01,Math.Abs(end.Y-start.Y));
        var bounds=new RectD(Math.Min(start.X,end.X),Math.Min(start.Y,end.Y),width,height);
        var style=new StrokeStyle(source.Color,Math.Max(0.01,source.Thickness*scale),source.EffectiveOpacity,source.FillColor);
        if(source.Kind==AnnotationKind.Text)
            return SheetEditing.CreateText(source.Text,AnnotationTextLayout.Style(source,scale),start,safe);
        if(source.Kind is AnnotationKind.Ink or AnnotationKind.Marker)
        {
            var points=source.Points.Select(Point).ToArray(); if(points.Length==0) points=[start];
            bounds=new(points.Min(p=>p.X),points.Min(p=>p.Y),Math.Max(0.01,points.Max(p=>p.X)-points.Min(p=>p.X)),Math.Max(0.01,points.Max(p=>p.Y)-points.Min(p=>p.Y)));
            return new InkObject(Guid.NewGuid(),new(bounds),points.Select(p=>new PointD((p.X-bounds.X)/bounds.Width,(p.Y-bounds.Y)/bounds.Height)).ToImmutableArray(),style);
        }
        var shape=source.Kind==AnnotationKind.Arrow ? SheetShapeKind.Arrow : source.Kind==AnnotationKind.Ellipse ? SheetShapeKind.Ellipse : SheetShapeKind.Rectangle;
        var angle=shape==SheetShapeKind.Arrow ? (Math.Atan2(end.Y-start.Y,end.X-start.X)-Math.Atan2(height,width))*180/Math.PI : 0;
        return new ShapeObject(Guid.NewGuid(),new(bounds,angle),shape,style);
    }
}
