using PrintMaster.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace PrintMaster.Views;

public partial class MainWindow
{
    private sealed class OriginalActionHints(FrameworkElement element)
    {
        public BindingBase? ToolTipBinding=BindingOperations.GetBindingBase(element,FrameworkElement.ToolTipProperty);
        public object ToolTipValue=element.ReadLocalValue(FrameworkElement.ToolTipProperty);
        public BindingBase? HelpBinding=BindingOperations.GetBindingBase(element,AutomationProperties.HelpTextProperty);
        public object HelpValue=element.ReadLocalValue(AutomationProperties.HelpTextProperty);
        public bool Overridden;
    }
    private readonly Dictionary<FrameworkElement,OriginalActionHints> _actionHintControls=[];
    private bool _actionHintsInitialized,_actionHintsRefreshPending,_actionHintsClosed;

    private void InitializeActionHints()
    {
        if(_actionHintsInitialized) return;
        _actionHintsInitialized=true;
        ViewModel.PropertyChanged+=ActionHintsViewModelChanged;
        GotKeyboardFocus+=ActionHintsFocusChanged;
        PreviewMouseMove+=ActionHintsMouseMoved;
        AddHandler(FrameworkElement.LoadedEvent,new RoutedEventHandler(ActionHintsControlLoaded),true);
        AddHandler(MenuItem.SubmenuOpenedEvent,new RoutedEventHandler(ActionHintsSubmenuOpened),true);
        Closed+=CloseActionHints;
        AttachActionHintControls(this); RefreshActionHints();
    }
    private void ActionHintsControlLoaded(object sender,RoutedEventArgs e)
    {
        if(e.OriginalSource is DependencyObject loaded) AttachActionHintControls(loaded);
        ScheduleActionHintsRefresh();
    }
    private void ActionHintsSubmenuOpened(object sender,RoutedEventArgs e)
    {
        if(e.OriginalSource is DependencyObject menu) AttachActionHintControls(menu);
        RefreshActionHints();
    }
    private void ActionHintsFocusChanged(object sender,KeyboardFocusChangedEventArgs e)=>ScheduleActionHintsRefresh();
    private void ActionHintsMouseMoved(object sender,MouseEventArgs e)
    {
        // List item templates can appear after the window's Loaded event, which is a direct event.
        // Register a newly realized command control before its tooltip is requested.
        if(e.OriginalSource is not DependencyObject source) return;
        FrameworkElement? control=Ancestor<ButtonBase>(source) ?? (FrameworkElement?)Ancestor<MenuItem>(source);
        if(control==null) return;
        AttachActionHintControls(control); RefreshActionHint(control);
    }
    private void ActionHintsViewModelChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName is null or "" || e.PropertyName.EndsWith("ActionAvailability",StringComparison.Ordinal)
            || e.PropertyName is nameof(PrintSessionViewModel.IsBusy) or nameof(PrintSessionViewModel.SelectedPage)
                or nameof(PrintSessionViewModel.SelectedObjects) or nameof(PrintSessionViewModel.SelectedObjectIds)
                or nameof(PrintSessionViewModel.HighlightedPageCount) or nameof(PrintSessionViewModel.ShowPrintPreview)
                or nameof(PrintSessionViewModel.IsEditingText)) ScheduleActionHintsRefresh();
    }
    private void ScheduleActionHintsRefresh()
    {
        if(_actionHintsClosed || _actionHintsRefreshPending) return;
        _actionHintsRefreshPending=true;
        Dispatcher.BeginInvoke(DispatcherPriority.DataBind,new Action(()=>
        {
            _actionHintsRefreshPending=false;
            if(!_actionHintsClosed) { AttachActionHintControls(this); RefreshActionHints(); }
        }));
    }
    private void AttachActionHintControls(DependencyObject root)
    {
        foreach(var item in ActionHintTree(root))
        {
            if(item is not FrameworkElement element || element is not (ButtonBase or MenuItem) || _actionHintControls.ContainsKey(element)) continue;
            _actionHintControls.Add(element,new(element));
            ToolTipService.SetShowOnDisabled(element,true);
            element.ToolTipOpening+=ActionHintToolTipOpening;
            RefreshActionHint(element);
        }
    }
    private static IEnumerable<DependencyObject> ActionHintTree(DependencyObject root)
    {
        var visited=new HashSet<DependencyObject>(); var pending=new Stack<DependencyObject>(); pending.Push(root);
        while(pending.TryPop(out var current))
        {
            if(!visited.Add(current)) continue;
            yield return current;
            if(current is Visual or Visual3D)
                for(var i=0;i<VisualTreeHelper.GetChildrenCount(current);i++) pending.Push(VisualTreeHelper.GetChild(current,i));
            if(current is FrameworkElement or FrameworkContentElement)
                foreach(var child in LogicalTreeHelper.GetChildren(current)) if(child is DependencyObject dependency) pending.Push(dependency);
        }
    }
    private void ActionHintToolTipOpening(object sender,ToolTipEventArgs e)
    { if(sender is FrameworkElement element) RefreshActionHint(element); }
    private void RefreshActionHints()
    {
        PruneDetachedMenuHints();
        foreach(var element in _actionHintControls.Keys) RefreshActionHint(element);
    }
    private void PruneDetachedMenuHints()
    {
        // Recent projects and context menus recreate explicit MenuItem objects. A removed
        // item can temporarily retain its old visual host, so check the owner's live items.
        foreach(var menu in _actionHintControls.Keys.OfType<MenuItem>().ToArray())
        {
            if(LogicalTreeHelper.GetParent(menu)!=null) continue;
            var owner=ItemsControl.ItemsControlFromItemContainer(menu);
            if(owner?.Items.Contains(menu)==true) continue;
            var original=_actionHintControls[menu]; menu.ToolTipOpening-=ActionHintToolTipOpening;
            if(original.Overridden)
            {
                RestoreActionHintValue(menu,FrameworkElement.ToolTipProperty,original.ToolTipBinding,original.ToolTipValue);
                RestoreActionHintValue(menu,AutomationProperties.HelpTextProperty,original.HelpBinding,original.HelpValue);
            }
            _actionHintControls.Remove(menu);
        }
    }
    private void RefreshActionHint(FrameworkElement element)
    {
        var command=element switch { ButtonBase button=>button.Command,MenuItem menu=>menu.Command,_=>null };
        var parameter=element switch { ButtonBase button=>button.CommandParameter,MenuItem menu=>menu.CommandParameter,_=>null };
        var target=element switch { ButtonBase button=>button.CommandTarget as DependencyObject,MenuItem menu=>menu.CommandTarget as DependencyObject,_=>null };
        var hint=command is RoutedCommand routed ? RoutedActionHint(routed,target)
            : ReferenceEquals(command,ViewModel.SetToolCommand) ? parameter is PrintMaster.Models.EditorTool.Crop ? ViewModel.CropActionAvailability : ViewModel.EditorActionAvailability
            : ViewModel.GetActionHint(command,parameter);
        if(command!=null && !ReferenceEquals(command,ViewModel.CancelCommand) && !element.IsEnabled && ViewModel.IsBusy)
            hint=ViewModel.IdleActionAvailability;
        var original=_actionHintControls[element];
        if(hint.Length!=0)
        {
            element.SetValue(FrameworkElement.ToolTipProperty,hint);
            AutomationProperties.SetHelpText(element,hint); original.Overridden=true;
        }
        else if(original.Overridden)
        {
            RestoreActionHintValue(element,FrameworkElement.ToolTipProperty,original.ToolTipBinding,original.ToolTipValue);
            RestoreActionHintValue(element,AutomationProperties.HelpTextProperty,original.HelpBinding,original.HelpValue);
            original.Overridden=false;
        }
    }
    private static void RestoreActionHintValue(DependencyObject element,DependencyProperty property,BindingBase? binding,object original)
    {
        if(binding!=null) BindingOperations.SetBinding(element,property,binding);
        else if(original==DependencyProperty.UnsetValue) element.ClearValue(property);
        else element.SetValue(property,original);
    }
    private string RoutedActionHint(RoutedCommand command,DependencyObject? target)
    {
        var scope=ClipboardCommandScope(target);
        if(scope==ShortcutScope.Text) return TextActionHint(command,target);
        if(command==ApplicationCommands.Undo) return ViewModel.UndoActionAvailability;
        if(command==ApplicationCommands.Redo) return ViewModel.RedoActionAvailability;
        if(command!=ApplicationCommands.Copy && command!=ApplicationCommands.Cut && command!=ApplicationCommands.Paste
            && command!=ApplicationCommands.Delete && command!=ApplicationCommands.SelectAll) return "";
        if(ViewModel.IsBusy) return ViewModel.IdleActionAvailability;
        if(scope==ShortcutScope.None) return "Перейдите в очередь страниц, на холст или в текстовое поле, чтобы выбрать назначение действия.";
        if(command==ApplicationCommands.Paste)
        {
            if(!ViewModel.CanPasteClipboard) return ViewModel.PasteActionAvailability;
            return scope==ShortcutScope.Queue || !ViewModel.CanInsertOnCurrentPage
                ? "Вставить содержимое буфера отдельными страницами." : "Вставить содержимое буфера на текущий лист.";
        }
        if(scope==ShortcutScope.Queue)
        {
            if(command==ApplicationCommands.SelectAll) return ViewModel.Pages.Count==0 ? "Очередь пуста. Добавьте файлы или создайте лист."
                : "Выделить все страницы очереди. Галочки печати задаются отдельно.";
            if(ViewModel.HighlightedPageCount==0) return ViewModel.QueueSelectionActionAvailability;
            return command==ApplicationCommands.Copy ? "Скопировать выделенные страницы вместе с материалами и оформлением."
                : command==ApplicationCommands.Cut ? "Вырезать выделенные страницы после успешного копирования."
                : "Удалить выделенные страницы из очереди. Ctrl+Z — отменить.";
        }
        if(command==ApplicationCommands.Copy || command==ApplicationCommands.Cut)
            return !ViewModel.CanCopySelection ? ViewModel.CopyActionAvailability
                : command==ApplicationCommands.Cut ? "Вырезать выделенные объекты после успешного копирования." : "Скопировать выделенные объекты с оформлением.";
        if(!ViewModel.CanEdit) return ViewModel.EditorActionAvailability;
        if(command==ApplicationCommands.SelectAll) return scope==ShortcutScope.Sheet
            ? "Выделить все доступные объекты на листе." : "Выделить исходную фотографию и аннотации.";
        var hasSelection=scope==ShortcutScope.Sheet ? ViewModel.SelectedObjects.Count>0 : Editor.WholeImageSelected || Editor.SelectedAnnotationIds.Length>0;
        return hasSelection ? "Удалить выделенные объекты. Ctrl+Z — отменить." : "Выделите объект на холсте, чтобы удалить его.";
    }
    private TextBoxBase? ActionHintTextTarget(DependencyObject? target)
        =>target!=null ? Ancestor<TextBoxBase>(target)
            : FocusManager.GetFocusedElement(this) is DependencyObject logicalFocus && Ancestor<TextBoxBase>(logicalFocus) is {} text ? text
            : Keyboard.FocusedElement is DependencyObject keyboardFocus ? Ancestor<TextBoxBase>(keyboardFocus) : null;
    private string TextActionHint(RoutedCommand command,DependencyObject? target)
    {
        var text=ActionHintTextTarget(target);
        if(command==ApplicationCommands.Undo) return text?.CanUndo==true ? "Отменить изменение текста в текущем поле."
            : "В этом текстовом поле нет действий для отмены.";
        if(command==ApplicationCommands.Redo) return text?.CanRedo==true ? "Повторить изменение текста в текущем поле."
            : "В этом текстовом поле нет отменённых действий для повтора.";
        if(command==ApplicationCommands.SelectAll) return "Выделить весь текст в текущем поле.";
        if(command!=ApplicationCommands.Copy && command!=ApplicationCommands.Cut && command!=ApplicationCommands.Paste && command!=ApplicationCommands.Delete) return "";
        if(command!=ApplicationCommands.Copy && text?.IsReadOnly==true) return "Это текстовое поле доступно только для чтения.";
        if(command==ApplicationCommands.Paste) return text!=null && !command.CanExecute(null,text)
            ? "Скопируйте текст в буфер обмена, чтобы вставить его в текущее поле." : "Вставить текст из буфера в текущее поле.";
        var hasSelection=text is TextBox input ? input.SelectionLength>0 : text is RichTextBox rich && !rich.Selection.IsEmpty;
        if(command==ApplicationCommands.Delete && !hasSelection)
        {
            if(text is TextBox plain) return plain.CaretIndex<plain.Text.Length
                ? "Удалить символ справа от курсора в текущем текстовом поле."
                : "Справа от курсора нет символа. Переместите курсор или выделите текст для удаления.";
            return text!=null && command.CanExecute(null,text) ? "Удалить символ справа от курсора в текущем текстовом поле."
                : "Переместите курсор или выделите текст в текущем поле для удаления.";
        }
        if(!hasSelection) return "Выделите текст в текущем поле. Команда действует внутри поля, а не на страницы или объекты.";
        return command==ApplicationCommands.Copy ? "Скопировать выделенный текст из текущего поля."
            : command==ApplicationCommands.Cut ? "Вырезать выделенный текст из текущего поля." : "Удалить выделенный текст в текущем поле.";
    }
    private void CloseActionHints(object? sender,EventArgs e)
    {
        _actionHintsClosed=true; ViewModel.PropertyChanged-=ActionHintsViewModelChanged;
        GotKeyboardFocus-=ActionHintsFocusChanged;
        PreviewMouseMove-=ActionHintsMouseMoved;
        RemoveHandler(FrameworkElement.LoadedEvent,new RoutedEventHandler(ActionHintsControlLoaded));
        RemoveHandler(MenuItem.SubmenuOpenedEvent,new RoutedEventHandler(ActionHintsSubmenuOpened));
        foreach(var element in _actionHintControls.Keys) element.ToolTipOpening-=ActionHintToolTipOpening;
        _actionHintControls.Clear(); Closed-=CloseActionHints;
    }
}
