using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PrintMaster.Controls;

namespace PrintMaster.Views;

internal static class DialogInputValidation
{
    public static bool CommitAndCheck(DependencyObject root)
    {
        if(!NumericField.CommitVisible(root)) return false;
        if(Keyboard.FocusedElement is TextBox text && !HasNumericParent(text)) text.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        return !HasError(root);
    }
    private static bool HasNumericParent(DependencyObject child)
    { for(var current=VisualTreeHelper.GetParent(child);current!=null;current=VisualTreeHelper.GetParent(current)) if(current is NumericField) return true; return false; }
    private static bool HasError(DependencyObject root)
    {
        if(root is FrameworkElement element && (!element.IsVisible || !element.IsEnabled)) return false;
        if(Validation.GetHasError(root) || root is NumericField { HasError:true }) return true;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) if(HasError(VisualTreeHelper.GetChild(root,i))) return true;
        return false;
    }
}
