using PrintMaster.Models;

namespace PrintMaster.Controls;

public sealed class ColorSampleRequest(PointD point,bool paperWhite=false) : EventArgs
{
    public PointD Point {get;}=point;
    public bool PaperWhite {get;}=paperWhite;
}
