using PrintMaster.Models;

namespace PrintMaster.Services;

public enum ObjectHandle { None,NorthWest,North,NorthEast,East,SouthEast,South,SouthWest,West,Rotate,TextWidth }

// All geometry is in paper millimeters. Screen zoom only affects drawing/hit areas of handles.
public static class ObjectTransform
{
    private static readonly ObjectHandle[] BoxHandles=[ObjectHandle.NorthWest,ObjectHandle.North,ObjectHandle.NorthEast,ObjectHandle.East,ObjectHandle.SouthEast,ObjectHandle.South,ObjectHandle.SouthWest,ObjectHandle.West,ObjectHandle.Rotate];
    public static IReadOnlyList<ObjectHandle> Handles(SheetObject item)=>!item.Visible || item.Locked ? [] : item switch
    {
        TextObject { Flow:null }=>[ObjectHandle.TextWidth],
        ShapeObject or InkObject or ContentObject=>BoxHandles,
        _=>[]
    };
    private static (int X,int Y) Direction(ObjectHandle handle)=>handle switch
    {
        ObjectHandle.NorthWest=>(-1,-1),ObjectHandle.North=>(0,-1),ObjectHandle.NorthEast=>(1,-1),
        ObjectHandle.East or ObjectHandle.TextWidth=>(1,0),ObjectHandle.SouthEast=>(1,1),ObjectHandle.South=>(0,1),ObjectHandle.SouthWest=>(-1,1),ObjectHandle.West=>(-1,0),_ =>(0,0)
    };
    public static PointD Center(SheetObject item) { var b=item.Placement.BoundsMm; return new(b.X+b.Width/2,b.Y+b.Height/2); }
    public static PointD RotateVector(PointD p,double degrees)
    {
        var angle=degrees*Math.PI/180; var cos=Math.Cos(angle); var sin=Math.Sin(angle);
        return new(p.X*cos-p.Y*sin,p.X*sin+p.Y*cos);
    }
    public static PointD HandlePoint(SheetObject item,ObjectHandle handle,double rotationGapMm=8)
    {
        var b=item.Placement.BoundsMm; var center=Center(item); var (x,y)=Direction(handle);
        var local=handle==ObjectHandle.Rotate ? new PointD(0,-b.Height/2-rotationGapMm) : new(x*b.Width/2,y*b.Height/2);
        var offset=RotateVector(local,item.Placement.RotationDegrees); return new(center.X+offset.X,center.Y+offset.Y);
    }
    public static bool Contains(SheetObject item,PointD point,double toleranceMm=0)
    {
        var center=Center(item); var local=RotateVector(new(point.X-center.X,point.Y-center.Y),-item.Placement.RotationDegrees);
        var b=item.Placement.BoundsMm; var padding=toleranceMm+(item switch { ShapeObject s=>s.Style.WidthMm/2,InkObject i=>i.Style.WidthMm/2,_=>0 });
        return Math.Abs(local.X)<=b.Width/2+padding && Math.Abs(local.Y)<=b.Height/2+padding;
    }
    public static IReadOnlyList<SheetObject> Hits(IEnumerable<SheetObject> objects,PointD point,double toleranceMm=0)
        =>objects.Reverse().Where(o=>o.Visible && !o.Locked && Contains(o,point,toleranceMm)).ToArray();
    public static Guid? NextHit(IEnumerable<SheetObject> objects,PointD point,Guid? current,double toleranceMm=0)
    {
        var hits=Hits(objects,point,toleranceMm); if(hits.Count==0) return null;
        var index=hits.ToList().FindIndex(o=>o.Id==current); return hits[(index+1)%hits.Count].Id;
    }
    public static double NormalizeAngle(double angle)
    {
        if(!double.IsFinite(angle)) throw new ArgumentException("Угол должен быть конечным числом.");
        return (angle%360+540)%360-180;
    }
    public static SheetObject Resize(SheetObject item,ObjectHandle handle,PointD start,PointD point,bool keepAspect=false)
    {
        if(!Handles(item).Contains(handle) || handle==ObjectHandle.Rotate || !Finite(start) || !Finite(point)) return item;
        var b=item.Placement.BoundsMm; var angle=item.Placement.RotationDegrees; var (hx,hy)=Direction(handle);
        var delta=RotateVector(new(point.X-start.X,point.Y-start.Y),-angle);
        if(Math.Abs(delta.X)+Math.Abs(delta.Y)<1e-9) return item;
        var min=item is ContentObject ? 1 : 0.01;
        var w=hx==0 ? b.Width : Math.Max(min,b.Width+hx*delta.X); var h=hy==0 ? b.Height : Math.Max(min,b.Height+hy*delta.Y);
        if(item is TextObject text)
        {
            w=Math.Max(5,b.Width+delta.X); if(Math.Abs(w-b.Width)<1e-9) return item;
            h=TextLayoutService.Layout(text.Text,text.Style,w).HeightMm;
            // Reflow keeps the world-space top-left corner anchored even for rotated text.
            var anchor=HandlePoint(item,ObjectHandle.NorthWest);
            var offset=RotateVector(new(w/2,h/2),angle); var center=new PointD(anchor.X+offset.X,anchor.Y+offset.Y);
            return item with { Placement=item.Placement with { BoundsMm=new(center.X-w/2,center.Y-h/2,w,h),Mode=PlacementMode.Fixed } };
        }
        if(keepAspect || item is ContentObject { KeepAspectRatio:true })
        {
            var sx=w/b.Width; var sy=h/b.Height;
            var scale=hx==0 ? sy : hy==0 ? sx : Math.Abs(sx-1)>=Math.Abs(sy-1) ? sx : sy;
            scale=Math.Max(scale,Math.Max(min/b.Width,min/b.Height)); w=b.Width*scale; h=b.Height*scale;
        }
        var oldCenter=Center(item); var anchorOffset=RotateVector(new(-hx*b.Width/2,-hy*b.Height/2),angle);
        var anchorWorld=new PointD(oldCenter.X+anchorOffset.X,oldCenter.Y+anchorOffset.Y);
        var newOffset=RotateVector(new(hx*w/2,hy*h/2),angle); var newCenter=new PointD(anchorWorld.X+newOffset.X,anchorWorld.Y+newOffset.Y);
        if(w>10000 || h>10000) return item;
        return item with { Placement=item.Placement with { BoundsMm=new(newCenter.X-w/2,newCenter.Y-h/2,w,h),Mode=PlacementMode.Fixed } };
    }
    public static SheetObject Rotate(SheetObject item,PointD start,PointD point,bool snap=false)
    {
        if(!Handles(item).Contains(ObjectHandle.Rotate) || !Finite(start) || !Finite(point)) return item;
        var center=Center(item); if(Math.Abs(point.X-center.X)+Math.Abs(point.Y-center.Y)<1e-8) return item;
        var before=Math.Atan2(start.Y-center.Y,start.X-center.X); var now=Math.Atan2(point.Y-center.Y,point.X-center.X);
        var delta=NormalizeAngle((now-before)*180/Math.PI);
        if(Math.Abs(delta)<1e-9 && !snap) return item;
        var angle=NormalizeAngle(item.Placement.RotationDegrees+delta); if(snap) angle=NormalizeAngle(Math.Round(angle/15)*15);
        if(Math.Abs(NormalizeAngle(angle-item.Placement.RotationDegrees))<1e-9) return item;
        return item with { Placement=item.Placement with { RotationDegrees=angle,Mode=PlacementMode.Fixed } };
    }
    private static bool Finite(PointD point)=>double.IsFinite(point.X) && double.IsFinite(point.Y);
}
