using PrintMaster.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace PrintMaster.Views;

internal enum ShortcutScope { None,Text,Queue,Sheet,Source }

public partial class MainWindow
{
    private bool _syncingQueue;
    private ShortcutScope _lastWorkScope=ShortcutScope.None;
    private ShortcutScope LastWorkScope=>_lastWorkScope is ShortcutScope.Sheet or ShortcutScope.Source ? ViewModel.IsSheetSelected ? ShortcutScope.Sheet : ShortcutScope.Source : _lastWorkScope;
    private static bool Inside(DependencyObject? child,DependencyObject parent)
    { while(child!=null) { if(child==parent) return true; child=GetParentNode(child); } return false; }
    internal ShortcutScope CurrentShortcutScope
    {
        get
        {
            if(Keyboard.FocusedElement is not DependencyObject focus) return LastWorkScope;
            if(Ancestor<TextBoxBase>(focus)!=null || Ancestor<PasswordBox>(focus)!=null || Ancestor<ComboBox>(focus)!=null) return ShortcutScope.Text;
            if(Inside(focus,QueuePane)) return ShortcutScope.Queue;
            if(Inside(focus,CanvasPane)) return ViewModel.IsSheetSelected ? ShortcutScope.Sheet : ShortcutScope.Source;
            if(Ancestor<MenuItem>(focus)!=null || Ancestor<Menu>(focus)!=null || focus==this) return LastWorkScope;
            return ShortcutScope.None;
        }
    }
    private void KeyboardFocusChanged(object sender,KeyboardFocusChangedEventArgs e)
    {
        if(ViewModel.IsEditingText && e.NewFocus is DependencyObject focus && Inside(focus,QueuePane)) ViewModel.FinishTextEditing();
        var scope=CurrentShortcutScope; if(scope!=ShortcutScope.None) _lastWorkScope=scope;
    }
    private void QueueSelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(_syncingQueue || ViewModel==null || ViewModel.IsQueueMutation) return;
        var active=Keyboard.FocusedElement is DependencyObject focus ? Ancestor<ListBoxItem>(focus)?.DataContext as PrintPageItem : null;
        active ??=e.AddedItems.OfType<PrintPageItem>().LastOrDefault() ?? QueueList.SelectedItem as PrintPageItem;
        _syncingQueue=true;
        try { ViewModel.SetQueueSelection(QueueList.SelectedItems.OfType<PrintPageItem>().ToArray(),active); }
        finally { _syncingQueue=false; }
    }
    private void SyncQueueSelection(object? sender,EventArgs e)
    {
        if(_syncingQueue || ViewModel.IsQueueMutation) return; _syncingQueue=true;
        try
        {
            var selected=ViewModel.HighlightedPages.ToHashSet();
            foreach(var page in QueueList.SelectedItems.OfType<PrintPageItem>().ToArray()) if(!selected.Contains(page)) QueueList.SelectedItems.Remove(page);
            foreach(var page in selected) if(!QueueList.SelectedItems.Contains(page)) QueueList.SelectedItems.Add(page);
        }
        finally { _syncingQueue=false; }
    }
    private void ShortcutKeyDown(object sender,KeyEventArgs e)
    {
        if(OwnedWindows.Cast<Window>().Any(w=>w.IsVisible)){e.Handled=true;return;}
        if(_imageCropOpen || WorkMenuIsOpen) return; // Modal editing/popup navigation owns its keys.
        var key=e.Key==Key.System ? e.SystemKey : e.Key;
        if(key==Key.F1 && Keyboard.Modifiers==ModifierKeys.None) { ShortcutHelp(this,new()); e.Handled=true; return; }
        if(Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && key is Key.S or Key.P) CommitFocusedField();
        if(HandleShortcut(key,Keyboard.Modifiers)) e.Handled=true;
    }
    internal bool HandleShortcut(Key key,ModifierKeys modifiers)
    {
        if(_imageCropOpen || WorkMenuIsOpen || OwnedWindows.Cast<Window>().Any(w=>w.IsVisible)) return false;
        if(ViewModel.IsPickingProjectColor)
        {
            if(key==Key.Escape && modifiers==ModifierKeys.None) {CancelProjectColorPick();ViewModel.Status="Выбор цвета отменён.";return true;}
            return CurrentShortcutScope!=ShortcutScope.Text;
        }
        var scope=CurrentShortcutScope;
        if(key==Key.F10 && modifiers==ModifierKeys.Shift || key==Key.Apps && modifiers==ModifierKeys.None) return OpenWorkContextMenu();
        if(scope==ShortcutScope.Text && key==Key.Z && modifiers==(ModifierKeys.Control|ModifierKeys.Shift)
            && Keyboard.FocusedElement is DependencyObject focused && Ancestor<TextBoxBase>(focused) is {} input)
        { if(input.CanRedo) input.Redo(); return true; }
        if(scope==ShortcutScope.Text) return false;
        if(key==Key.I && modifiers==ModifierKeys.None && scope is ShortcutScope.Source or ShortcutScope.Sheet) return BeginDefaultProjectColorPick();
        var ctrl=modifiers.HasFlag(ModifierKeys.Control); var shift=modifiers.HasFlag(ModifierKeys.Shift); var alt=modifiers.HasFlag(ModifierKeys.Alt);
        if(!ViewModel.IsIdle) return false;
        if(scope is ShortcutScope.Source or ShortcutScope.Sheet && ViewModel.CanEdit)
        {
            EditorTool? tool=modifiers==ModifierKeys.None ? key switch {Key.V=>EditorTool.Select,Key.B=>EditorTool.Brush,Key.M=>EditorTool.Marker,Key.E=>EditorTool.Eraser,_=>null}
                : modifiers==ModifierKeys.Shift && key==Key.E ? EditorTool.ObjectEraser : null;
            if(tool is {} chosen){ViewModel.SetToolCommand.Execute(chosen);return true;}
        }
        if(key==Key.C && modifiers==ModifierKeys.None) return TryCropShortcut(scope);
        if(ctrl && !alt && key==Key.A)
        {
            if(scope==ShortcutScope.None) return false;
            if(scope==ShortcutScope.Queue) ViewModel.SelectQueuePagesCommand.Execute(null);
            else if(scope==ShortcutScope.Sheet) ViewModel.SelectAllSheetObjects();
            else { ViewModel.Tool=EditorTool.Select; Editor.SelectEverything(); }
            return true;
        }
        if(ctrl && (key==Key.Z || key==Key.Y))
        {
            var redo=key==Key.Y || shift; var command=redo ? ViewModel.RedoCommand : ViewModel.UndoCommand;
            if(command.CanExecute(null)) command.Execute(null); return true;
        }
        if(scope==ShortcutScope.None) return false;
        if(key==Key.Delete && modifiers==ModifierKeys.None) { DeleteCurrentSelection(); return true; }
        if(key==Key.Escape && modifiers==ModifierKeys.None)
        {
            if(scope==ShortcutScope.Queue) ViewModel.ClearQueueSelectionCommand.Execute(null);
            else if(scope==ShortcutScope.Sheet) { var wasDragging=SheetEditor.IsGestureActive; SheetEditor.CancelGesture(); if(!wasDragging) ViewModel.SetObjectSelection([]); }
            else { Editor.CancelGesture(); Editor.ClearSelection(); }
            return true;
        }
        if(ctrl && key==Key.D) { if(scope==ShortcutScope.Queue) ViewModel.DuplicateHighlightedPagesCommand.Execute(null); else if(scope==ShortcutScope.Sheet) ViewModel.DuplicateSheetSelection(); else DuplicateSourceSelection(); return true; }
        if(scope==ShortcutScope.Queue && key==Key.Space && modifiers==ModifierKeys.None) { ViewModel.ToggleHighlightedPrintingCommand.Execute(null); return true; }
        if(scope==ShortcutScope.Queue && alt && key is Key.Up or Key.Down) { ViewModel.MoveHighlightedPagesCommand.Execute(key==Key.Up ? -1 : 1); return true; }
        if(scope==ShortcutScope.Sheet && !ctrl && !alt && key is Key.Left or Key.Right or Key.Up or Key.Down)
        { var step=shift ? 10d : 1d; ViewModel.MoveSheetSelection(key==Key.Left ? -step : key==Key.Right ? step : 0,key==Key.Up ? -step : key==Key.Down ? step : 0); return true; }
        return false;
    }
    private void DeleteCurrentSelection(ShortcutScope? targetScope=null)
    {
        var scope=targetScope ?? CurrentShortcutScope;
        if(scope==ShortcutScope.Queue) { if(ViewModel.DeleteHighlightedPagesCommand.CanExecute(null)) ViewModel.DeleteHighlightedPagesCommand.Execute(null); }
        else if(scope==ShortcutScope.Sheet) ViewModel.DeleteSheetSelection();
        else if(scope==ShortcutScope.Source && ViewModel.CanEdit) Editor.DeleteSelection();
    }
    private void DuplicateSourceSelection()
    {
        if(!ViewModel.CanEdit || ViewModel.SelectedPage is not {} page) return;
        if(Editor.WholeImageSelected) { ViewModel.DuplicateSourceImage(); SheetEditor.Focus(); return; }
        var ids=Editor.SelectedAnnotationIds.ToHashSet(); var primary=page.Document.PrimaryContent; if(primary==null) return;
        var copies=primary.Edits.Annotations.Where(a=>ids.Contains(a.Id)).Select(a=>PrintMaster.Services.ClipboardAnnotationService.Clone(a,new(page.SourceWidth,page.SourceHeight),new(page.SourceWidth,page.SourceHeight),1));
        page.Document.SetContentEdits(primary.Id,primary.Edits with { Annotations=primary.Edits.Annotations.AddRange(copies) },"Дублировать аннотации");
    }
    private void SelectAllCanExecute(object sender,CanExecuteRoutedEventArgs e)
    { if(CurrentShortcutScope==ShortcutScope.Text) return; e.CanExecute=ViewModel.IsIdle && CurrentShortcutScope!=ShortcutScope.None; e.Handled=true; }
    private void SelectAllExecuted(object sender,ExecutedRoutedEventArgs e)
    { if(CurrentShortcutScope==ShortcutScope.Text) return; e.Handled=HandleShortcut(Key.A,ModifierKeys.Control); }
    private void DeleteCanExecute(object sender,CanExecuteRoutedEventArgs e)
    {
        var scope=ClipboardCommandScope(e.OriginalSource as DependencyObject); if(scope==ShortcutScope.Text) return;
        if(ViewModel.IsPickingProjectColor) {e.CanExecute=false;e.Handled=true;return;}
        e.CanExecute=scope==ShortcutScope.Queue ? ViewModel.DeleteHighlightedPagesCommand.CanExecute(null)
            : ViewModel.CanEdit && (scope==ShortcutScope.Sheet ? ViewModel.SelectedObjects.Count>0 : scope==ShortcutScope.Source && (Editor.WholeImageSelected || Editor.SelectedAnnotationIds.Length>0)); e.Handled=true;
    }
    private void DeleteExecuted(object sender,ExecutedRoutedEventArgs e)
    { var scope=ClipboardCommandScope(e.OriginalSource as DependencyObject); if(scope==ShortcutScope.Text) return; if(!ViewModel.IsPickingProjectColor) DeleteCurrentSelection(scope); e.Handled=true; }
    private void ShortcutHelp(object sender,RoutedEventArgs e)
    {
        MessageBox.Show(this,"В очереди: Ctrl+A — все страницы; Ctrl/Shift+клик — группа/диапазон; Del — удалить; Пробел — галочки печати; Ctrl+D — дубликат; Alt+↑/↓ — порядок.\n\nНа листе: Ctrl+A — все объекты; Ctrl+клик — группа; Del — удалить; Ctrl+D — дубликат; стрелки — 1 мм, Shift+стрелки — 10 мм. На исходном фото Ctrl+A выбирает фото с аннотациями. V — выбор; B — кисть; M — маркер; E — частичный ластик; Shift+E — удалить объект. C — кадрирование фото; I на холсте — пипетка, Esc — отменить выбор цвета.\n\nCtrl+C/X/V — копировать/вырезать/вставить. Ctrl+Z — отмена; Ctrl+Y или Ctrl+Shift+Z — повтор; Esc — отмена жеста/снять выделение. Shift+F10 или клавиша контекстного меню — меню текущего выделения; ПКМ по участнику группы сохраняет группу. В текстовых и числовых полях клавиши и ПКМ работают внутри поля.\n\nCtrl+O — файлы; Ctrl+N — лист; Ctrl+S — проект; Ctrl+Shift+S — сохранить как; Ctrl+Shift+O — открыть проект; Ctrl+P — печать отмеченных страниц; Ctrl+колесико — масштаб.","Горячие клавиши",MessageBoxButton.OK,MessageBoxImage.Information);
    }
    private async void ClipboardCutExecuted(object sender,ExecutedRoutedEventArgs e)
    {
        var scope=ClipboardCommandScope(e.OriginalSource as DependencyObject); if(scope==ShortcutScope.Text) return;
        var queue=scope==ShortcutScope.Queue; var origin=ViewModel.SelectedPage; e.Handled=true;
        var cut=await ViewModel.CopyContextAsync(queue,true);
        if(cut && !queue && ViewModel.SelectedPage==origin) Editor.ClearSelection();
    }
    private void UndoRedoCanExecute(object sender,CanExecuteRoutedEventArgs e)
    {
        if(TextFieldHasFocus()) return;
        var redo=e.Command==ApplicationCommands.Redo;
        var command=redo ? ViewModel.RedoCommand : ViewModel.UndoCommand;
        e.CanExecute=command.CanExecute(null); e.Handled=true;
    }
    private void UndoRedoExecuted(object sender,ExecutedRoutedEventArgs e)
    {
        if(TextFieldHasFocus()) return;
        e.Handled=HandleShortcut(e.Command==ApplicationCommands.Redo ? Key.Y : Key.Z,ModifierKeys.Control);
    }
}


