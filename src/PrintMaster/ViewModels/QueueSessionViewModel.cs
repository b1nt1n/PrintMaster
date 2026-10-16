using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrintMaster.Models;
using PrintMaster.Services;
using System.Collections.Immutable;

namespace PrintMaster.ViewModels;

public partial class PrintSessionViewModel
{
    private readonly HashSet<Guid> _queueSelection=[];
    private bool _settingQueueSelection,_applyingQueueFrame,_editingQueue;
    public bool IsQueueMutation=>_applyingQueueFrame || _editingQueue;
    public IReadOnlyList<PrintPageItem> HighlightedPages=>Pages.Where(p=>_queueSelection.Contains(p.Id)).ToArray();
    public int HighlightedPageCount=>HighlightedPages.Count;
    public string HighlightedSummary=>$"Выделено: {HighlightedPageCount} · к печати: {SelectedCount}";
    public event EventHandler? QueueSelectionUpdated;
    [ObservableProperty] private int batchCopies=1;

    public void SetQueueSelection(IEnumerable<PrintPageItem> selected,PrintPageItem? active=null)
    {
        var ids=selected.Select(p=>p.Id).ToHashSet(); _settingQueueSelection=true;
        try
        {
            if(active!=null && Pages.Any(p=>p.Id==active.Id)) SelectedPage=Pages.First(p=>p.Id==active.Id);
            _queueSelection.Clear(); foreach(var page in Pages.Where(p=>ids.Contains(p.Id))) _queueSelection.Add(page.Id);
        }
        finally { _settingQueueSelection=false; }
        NotifyQueueSelection();
    }
    private void TrackActiveQueuePage(PrintPageItem? page)
    {
        if(_settingQueueSelection || _applyingQueueFrame) return;
        _queueSelection.Clear(); if(page!=null) _queueSelection.Add(page.Id); NotifyQueueSelection();
    }
    private void NotifyQueueSelection()
    {
        _queueSelection.RemoveWhere(id=>!Pages.Any(p=>p.Id==id));
        foreach(var name in new[] { nameof(HighlightedPages),nameof(HighlightedPageCount),nameof(HighlightedSummary),nameof(HasMultipleHighlightedPages) }) OnPropertyChanged(name);
        DeleteHighlightedPagesCommand.NotifyCanExecuteChanged(); DuplicateHighlightedPagesCommand.NotifyCanExecuteChanged();
        RotateHighlightedPagesCommand.NotifyCanExecuteChanged(); ToggleHighlightedPrintingCommand.NotifyCanExecuteChanged();
        SetHighlightedPrintingCommand.NotifyCanExecuteChanged();
        SetHighlightedCopiesCommand.NotifyCanExecuteChanged(); MoveHighlightedPagesCommand.NotifyCanExecuteChanged();
        UndoQueueCommand.NotifyCanExecuteChanged(); RedoQueueCommand.NotifyCanExecuteChanged();
        QueueSelectionUpdated?.Invoke(this,EventArgs.Empty);
        RefreshHistoryView();
    }
    private bool HasHighlightedPages()=>IsIdle && HighlightedPageCount>0;
    [RelayCommand] private void SelectQueuePages() { if(IsIdle) SetQueueSelection(Pages,SelectedPage); }
    [RelayCommand] private void ClearQueueSelection()=>SetQueueSelection([]);
    [RelayCommand(CanExecute=nameof(HasHighlightedPages))] private void DeleteHighlightedPages()=>QueueEdit(HighlightedPages,()=>
    {
        var selected=HighlightedPages.ToArray(); var at=Pages.IndexOf(selected[0]);
        foreach(var page in selected) { page.PropertyChanged-=PageChanged; page.Edits.PropertyChanged-=EditsChanged; Pages.Remove(page); }
        SelectedPage=Pages.ElementAtOrDefault(Math.Min(at,Pages.Count-1));
        SetQueueSelection(SelectedPage==null ? [] : [SelectedPage]);
    },label:"Удалить выделенные страницы");
    [RelayCommand(CanExecute=nameof(HasHighlightedPages))] private void ToggleHighlightedPrinting()=>QueueEdit(HighlightedPages,()=>
    {
        var include=HighlightedPages.Any(p=>!p.IsIncluded); foreach(var page in HighlightedPages) page.IsIncluded=include;
    },label:"Галочки выделенных страниц");
    [RelayCommand(CanExecute=nameof(HasHighlightedPages))] private void SetHighlightedCopies()
    {
        if(!CommitInputForAction()) return;
        if(BatchCopies is <1 or >999) { Status="Количество копий: целое число от 1 до 999."; return; }
        QueueEdit(HighlightedPages,()=>{ foreach(var page in HighlightedPages) page.Copies=BatchCopies; },label:"Копии выделенных страниц");
    }
    [RelayCommand(CanExecute=nameof(HasHighlightedPages))] private void SetHighlightedPrinting(bool included)=>QueueEdit(HighlightedPages,()=>
    { foreach(var page in HighlightedPages) page.IsIncluded=included; },label:included ? "Включить выделенные страницы в печать" : "Исключить выделенные страницы из печати");
    [RelayCommand(CanExecute=nameof(HasHighlightedPages))] private void RotateHighlightedPages()
    {
        var pages=HighlightedPages.ToArray();
        QueueEdit(pages,()=>
        {
            foreach(var page in HighlightedPages)
            {
                if(!page.IsSheetLayout) { page.RotationQuarterTurns=(page.RotationQuarterTurns+1)%4; continue; }
                var size=page.Document.Paper.OrientedSizeMm; var cx=size.Width/2; var cy=size.Height/2;
                page.Document.ReplaceObjects(page.Document.Objects.Where(o=>!o.Locked).Select(o=>
                {
                    var b=o.Placement.BoundsMm; var x=b.X+b.Width/2; var y=b.Y+b.Height/2;
                    var placement=o.Placement with { BoundsMm=b with { X=cx-(y-cy)-b.Width/2,Y=cy+(x-cx)-b.Height/2 },RotationDegrees=(o.Placement.RotationDegrees+90)%360 };
                    return o with { Placement=placement };
                }),"Повернуть содержимое страницы");
            }
        },true);
    }
    private bool CanMoveHighlightedPages(int direction)
    {
        if(!HasHighlightedPages() || direction is not (-1 or 1)) return false;
        for(var i=0;i<Pages.Count;i++)
        {
            var adjacent=i+direction;
            if(adjacent>=0 && adjacent<Pages.Count && _queueSelection.Contains(Pages[i].Id) && !_queueSelection.Contains(Pages[adjacent].Id)) return true;
        }
        return false;
    }
    [RelayCommand(CanExecute=nameof(CanMoveHighlightedPages))] private void MoveHighlightedPages(int direction)=>QueueEdit(HighlightedPages,()=>
    {
        if(direction<0)
        { for(var i=1;i<Pages.Count;i++) if(_queueSelection.Contains(Pages[i].Id) && !_queueSelection.Contains(Pages[i-1].Id)) Pages.Move(i,i-1); }
        else
        { for(var i=Pages.Count-2;i>=0;i--) if(_queueSelection.Contains(Pages[i].Id) && !_queueSelection.Contains(Pages[i+1].Id)) Pages.Move(i,i+1); }
    },label:"Переставить выделенные страницы");
    [RelayCommand(CanExecute=nameof(HasHighlightedPages))] private void DuplicateHighlightedPages()=>QueueEdit(HighlightedPages,()=>
    {
        if(Pages.Count+HighlightedPageCount>20000) { Status="В очереди не может быть больше 20 000 страниц."; return; }
        var originals=HighlightedPages.ToArray(); var copies=new List<PrintPageItem>();
        var markdownCopies=originals.Any(p=>p.Document.Objects.OfType<MarkdownObject>().Any())
            ? MarkdownGraphCloner.ClonePages(originals.Select(p=>new ProjectPage(p.Document.Snapshot(),p.IsIncluded,p.Copies) {PrintOptions=p.PrintOptions}).ToArray()) : default;
        var copyIndex=0;
        foreach(var page in originals)
        {
            var copy=markdownCopies.IsDefault ? CloneQueuePage(page) : new PrintPageItem(SheetDocument.Restore(markdownCopies[copyIndex].Sheet)){Copies=page.Copies,IsIncluded=page.IsIncluded,PrintOptions=page.PrintOptions};copyIndex++;var at=Pages.IndexOf(page)+1;
            copy.PropertyChanged+=PageChanged; copy.Edits.PropertyChanged+=EditsChanged; Pages.Insert(at,copy); copies.Add(copy);
        }
        SetQueueSelection(copies,copies.FirstOrDefault());
    },label:"Дублировать выделенные страницы");
    public void MoveQueueGroup(IReadOnlyCollection<PrintPageItem> selected,PrintPageItem target)
    {
        if(!IsIdle) return;
        var selectedIds=selected.Select(p=>p.Id).ToHashSet();var targetId=target.Id;
        if(!Pages.Any(p=>p.Id==targetId))return;
        var group=Pages.Where(p=>selectedIds.Contains(p.Id)).ToArray();if(group.Length==0 || group.Any(p=>p.Id==targetId))return;
        var groupIds=group.Select(p=>p.Id).ToHashSet();
        QueueEdit(group,()=>
        {
            // Finishing a flow draft replaces page models. Only move the live models,
            // otherwise an old page can be inserted beside its replacement with the same ID.
            var live=Pages.Where(p=>groupIds.Contains(p.Id)).ToArray();var liveTarget=Pages.FirstOrDefault(p=>p.Id==targetId);
            if(live.Length==0 || liveTarget==null || live.Contains(liveTarget))return;
            var after=Pages.IndexOf(live[0])<Pages.IndexOf(liveTarget);
            foreach(var page in live) Pages.Remove(page);
            var at=Pages.IndexOf(liveTarget)+(after ? 1 : 0);
            foreach(var page in live) Pages.Insert(at++,page);
            SetQueueSelection(live,live.First());
        },label:"Переставить группу страниц");
    }
    private static PrintPageItem CloneQueuePage(PrintPageItem page)
    {
        var cloned=MarkdownGraphCloner.ClonePages([new(page.Document.Snapshot(),page.IsIncluded,page.Copies){PrintOptions=page.PrintOptions}]).Single();
        return new(SheetDocument.Restore(cloned.Sheet)){Copies=cloned.Copies,IsIncluded=cloned.IsIncluded,PrintOptions=cloned.PrintOptions};
    }
    private void QueueEdit(IEnumerable<PrintPageItem> affected,Action action,bool rotation=false,string? label=null)
    {
        if(!IsIdle) return;
        var selectedIds=affected.Select(p=>p.Id).ToHashSet(); FinishTextEditing();
        if(selectedIds.Count>0)
        {
            SetQueueSelection(Pages.Where(p=>selectedIds.Contains(p.Id)),SelectedPage);
            if(HighlightedPageCount==0) return;
        }
        using var history=BeginHistoryEdit(label ?? (rotation ? "Повернуть выделенные страницы" : "Изменить выделенные страницы"));
        _editingQueue=true; try { action(); } finally { _editingQueue=false; }
        NotifyQueue(); MarkProjectChanged(); NotifyQueueSelection();
    }
    private bool CanUndoQueue()=>CanUndo();
    private bool CanRedoQueue()=>CanRedo();
    [RelayCommand(CanExecute=nameof(CanUndoQueue))] private void UndoQueue()=>Undo();
    [RelayCommand(CanExecute=nameof(CanRedoQueue))] private void RedoQueue()=>Redo();
    private void ResetQueueHistory()=>ResetHistory();
}
