using PrintMaster.Models;

namespace PrintMaster.Services;

public readonly record struct Affine(double M11,double M12,double M21,double M22,double OffsetX,double OffsetY)
{
    public PointD Transform(PointD p) => new(p.X*M11+p.Y*M21+OffsetX,p.X*M12+p.Y*M22+OffsetY);
    public PointD Inverse(PointD p)
    {
        var determinant=M11*M22-M12*M21;
        if(Math.Abs(determinant)<1e-10) throw new InvalidOperationException("Вырожденное преобразование.");
        var x=p.X-OffsetX; var y=p.Y-OffsetY;
        return new((x*M22-y*M21)/determinant,(y*M11-x*M12)/determinant);
    }
}

public static class ImageGeometry
{
    public static Affine CreateTransform(RectD crop,int quarterTurns,RectD destination)
    {
        var oriented=new SizeD(crop.Width,crop.Height).Rotated(quarterTurns);
        var sx=destination.Width/oriented.Width; var sy=destination.Height/oriented.Height;
        return ((quarterTurns%4+4)%4) switch
        {
            0=>new(sx,0,0,sy,destination.X-crop.X*sx,destination.Y-crop.Y*sy),
            1=>new(0,sy,-sx,0,destination.X+crop.Bottom*sx,destination.Y-crop.X*sy),
            2=>new(-sx,0,0,-sy,destination.X+crop.Right*sx,destination.Y+crop.Bottom*sy),
            _=>new(0,-sy,sx,0,destination.X-crop.Y*sx,destination.Y+crop.Right*sy)
        };
    }
    public static RectD AspectCrop(PointD start,PointD end,double aspectRatio,RectD bounds)
    {
        var dx=end.X-start.X; var dy=end.Y-start.Y;
        var signX=dx<0 ? -1.0 : 1.0; var signY=dy<0 ? -1.0 : 1.0;
        var w=Math.Abs(dx); var h=Math.Abs(dy);
        if(aspectRatio>0)
        {
            if(w/Math.Max(h,0.001)>aspectRatio) h=w/aspectRatio; else w=h*aspectRatio;
            var availableX=signX>0 ? bounds.Right-start.X : start.X-bounds.X;
            var availableY=signY>0 ? bounds.Bottom-start.Y : start.Y-bounds.Y;
            var factor=Math.Min(1,Math.Min(availableX/Math.Max(w,0.001),availableY/Math.Max(h,0.001)));
            w*=factor; h*=factor;
        }
        return RectD.FromPoints(start,new(start.X+signX*w,start.Y+signY*h)).Intersect(bounds);
    }
}
