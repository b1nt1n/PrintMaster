using PrintMaster.Controls;
using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace PrintMaster.Views;

public partial class MainWindow : Window
{
    public PrintSessionViewModel ViewModel { get; }
    private Point _dragStart;
    private PrintPageItem? _dragPage;
    private PrintPageItem[] _dragPages=[];
    private bool _closingAfterCancel;
    private bool _environmentClosePrepared;
    internal ViewportNavigation Navigation { get; }
    public MainWindow(PrintSessionViewModel? viewModel=null,IReadOnlyList<string>? startupFiles=null)
    {
        InitializeComponent(); ViewModel=viewModel ?? new(new UserDialogs(),preferences:new ApplicationPreferencesStore(),workspaceState:new WorkspaceStateStore()); DataContext=ViewModel;
        ViewModel.CommitInputs=()=>DialogInputValidation.CommitAndCheck(this);
        InitializeActionHints();
        InitializeProjectColorPicker();
        WindowWorkArea.Attach(this);
        if(ViewModel.IsWorkspaceInitialized) RestoreWorkspaceEnvironment();
        Loaded+=async (_,_)=>
        {
            await ViewModel.InitializeWorkspaceAsync(); RestoreWorkspaceEnvironment();
            await ViewModel.InitializeAsync();
            if(startupFiles is { Count:>0 })
            {
                var project=startupFiles.FirstOrDefault(p=>System.IO.Path.GetExtension(p).Equals(ProjectFileService.Extension,StringComparison.OrdinalIgnoreCase));
                if(project!=null) await ViewModel.OpenProjectFromAsync(project);
                else await ViewModel.ImportFilesAsync(startupFiles.Where(System.IO.File.Exists));
            }
            else await ViewModel.OfferRecoveryAsync();
        };
        Navigation=new(CanvasScroll,PaperStage,()=>ViewModel.IsSheetSelected ? SheetEditor : Editor,ViewModel);
        SizeChanged+=AdaptWorkspace;
        Closed+=(_,_)=>{ Navigation.Dispose(); ViewModel.Dispose(); };
        Closing+=WindowClosing;
        Editor.TextRequested+=EditText;
        Editor.BeforeGesture+=(_,_)=>ViewModel.FinishTextEditing();
        ViewModel.CropRequested+=OpenSourceImageCrop;
        Editor.SelectionChanged+=(_,_)=>{ ViewModel.SelectedSourceAnnotation=Editor.SelectedAnnotation; ViewModel.SourceImageSelected=Editor.WholeImageSelected; };
        SheetEditor.TextRequested+=EditSheetText;
        SheetEditor.SourceTextRequested+=EditText;
        SheetEditor.MovePreviewFailed+=message=>ViewModel.PreviewError="Предпросмотр перемещения недоступен: "+message;
        SheetEditor.BeforeGesture+=(_,_)=>ViewModel.FinishTextEditing();
        ViewModel.QueueSelectionUpdated+=SyncQueueSelection;
        ViewModel.PropertyChanged+=(_,e)=>
        {
            if(e.PropertyName is nameof(PrintSessionViewModel.SelectedPage) or nameof(PrintSessionViewModel.IsBusy)
                or nameof(PrintSessionViewModel.SelectedObjectIds) or nameof(PrintSessionViewModel.SelectedSourceAnnotation)
                or nameof(PrintSessionViewModel.SourceImageSelected) or nameof(PrintSessionViewModel.HighlightedPages)) CloseWorkMenus();
        };
        GotKeyboardFocus+=KeyboardFocusChanged; PreviewKeyDown+=ShortcutKeyDown;
        SheetEditor.SelectionRequested+=(_,request)=>
        {
            var ids=request.Toggle ? ViewModel.SelectedObjectIds.ToHashSet() : new HashSet<Guid>();
            if(request.Id is {} id) { if(!ids.Add(id) && request.Toggle) ids.Remove(id); }
            ViewModel.SetObjectSelection(ids,request.Id);
        };
        SyncQueueSelection(null,EventArgs.Empty);
    }
    private bool TextFieldHasFocus()=>CurrentShortcutScope==ShortcutScope.Text;
    private void ClipboardCopyCanExecute(object sender,CanExecuteRoutedEventArgs e)
    {
        var scope=ClipboardCommandScope(e.OriginalSource as DependencyObject); if(scope==ShortcutScope.Text) return;
        e.CanExecute=ViewModel!=null && (scope==ShortcutScope.Queue ? ViewModel.IsIdle && ViewModel.HighlightedPageCount>0 : ViewModel.CanCopySelection); e.Handled=true;
    }
    private void ClipboardPasteCanExecute(object sender,CanExecuteRoutedEventArgs e)
    {
        if(ClipboardCommandScope(e.OriginalSource as DependencyObject)==ShortcutScope.Text) return;
        e.CanExecute=ViewModel?.CanPasteClipboard==true; e.Handled=true;
    }
    private async void ClipboardCopyExecuted(object sender,ExecutedRoutedEventArgs e)
    {
        var scope=ClipboardCommandScope(e.OriginalSource as DependencyObject); if(scope==ShortcutScope.Text) return;
        e.Handled=true; await ViewModel.CopyContextAsync(scope==ShortcutScope.Queue);
    }
    private async void ClipboardPasteExecuted(object sender,ExecutedRoutedEventArgs e)
    {
        var scope=ClipboardCommandScope(e.OriginalSource as DependencyObject); if(scope==ShortcutScope.Text) return; e.Handled=true;
        var destination=scope==ShortcutScope.Queue ? PasteDestination.Queue : PasteDestination.Auto;
        await ViewModel.PasteClipboardCommand.ExecuteAsync(destination);
        if(destination==PasteDestination.Auto && ViewModel.CanEdit && !TextFieldHasFocus()
            && !(Keyboard.FocusedElement is DependencyObject current && Ancestor<ListBox>(current)==QueueList))
            (ViewModel.IsSheetSelected ? (FrameworkElement)SheetEditor : Editor).Focus();
    }
    private async void FilesDropped(object sender,DragEventArgs e)
    {
        ClearFileDropFeedback();
        if(e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var onCanvas=IsCanvasDropTarget(e.OriginalSource as DependencyObject);
            var anchor=onCanvas ? FileDropAnchor(e) : null;
            e.Handled=true; await ViewModel.HandleFileDropAsync(files,onCanvas,anchor);
            if(onCanvas && ViewModel.CanEdit && !TextFieldHasFocus()) (ViewModel.IsSheetSelected ? (FrameworkElement)SheetEditor : Editor).Focus();
        }
    }
    private void FilesDragOver(object sender,DragEventArgs e)
    {
        if(!e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var onCanvas=IsCanvasDropTarget(e.OriginalSource as DependencyObject);
        if(ViewModel.IsIdle) ShowFileDropFeedback(files,onCanvas,onCanvas ? FileDropAnchor(e) : null); else ClearFileDropFeedback();
        e.Effects=ViewModel.IsIdle ? DragDropEffects.Copy : DragDropEffects.None; e.Handled=true;
    }
    private async void QueueItemLoaded(object sender,RoutedEventArgs e)
    { if(sender is ListBoxItem { DataContext:PrintPageItem page }) await ViewModel.EnsureThumbnailAsync(page); }
    private void QueueMouseDown(object sender,MouseButtonEventArgs e)
    {
        _dragPage=null; _dragStart=e.GetPosition(QueueList);
        if(e.OriginalSource is not DependencyObject source) return;
        var current=source;
        while(current!=null && current!=QueueList)
        {
            if(current is System.Windows.Controls.Primitives.ButtonBase or TextBox) return;
            current=GetParentNode(current);
        }
        _dragPage=Ancestor<ListBoxItem>(source)?.DataContext as PrintPageItem;
        _dragPages=_dragPage!=null && ViewModel.HighlightedPages.Contains(_dragPage) ? ViewModel.HighlightedPages.ToArray() : _dragPage==null ? [] : [_dragPage];
    }
    private void QueueMouseMove(object sender,MouseEventArgs e)
    {
        if(_dragPage==null || e.LeftButton!=MouseButtonState.Pressed || !ViewModel.IsIdle) return;
        var point=e.GetPosition(QueueList);
        if(Math.Abs(point.X-_dragStart.X)<SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y-_dragStart.Y)<SystemParameters.MinimumVerticalDragDistance) return;
        _dragPage=null;
        DragDrop.DoDragDrop(QueueList,new DataObject("PrintMaster.MovePages",_dragPages),DragDropEffects.Move);
    }
    private void QueueDropped(object sender,DragEventArgs e)
    {
        if(!e.Data.GetDataPresent("PrintMaster.MovePages") || e.OriginalSource is not DependencyObject source) return;
        var target=Ancestor<ListBoxItem>(source)?.DataContext as PrintPageItem;
        if(target!=null && e.Data.GetData("PrintMaster.MovePages") is PrintPageItem[] pages) ViewModel.MoveQueueGroup(pages,target);
        e.Handled=true;
    }
    private static DependencyObject? GetParentNode(DependencyObject d)=>d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : (d as FrameworkContentElement)?.Parent;
    private static T? Ancestor<T>(DependencyObject? source) where T:DependencyObject
    { while(source!=null) { if(source is T found) return found; source=GetParentNode(source); } return null; }

