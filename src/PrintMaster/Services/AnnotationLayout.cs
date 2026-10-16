using PrintMaster.Models;

namespace PrintMaster.Services;

public static class AnnotationLayout
{
    public static RectD VisualBounds(Annotation item)
    {
        var points=item.Kind is AnnotationKind.Ink or AnnotationKind.Marker && item.Points.Count>0
            ? item.Points.ToList() : new List<PointD> {item.Start,item.End};
        if(item.Kind==AnnotationKind.Arrow)
        {
            var angle=Math.Atan2(item.End.Y-item.Start.Y,item.End.X-item.Start.X); var length=Math.Max(14,item.Thickness*4);
            points.Add(new(item.End.X-length*Math.Cos(angle-0.5),item.End.Y-length*Math.Sin(angle-0.5)));
            points.Add(new(item.End.X-length*Math.Cos(angle+0.5),item.End.Y-length*Math.Sin(angle+0.5)));
        }
        var padding=item.Kind==AnnotationKind.Text ? 0 : item.Thickness/2;
        var x=points.Min(p=>p.X)-padding; var y=points.Min(p=>p.Y)-padding;
        return new(x,y,points.Max(p=>p.X)+padding-x,points.Max(p=>p.Y)+padding-y);
    }
    public static Annotation Align(Annotation item,RectD crop,int quarterTurns,string alignment)
    {
        var b=VisualBounds(item); var size=new SizeD(crop.Width,crop.Height).Rotated(quarterTurns);
        var transform=ImageGeometry.CreateTransform(crop,quarterTurns,new(0,0,size.Width,size.Height));
        var corners=new[] {new PointD(b.X,b.Y),new(b.Right,b.Y),new(b.X,b.Bottom),new(b.Right,b.Bottom)}.Select(transform.Transform).ToArray();
        var x=corners.Min(p=>p.X); var y=corners.Min(p=>p.Y); var right=corners.Max(p=>p.X); var bottom=corners.Max(p=>p.Y);
        var dx=alignment switch { "left"=>-x,"center"=>size.Width/2-(x+right)/2,"right"=>size.Width-right,_=>0 };
        var dy=alignment switch { "top"=>-y,"middle"=>size.Height/2-(y+bottom)/2,"bottom"=>size.Height-bottom,_=>0 };
        var origin=transform.Inverse(new(0,0)); var moved=transform.Inverse(new(dx,dy));
        if(Math.Abs(moved.X-origin.X)<1e-8 && Math.Abs(moved.Y-origin.Y)<1e-8) return item;
        return item.Move(moved.X-origin.X,moved.Y-origin.Y);
    }
}
