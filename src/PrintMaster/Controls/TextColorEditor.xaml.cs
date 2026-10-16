using PrintMaster.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace PrintMaster.Controls;

public partial class TextColorEditor : UserControl
{
    public static readonly DependencyProperty ColorProperty=DependencyProperty.Register(nameof(Color),typeof(string),typeof(TextColorEditor),
        new FrameworkPropertyMetadata("#111827",FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,ColorChanged));
    public static readonly DependencyProperty ColorLabelProperty=DependencyProperty.Register(nameof(ColorLabel),typeof(string),typeof(TextColorEditor),new PropertyMetadata("Цвет"));
    public static readonly DependencyProperty IsMixedProperty=DependencyProperty.Register(nameof(IsMixed),typeof(bool),typeof(TextColorEditor),new PropertyMetadata(false,ColorChanged));
    public string Color { get=>(string)GetValue(ColorProperty); set=>SetValue(ColorProperty,value); }
    public string ColorLabel { get=>(string)GetValue(ColorLabelProperty); set=>SetValue(ColorLabelProperty,value); }
    public bool IsMixed { get=>(bool)GetValue(IsMixedProperty); set=>SetValue(IsMixedProperty,value); }
    public static readonly DependencyProperty CanPickColorProperty=DependencyProperty.Register(nameof(CanPickColor),typeof(bool),typeof(TextColorEditor),new PropertyMetadata(false));
    public bool CanPickColor {get=>(bool)GetValue(CanPickColorProperty);set=>SetValue(CanPickColorProperty,value);}
    public static readonly RoutedEvent PickColorRequestedEvent=EventManager.RegisterRoutedEvent(nameof(PickColorRequested),RoutingStrategy.Bubble,typeof(RoutedEventHandler),typeof(TextColorEditor));
    public event RoutedEventHandler PickColorRequested {add=>AddHandler(PickColorRequestedEvent,value);remove=>RemoveHandler(PickColorRequestedEvent,value);}
    private void RequestColorPick(object sender,RoutedEventArgs e)
    {if(CanPickColor && IsEnabled) RaiseEvent(new RoutedEventArgs(PickColorRequestedEvent,this));}
    internal void ApplySampleColor(string value)
    {SetCurrentValue(ColorProperty,value);GetBindingExpression(ColorProperty)?.UpdateSource();Refresh();}
    internal ITextColorPicker Picker { get; set; }=new WindowsTextColorPicker();
    public TextColorEditor() { InitializeComponent(); Refresh(); }
    private static void ColorChanged(DependencyObject source,DependencyPropertyChangedEventArgs e)=>((TextColorEditor)source).Refresh();
    private void Refresh()
    {
        if(HexInput==null) return;
        var rgb=TextColorService.ToColorRef(Color);
        HexInput.Text=IsMixed ? "" : TextColorService.FromColorRef(rgb);
        var media=System.Windows.Media.Color.FromRgb((byte)rgb,(byte)(rgb>>8),(byte)(rgb>>16));
        var brush=new SolidColorBrush(media); brush.Freeze(); Swatch.Background=IsMixed ? Brushes.Transparent : brush;
        PaletteGlyph.Foreground=IsMixed || media.R*0.299+media.G*0.587+media.B*0.114>150 ? Brushes.Black : Brushes.White;
        ValidationMessage.Visibility=Visibility.Collapsed;
    }
    private bool CommitHex()
    {
        if(IsMixed && string.IsNullOrWhiteSpace(HexInput.Text)) { Refresh(); return true; }
        if(!TextColorService.TryNormalize(HexInput.Text,out var value))
        {
            ValidationMessage.Text="Введите 6 знаков: 0–9, A–F (например, #2A73C5).";
            ValidationMessage.Visibility=Visibility.Visible; return false;
        }
        SetCurrentValue(ColorProperty,value); GetBindingExpression(ColorProperty)?.UpdateSource(); Refresh(); return true;
    }
    private void ApplyHex(object sender,KeyboardFocusChangedEventArgs e)
    { if(!IsEnabled) { Refresh(); return; } CommitHex(); }
    private void HexKeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key==Key.Enter) { CommitHex(); e.Handled=true; }
        else if(e.Key==Key.Escape) { Refresh(); e.Handled=true; }
    }
    private void OpenPalette(object sender,RoutedEventArgs e)
    {
        if(!IsEnabled) return;
        var owner=Window.GetWindow(this); if(owner==null) return;
        var selected=Picker.Pick(new WindowInteropHelper(owner).EnsureHandle(),Color);
        if(selected==null) return;
        SetCurrentValue(ColorProperty,selected); GetBindingExpression(ColorProperty)?.UpdateSource(); Refresh();
    }
}
