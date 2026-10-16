using PrintMaster.Controls;
using PrintMaster.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PrintMaster.Views;

public partial class MainWindow
{
    // Commands from a popup route through its explicit PlacementTarget, not the previous keyboard focus.
    internal ShortcutScope ClipboardCommandScope(DependencyObject? target)
    {
        if(target!=null)
        {
            if(Ancestor<TextBoxBase>(target)!=null || Ancestor<PasswordBox>(target)!=null || Ancestor<ComboBox>(target)!=null) return ShortcutScope.Text;
            if(Inside(target,QueuePane)) return ShortcutScope.Queue;
            if(Inside(target,CanvasPane)) return ViewModel.IsSheetSelected ? ShortcutScope.Sheet : ShortcutScope.Source;
        }
        return CurrentShortcutScope;
    }
}
