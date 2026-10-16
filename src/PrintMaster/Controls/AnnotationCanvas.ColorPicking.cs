using PrintMaster.Models;
using System.Windows;
using System.Windows.Input;

namespace PrintMaster.Controls;

public sealed partial class AnnotationCanvas
{
    public static readonly DependencyProperty IsColorPickingProperty=DependencyProperty.Register(nameof(IsColorPicking),typeof(bool),typeof(AnnotationCanvas),new FrameworkPropertyMetadata(false,(_,_)=>{ }));
    public bool IsColorPicking {get=>(bool)GetValue(IsColorPickingProperty);set=>SetValue(IsColorPickingProperty,value);}
    public event EventHandler<ColorSampleRequest>? ColorSampleRequested;
    internal bool RequestColorSampleAt(Point point)
    {
        if(!IsColorPicking || !IsEnabled || IsPrintPreview || Page?.IsImage!=true || Preview==null || _placement.IsEmpty
            || point.X<0 || point.Y<0 || point.X>=ActualWidth || point.Y>=ActualHeight) return false;
        var visible=_placement.Intersect(SafeArea);
        if(!visible.Contains(new(point.X,point.Y))) ColorSampleRequested?.Invoke(this,new(new(0,0),true));
        else
        {
            var source=Map(point);var crop=Page.Edits.Crop;
            source=new(Math.Clamp(source.X,crop.X,Math.BitDecrement(crop.Right)),Math.Clamp(source.Y,crop.Y,Math.BitDecrement(crop.Bottom)));
            ColorSampleRequested?.Invoke(this,new(source));
        }
        return true;
    }
}
