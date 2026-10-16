using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace PrintMaster.Controls;

// One layout for labelled commands. The parent control owns focus, automation and
// disabled/checked states; decorative content follows its foreground and type.
public sealed class IconLabel : Grid
{
    public static readonly DependencyProperty IconProperty=DependencyProperty.Register(nameof(Icon),typeof(string),typeof(IconLabel),new FrameworkPropertyMetadata("check"));
    public static readonly DependencyProperty TextProperty=DependencyProperty.Register(nameof(Text),typeof(string),typeof(IconLabel),new FrameworkPropertyMetadata(""));
    public string Icon {get=>(string)GetValue(IconProperty);set=>SetValue(IconProperty,value);}
    public string Text {get=>(string)GetValue(TextProperty);set=>SetValue(TextProperty,value);}
    public IconLabel()
    {
        VerticalAlignment=VerticalAlignment.Center;Focusable=false;IsHitTestVisible=false;
        ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});ColumnDefinitions.Add(new ColumnDefinition());
        var glyph=new LucideIcon {Width=18,Height=18,Margin=new Thickness(0,0,7,0)};
        glyph.SetBinding(LucideIcon.IconProperty,new Binding(nameof(Icon)){Source=this});
        var caption=new TextBlock {VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap};SetColumn(caption,1);
        caption.SetBinding(TextBlock.TextProperty,new Binding(nameof(Text)){Source=this});
        // An implicit TextBlock style has higher precedence than inheritance. Bind
        // explicitly so a primary button's label stays white with its icon.
        foreach(var property in new[]{TextBlock.ForegroundProperty,TextBlock.FontSizeProperty,TextBlock.FontWeightProperty,TextBlock.FontFamilyProperty})
            caption.SetBinding(property,new Binding(property.Name){RelativeSource=new RelativeSource(RelativeSourceMode.FindAncestor,typeof(Control),1)});
        Children.Add(glyph);Children.Add(caption);
    }
    public override string ToString()=>Text;
}
