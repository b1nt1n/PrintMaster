using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PrintMaster.Views;

public sealed class BooleanVisibilityConverter : IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture)
    {
        var visible=value is bool b ? b : value is int n && n!=0;
        if((string?)parameter=="Invert") visible=!visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>Binding.DoNothing;
}
public sealed class EnumCheckedConverter : IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture)=>value?.ToString()==(string)parameter;
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>Binding.DoNothing;
}
