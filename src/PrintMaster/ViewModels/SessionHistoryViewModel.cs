using PrintMaster.Models;
using System.Collections.Specialized;
using System.ComponentModel;

namespace PrintMaster.ViewModels;

public partial class PrintSessionViewModel
{
    // Only immutable logical states and owned material paths are retained, never print rasters.
    private sealed record HistoryPage(PrintPageItem Page,SheetSnapshot Sheet,int Copies,bool Included,PagePrintOptions? PrintOptions);
    private sealed record HistoryView(Guid? Active,Guid[] Queue,Guid[] Objects,Guid? Annotation,bool SourceImage);
    private sealed record HistoryState(HistoryPage[] Pages,HistoryView View);
    private sealed record HistoryFrame(HistoryState Before,HistoryState After,string Action);
    private sealed record HistoryOptions(ProjectPrintSettings Print,string Ink,double Stroke,double TextSize,double CropRatio,int ExportDpi,TextStyle TextStyle,
        double BrushOpacity,double MarkerThickness,double MarkerOpacity,double EraserDiameter);
    private readonly Stack<HistoryFrame> _historyUndo=[],_historyRedo=[];
    private readonly HashSet<PrintPageItem> _historyPages=[];
    private HistoryState _historyHead=null!;
    private HistoryState? _historySaved;
    private HistoryOptions? _historySavedOptions;
    private HistoryState? _historyBefore;
    private string? _historyAction;
    private int _historyDepth;
    private bool _applyingHistory;
    internal int UndoCount=>_historyUndo.Count;
    internal int RedoCount=>_historyRedo.Count;
    public string UndoDescription=>_historyUndo.TryPeek(out var frame) ? "Отменить: "+frame.Action+" (Ctrl+Z)" : "Отменить (Ctrl+Z)";
    public string RedoDescription=>_historyRedo.TryPeek(out var frame) ? "Повторить: "+frame.Action+" (Ctrl+Y)" : "Повторить (Ctrl+Y)";
    private HistoryView CaptureHistoryView()=>new(SelectedPage?.Id,_queueSelection.ToArray(),_selectedObjectIds.ToArray(),SelectedSourceAnnotation?.Id,SourceImageSelected);
    private HistoryState CaptureHistory()=>new(Pages.Select(p=>new HistoryPage(p,p.Document.Snapshot(),p.Copies,p.IsIncluded,p.PrintOptions)).ToArray(),CaptureHistoryView());
    private HistoryOptions CaptureHistoryOptions()=>new(ProjectPrintSettings.Capture(Settings),InkColor,StrokeThickness,TextSize,CropRatio,ExportDpi,_defaultTextStyle,
        BrushOpacity,MarkerThickness,MarkerOpacity,EraserDiameter);
    private void SaveHistoryCheckpoint() { _historySaved=CaptureHistory(); _historySavedOptions=CaptureHistoryOptions(); }
    private bool AtHistoryCheckpoint()=>_historySaved!=null && CaptureHistoryOptions()==_historySavedOptions && SameHistory(CaptureHistory(),_historySaved);
    private void RefreshHistoryDirty()=>HasUnsavedChanges=!AtHistoryCheckpoint();
    private void InitializeHistory()
    {
        _historyHead=CaptureHistory(); SaveHistoryCheckpoint(); Pages.CollectionChanged+=HistoryQueueChanged;
    }
    private void HistoryQueueChanged(object? sender,NotifyCollectionChangedEventArgs e)
    {
        var removed=e.Action==NotifyCollectionChangedAction.Reset ? _historyPages.ToArray() : e.Action==NotifyCollectionChangedAction.Move ? [] : e.OldItems?.OfType<PrintPageItem>().ToArray() ?? [];
        foreach(var old in removed)
        {
            old.Document.Changed-=HistoryDocumentChanged; old.PropertyChanged-=HistoryPageChanged;
            old.Thumbnail=null; _historyPages.Remove(old);
        }
        var added=e.Action==NotifyCollectionChangedAction.Reset ? Pages.ToArray() : e.Action==NotifyCollectionChangedAction.Move ? [] : e.NewItems?.OfType<PrintPageItem>().ToArray() ?? [];
        foreach(var page in added.Where(p=>!_historyPages.Contains(p)))
        {
            page.Document.Changed+=HistoryDocumentChanged; page.PropertyChanged+=HistoryPageChanged; _historyPages.Add(page);
        }
        RecordHistory(e.Action==NotifyCollectionChangedAction.Add ? "Добавить страницы" : e.Action==NotifyCollectionChangedAction.Move ? "Переставить страницы" : "Удалить страницы",e.Action==NotifyCollectionChangedAction.Add ? added.LastOrDefault()?.Id : null);
    }
    private void HistoryDocumentChanged(object? sender,SheetChange change)
    {
        var page=Pages.FirstOrDefault(p=>p.Document==sender);
        RecordHistory(change.Action,page?.Id);
    }
    private void HistoryPageChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName is not (nameof(PrintPageItem.Copies) or nameof(PrintPageItem.IsIncluded) or nameof(PrintPageItem.PrintOptions))) return;
        // The generated Copies setter clamps recursively before PropertyChanged is raised.
        RecordHistory(e.PropertyName==nameof(PrintPageItem.Copies) ? "Количество копий" : e.PropertyName==nameof(PrintPageItem.PrintOptions) ? "Настройки печати страницы" : "Галочки печати",(sender as PrintPageItem)?.Id);
    }
    private static bool SameSheet(SheetSnapshot a,SheetSnapshot b)
    {
        if(a==b) return true;
        if(a.Id!=b.Id || a.Paper!=b.Paper || a.PrimaryContentId!=b.PrimaryContentId || !a.Assets.SequenceEqual(b.Assets) || a.Objects.Length!=b.Objects.Length) return false;
        for(var i=0;i<a.Objects.Length;i++)
        {
            if(a.Objects[i]==b.Objects[i]) continue;
            if(a.Objects[i] is not ContentObject x || b.Objects[i] is not ContentObject y) return false;
            if(x with { Edits=y.Edits }!=y || x.Edits.Crop!=y.Edits.Crop || x.Edits.QuarterTurns!=y.Edits.QuarterTurns || x.Edits.Annotations.Length!=y.Edits.Annotations.Length) return false;
            for(var j=0;j<x.Edits.Annotations.Length;j++)
            {
                var first=x.Edits.Annotations[j]; var second=y.Edits.Annotations[j];
                if(first with { Points=second.Points }!=second || !first.Points.SequenceEqual(second.Points)) return false;
            }
        }
        return true;
    }
    private static bool SameHistory(HistoryState a,HistoryState b)
    {
        if(a.Pages.Length!=b.Pages.Length) return false;
        for(var i=0;i<a.Pages.Length;i++)
        {
            var x=a.Pages[i]; var y=b.Pages[i];
            if(x.Page.Id!=y.Page.Id || x.Copies!=y.Copies || x.Included!=y.Included || x.PrintOptions!=y.PrintOptions || !SameSheet(x.Sheet,y.Sheet)) return false;
        }
        return true;
    }
    private void RecordHistory(string action,Guid? focus=null)
    {
        if(_historyHead==null || _applyingHistory || _restoringProject || _historyDepth>0 || _disposed) return;
        var current=CaptureHistory();
        if(SameHistory(_historyHead,current)) { _historyHead=current; return; }
        var before=_historyHead;
        if(focus!=null)
        {
            if(before.Pages.Any(p=>p.Page.Id==focus)) before=FocusHistory(before,focus.Value);
            current=FocusHistory(current,focus.Value);
        }
        PushHistory(before,current,action); _historyHead=CaptureHistory();
    }
    private static HistoryState FocusHistory(HistoryState state,Guid id)=>state.View.Active==id ? state : state with { View=new(id,[id],[],null,false) };
    private void PushHistory(HistoryState before,HistoryState after,string action)
    {
        _historyUndo.Push(new(before,after,action)); _historyRedo.Clear();
        if(_historyUndo.Count>100) { var kept=_historyUndo.Take(100).Reverse().ToArray(); _historyUndo.Clear(); foreach(var frame in kept) _historyUndo.Push(frame); }
        NotifyHistory();
    }
    private void RefreshHistoryView()
    {
        if(_historyHead==null || _applyingHistory || _restoringProject || _historyDepth>0) return;
        var current=CaptureHistory(); if(!SameHistory(_historyHead,current)) return;
        _historyHead=current;
        if(_historyUndo.TryPeek(out var last) && last.After.View.Active==current.View.Active && SameHistory(last.After,current))
        {
            _historyUndo.Pop(); _historyUndo.Push(last with { After=last.After with { View=current.View } });
        }
    }
    private IDisposable BeginHistoryEdit(string action)
    {
        if(_historyDepth++==0) { _historyBefore=CaptureHistory(); _historyAction=action; }
        return new HistoryEdit(this);
    }
    private sealed class HistoryEdit(PrintSessionViewModel owner) : IDisposable
    {
        private PrintSessionViewModel? _owner=owner;
        public void Dispose() { var vm=Interlocked.Exchange(ref _owner,null); vm?.EndHistoryEdit(); }
    }
    private void EndHistoryEdit()
    {
        if(--_historyDepth!=0) return;
        var before=_historyBefore!; var after=CaptureHistory(); var action=_historyAction!;
        _historyBefore=null; _historyAction=null;
        if(!_applyingHistory && !_restoringProject && !_disposed && !SameHistory(before,after)) PushHistory(before,after,action);
        _historyHead=after; NotifyHistory();
        if(!_restoringProject && !_disposed) RefreshHistoryDirty();
    }
    private void RestoreHistory(HistoryState state)
    {
        var previousPaper=(Settings.Paper,Settings.CustomWidthMm,Settings.CustomHeightMm,Settings.Landscape);
        _applyingHistory=true; _applyingQueueFrame=true;
        try
        {
            _previewCancellation?.Cancel();
            var reorder=!Pages.SequenceEqual(state.Pages.Select(p=>p.Page));
            if(reorder)
            {
                foreach(var page in Pages) { page.PropertyChanged-=PageChanged; page.Edits.PropertyChanged-=EditsChanged; }
                Pages.Clear();
            }
            foreach(var entry in state.Pages)
            {
                if(!SameSheet(entry.Page.Document.Snapshot(),entry.Sheet)) { entry.Page.Document.RestoreHistoryState(entry.Sheet); entry.Page.Thumbnail=null; }
                entry.Page.Copies=entry.Copies; entry.Page.IsIncluded=entry.Included; entry.Page.PrintOptions=entry.PrintOptions;
                if(reorder) { entry.Page.PropertyChanged+=PageChanged; entry.Page.Edits.PropertyChanged+=EditsChanged; Pages.Add(entry.Page); }
            }
            SelectedPage=Pages.FirstOrDefault(p=>p.Id==state.View.Active) ?? Pages.FirstOrDefault();
            SetQueueSelection(Pages.Where(p=>state.View.Queue.Contains(p.Id)),SelectedPage);
            SetObjectSelection(state.View.Objects);
            SelectedSourceAnnotation=SelectedPage?.Edits.Annotations.FirstOrDefault(a=>a.Id==state.View.Annotation);
            SourceImageSelected=state.View.SourceImage && IsImageSelected;
            SyncLayoutToSheet(); NotifyQueue(); NotifySheetMode(); NotifyPageOptions(); MarkProjectChanged();
            foreach(var property in new[] { nameof(HasSelection),nameof(IsImageSelected),nameof(IsDocumentSelected),nameof(CanEdit),nameof(ShowEditorTools),nameof(CanvasHint),nameof(HasFillWarning),nameof(IsTextMode),nameof(CanvasWidth),nameof(CanvasHeight),nameof(CanvasSafeArea),nameof(PaperSummary) }) OnPropertyChanged(property);
            _=LoadEditorAsync(SelectedPage);
        }
        finally { _applyingHistory=false; _applyingQueueFrame=false; }
        _historyHead=CaptureHistory(); NotifyQueueSelection(); NotifyHistory();
        if(previousPaper!=(Settings.Paper,Settings.CustomWidthMm,Settings.CustomHeightMm,Settings.Landscape)) _=ProbePrinterAsync();
    }
    private void UndoHistory()
    {
        if(!CanUndo() || !_historyUndo.TryPop(out var frame)) return;
        RestoreHistory(frame.Before); _historyRedo.Push(frame); Status="Отменено: "+frame.Action; NotifyHistory();
    }
    private void RedoHistory()
    {
        if(!CanRedo() || !_historyRedo.TryPop(out var frame)) return;
        RestoreHistory(frame.After); _historyUndo.Push(frame); Status="Повторено: "+frame.Action; NotifyHistory();
    }
    private void ResetHistory()
    {
        _historyUndo.Clear(); _historyRedo.Clear(); _historyHead=CaptureHistory(); NotifyHistory();
    }
    private void NotifyHistory()
    {
        UndoCommand.NotifyCanExecuteChanged(); RedoCommand.NotifyCanExecuteChanged();
        UndoQueueCommand.NotifyCanExecuteChanged(); RedoQueueCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(UndoDescription)); OnPropertyChanged(nameof(RedoDescription));
    }
    private void DisposeHistory()
    {
        Pages.CollectionChanged-=HistoryQueueChanged;
        foreach(var page in _historyPages)
        {
            page.Document.Changed-=HistoryDocumentChanged; page.PropertyChanged-=HistoryPageChanged;
            page.PropertyChanged-=PageChanged; page.Edits.PropertyChanged-=EditsChanged;
        }
        _historyPages.Clear(); _historyUndo.Clear(); _historyRedo.Clear(); _historyHead=null!;
        _historyBefore=null; _textHistoryEdit=null; _historySaved=null; _historySavedOptions=null;
    }
}
