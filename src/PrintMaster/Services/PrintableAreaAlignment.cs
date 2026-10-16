using PrintMaster.Models;

namespace PrintMaster.Services;

public static class PrintableAreaAlignment
{
    public static PointD Offset(RectD visual,RectD safe,string alignment)
    {
        var dx=alignment switch
        {
            "left" or "top-left" or "bottom-left"=>safe.X-visual.X,
            "right" or "top-right" or "bottom-right"=>safe.Right-visual.Right,
            "center" or "center-both"=>safe.X+safe.Width/2-visual.X-visual.Width/2,_=>0
        };
        var dy=alignment switch
        {
            "top" or "top-left" or "top-right"=>safe.Y-visual.Y,
            "bottom" or "bottom-left" or "bottom-right"=>safe.Bottom-visual.Bottom,
            "middle" or "center-both"=>safe.Y+safe.Height/2-visual.Y-visual.Height/2,_=>0
        };
        return new(dx,dy);
    }
}
