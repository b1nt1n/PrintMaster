using System.Windows;

namespace PrintMaster.Controls;

public sealed partial class SheetCanvas
{
    public static readonly DependencyProperty IsColorPickingProperty=DependencyProperty.Register(nameof(IsColorPicking),typeof(bool),typeof(SheetCanvas),new FrameworkPropertyMetadata(false));
    public bool IsColorPicking {get=>(bool)GetValue(IsColorPickingProperty);set=>SetValue(IsColorPickingProperty,value);}
    public event EventHandler<ColorSampleRequest>? ColorSampleRequested;
    internal bool RequestColorSampleAt(Point point)
    {
        if(!IsColorPicking || !CanEdit || point.X<0 || point.Y<0 || point.X>=ActualWidth || point.Y>=ActualHeight) return false;
        ColorSampleRequested?.Invoke(this,new(Map(point))); return true;
    }
}
