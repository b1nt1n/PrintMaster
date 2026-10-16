using PrintMaster.Services;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PrintMaster.Views;

// Conversion is culture-independent on input; range/integer validation lives in NumericField.
public sealed class NumericValueConverter : IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture)=>value switch
    { null=>"",string text=>text,IFormattable number=>number.ToString("G",CultureInfo.CurrentCulture),_=>value.ToString() ?? "" };
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)
    {
        var text=value as string ?? "";
        if(targetType==typeof(string)) return text;
        if(!NumericInputRules.TryParse(text,out var number)) return DependencyProperty.UnsetValue;
        if(targetType==typeof(int)) return number==Math.Truncate(number) && number>=int.MinValue && number<=int.MaxValue ? (object)(int)number : DependencyProperty.UnsetValue;
        if(targetType==typeof(double)) return number;
        return DependencyProperty.UnsetValue;
    }
}
