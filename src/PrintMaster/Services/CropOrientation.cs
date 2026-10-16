using PrintMaster.Models;

namespace PrintMaster.Services;

// Crop persistence always uses original-source coordinates. UI dimensions/handles use visible orientation.
public static class CropOrientation
{
    private static Affine Transform(SizeD source,int turns)
    {
        var size=source.Rotated(turns);
        return ImageGeometry.CreateTransform(new(0,0,source.Width,source.Height),turns,new(0,0,size.Width,size.Height));
    }
    public static PointD ToOriented(PointD point,SizeD source,int turns)=>Transform(source,turns).Transform(point);
    public static RectD ToOriented(RectD crop,SizeD source,int turns)=>Map(crop,Transform(source,turns).Transform);
    public static RectD ToSource(RectD crop,SizeD source,int turns)=>Map(crop,Transform(source,turns).Inverse);
    private static RectD Map(RectD crop,Func<PointD,PointD> map)
    {
        var a=map(new(crop.X,crop.Y)); var b=map(new(crop.Right,crop.Bottom));
        return RectD.FromPoints(a,b);
    }
}
