using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PrintMaster.Controls;
using System.Windows.Data;

namespace PrintMaster.Views;

public sealed class QueueCopiesDialog : Window
{
    private readonly NumericField _field;
    public int Copies { get; private set; }
    public QueueCopiesDialog(int count,int copies)
    {
        Title="Копии страниц"; Width=350; SizeToContent=SizeToContent.Height;
        ResizeMode=ResizeMode.NoResize; WindowStartupLocation=WindowStartupLocation.CenterOwner; ShowInTaskbar=false;
        if(TryFindResource(typeof(Window)) is Style style) Style=style;
        var panel=new StackPanel(); Content=new Border { Padding=new Thickness(20),Background=TryFindResource("Background") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White,Child=panel };
        panel.Children.Add(new TextBlock { Text=$"Копии для каждой из {count} выделенных страниц",TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,12) });
        _field=new NumericField { Name="CopiesInput",Text=copies.ToString(CultureInfo.InvariantCulture),Minimum=1,Maximum=999,IsInteger=true,Unit="коп.",ShowInlineError=false,Margin=new(0,0,0,8) }; panel.Children.Add(_field);
        System.Windows.Automation.AutomationProperties.SetName(_field,"Количество копий от 1 до 999");
        var error=new TextBlock { Name="CopiesError",Text="Введите целое число от 1 до 999.",Visibility=Visibility.Collapsed,TextWrapping=TextWrapping.Wrap,Foreground=System.Windows.Media.Brushes.Firebrick };
        panel.Children.Add(error);
        error.SetBinding(TextBlock.TextProperty,new Binding(nameof(NumericField.Error)) { Source=_field });
        error.SetBinding(VisibilityProperty,new Binding("Error.Length") { Source=_field,Converter=new BooleanVisibilityConverter() });
        var buttons=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,14,0,0) };
        var cancel=new Button { Content=new IconLabel {Icon="x",Text="Отмена"},IsCancel=true,Margin=new(0,0,8,0) };
        var apply=new Button { Name="ApplyCopies",Content=new IconLabel {Icon="check",Text="Применить"},IsDefault=true };
        System.Windows.Automation.AutomationProperties.SetName(cancel,"Отмена");System.Windows.Automation.AutomationProperties.SetName(apply,"Применить количество копий");
        if(TryFindResource("PrimaryButton") is Style primary)apply.Style=primary;
        buttons.Children.Add(cancel); buttons.Children.Add(apply); panel.Children.Add(buttons);
        apply.Click+=(_,_)=>
        {
            if(!_field.Commit()) { _field.FocusInput(); return; }
            Copies=int.Parse(_field.Text,CultureInfo.InvariantCulture); DialogResult=true;
        };
        Loaded+=(_,_)=>{ _field.FocusInput(); _field.Input.SelectAll(); };
        PrintMaster.Services.WindowWorkArea.Attach(this);
    }
}