    private void EditText(object? sender,TextEditRequest request)
    {
        ViewModel.BeginAnnotationTextEditing(request.Location,request.Existing,request.ContentId);
    }
    private void EditSheetText(object? sender,SheetTextEditRequest request)
    {
        if(!ViewModel.CanEdit || !ViewModel.IsSheetSelected) return;
        ViewModel.BeginTextEditing(request.Location,request.Existing);
    }
    private async void WindowClosing(object? sender,CancelEventArgs e)
    {
        if(!DialogInputValidation.CommitAndCheck(this))
        { ViewModel.Status="Исправьте числовое поле или нажмите в нем Esc перед закрытием."; e.Cancel=true; return; }
        if(ViewModel.IsClosePrepared && !ViewModel.IsBusy && _environmentClosePrepared) return;
        e.Cancel=true;
        if(_closingAfterCancel) return;
        _closingAfterCancel=true;
        try
        {
            await ViewModel.PrepareCloseAsync();
            if(!await ViewModel.SaveWorkspaceEnvironmentAsync(WindowEnvironment.Capture(this),CaptureWorkspacePanels()))
                ViewModel.ShowWorkspaceSaveError();
            _environmentClosePrepared=true;
            _=Dispatcher.BeginInvoke(Close);
        }
        catch { _closingAfterCancel=false; }
    }
    private void CommitFocusedField()
    {
        if(Keyboard.FocusedElement is not DependencyObject focus) return;
        if(Ancestor<NumericField>(focus) is {} number) number.Commit();
        else if(focus is TextBox text) text.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        Ancestor<ComboBox>(focus)?.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
    }
}
