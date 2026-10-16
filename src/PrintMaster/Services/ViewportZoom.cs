namespace PrintMaster.Services;

public static class ViewportZoom
{
    public const double Minimum=5;
    public const double Maximum=800;
    public static double Clamp(double percent)=>double.IsFinite(percent) ? Math.Clamp(percent,Minimum,Maximum) : 100;
    public static double FromWheel(double percent,int delta)=>Clamp(percent*Math.Pow(1.15,Math.Clamp(delta,-2400,2400)/120d));
}
