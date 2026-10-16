using PrintMaster.Models;
using PrintMaster.ViewModels;
using System.Collections.Immutable;
using Xunit;

namespace PrintMaster.Tests;

public sealed class SelectionTests
{
    private static PrintSessionViewModel Session(out ClipboardTests.TestDialogs dialogs,out FakeClipboard clipboard)
    {
        dialogs=new(); clipboard=new(); return ClipboardTests.CreateVm(dialogs,clipboard);
    }
    private static PrintPageItem[] Pages(PrintSessionViewModel vm,int count=4)
    {
        var pages=Enumerable.Range(0,count).Select(i=>PrintPageItem.CreateBlank(SheetPaper.A4)).ToArray();
        foreach(var page in pages) vm.AddPage(page);
        return pages;
    }
    [Fact]
    public void HighlightingDoesNotChangePrintingAndBatchValuesUndoTogether()
    {
        using var vm=Session(out _,out _); var pages=Pages(vm);
        pages[1].IsIncluded=false; pages[0].Copies=3;
        vm.SelectQueuePagesCommand.Execute(null);
        Assert.Equal(pages,vm.HighlightedPages); Assert.Equal(3,vm.SelectedCount); Assert.Equal(5,vm.Impressions);
        vm.SetQueueSelection([pages[0],pages[1]],pages[1]); vm.BatchCopies=8; vm.SetHighlightedCopiesCommand.Execute(null);
        Assert.Equal(new[] {8,8,1,1},pages.Select(p=>p.Copies));
        vm.ToggleHighlightedPrintingCommand.Execute(null); Assert.All(pages,p=>Assert.True(p.IsIncluded));
        vm.UndoQueueCommand.Execute(null); Assert.False(pages[1].IsIncluded); Assert.Equal(8,pages[1].Copies);
        vm.UndoQueueCommand.Execute(null); Assert.Equal(new[] {3,1,1,1},pages.Select(p=>p.Copies));
        vm.RedoQueueCommand.Execute(null); Assert.Equal(8,pages[0].Copies); Assert.Equal(8,pages[1].Copies); Assert.False(pages[1].IsIncluded);
        vm.ClearQueueSelectionCommand.Execute(null); Assert.Empty(vm.HighlightedPages); Assert.False(vm.DeleteHighlightedPagesCommand.CanExecute(null));
    }
    [Fact]
    public void DeletingEveryPageRestoresOrderIdentityCopiesAndSelectionWithOneUndo()
    {
        using var vm=Session(out _,out _); var pages=Pages(vm);
        pages[1].Copies=7; pages[2].IsIncluded=false; vm.SetQueueSelection(pages,pages[2]);
        vm.DeleteHighlightedPagesCommand.Execute(null);
        Assert.Empty(vm.Pages); Assert.Null(vm.SelectedPage); Assert.Equal(0,vm.Impressions);
        Assert.True(vm.UndoQueueCommand.CanExecute(null)); vm.UndoQueueCommand.Execute(null);
        Assert.Equal(pages,vm.Pages); Assert.Same(pages[2],vm.SelectedPage); Assert.Equal(pages,vm.HighlightedPages);
        Assert.Equal(7,pages[1].Copies); Assert.False(pages[2].IsIncluded);
        vm.RedoQueueCommand.Execute(null); Assert.Empty(vm.Pages); vm.UndoQueueCommand.Execute(null); Assert.Equal(pages,vm.Pages);
    }
    [Fact]
    public void MovingNonContiguousSelectionPreservesItsRelativeOrder()
    {
        using var vm=Session(out _,out _); var p=Pages(vm,5); vm.SetQueueSelection([p[1],p[3]],p[3]);
        vm.MoveHighlightedPagesCommand.Execute(-1);
        Assert.Equal(new[] {p[1],p[0],p[3],p[2],p[4]},vm.Pages); Assert.Equal(new[] {p[1],p[3]},vm.HighlightedPages);
        vm.UndoQueueCommand.Execute(null); Assert.Equal(p,vm.Pages);
        vm.MoveHighlightedPagesCommand.Execute(1); Assert.Equal(new[] {p[0],p[2],p[1],p[4],p[3]},vm.Pages);
        vm.UndoQueueCommand.Execute(null); Assert.Equal(p,vm.Pages);
    }
    [Fact]
    public void QueueDuplicateHasIndependentIdsAndDetachesTextFlow()
    {
        using var vm=Session(out _,out _); var p=Pages(vm,2); p[0].Copies=4; p[0].IsIncluded=false;
        var text=new TextObject(Guid.NewGuid(),new(new(12,18,80,30)),"копия",new("Arial",16,"#175E9C",true),Flow:new(Guid.NewGuid(),0,false));
        p[0].Document.AddObject(text); vm.SetQueueSelection([p[0]]); vm.DuplicateHighlightedPagesCommand.Execute(null);
        var copy=vm.Pages[1]; var copied=Assert.IsType<TextObject>(Assert.Single(copy.Document.Objects));
        Assert.NotEqual(p[0].Id,copy.Id); Assert.NotEqual(text.Id,copied.Id); Assert.Null(copied.Flow);
        Assert.Equal(text.Text,copied.Text); Assert.Equal(text.Style,copied.Style); Assert.Equal(4,copy.Copies); Assert.False(copy.IsIncluded);
        copy.Document.MoveObject(copied.Id,6,3); Assert.Equal(12,text.Placement.BoundsMm.X);
        vm.UndoQueueCommand.Execute(null); Assert.Same(copy,vm.Pages[1]); Assert.Equal(copied.Placement,copy.Document.Objects.Single().Placement);
        vm.UndoQueueCommand.Execute(null); Assert.Equal(p,vm.Pages);
        vm.RedoQueueCommand.Execute(null); Assert.Same(copy,vm.Pages[1]); vm.RedoQueueCommand.Execute(null);
        Assert.Equal(copied.Placement.BoundsMm.X+6,copy.Document.Objects.Single().Placement.BoundsMm.X);
    }
    [Fact]
    public void GroupRotationSkipsLockedAndEmptyPagesWithoutUndoingTheirEarlierEdits()
    {
        using var vm=Session(out _,out _); var p=Pages(vm,3);
        var locked=new ShapeObject(Guid.NewGuid(),new(new(10,20,30,40)),SheetShapeKind.Rectangle,new(),Locked:true);
        p[0].Document.AddObject(locked);
        var shape=locked with { Id=Guid.NewGuid(),Locked=false }; p[1].Document.AddObject(shape);
        var before=p.Select(page=>page.Document.Snapshot()).ToArray(); vm.SetQueueSelection(p);
        vm.RotateHighlightedPagesCommand.Execute(null);
        Assert.Equal(90,p[1].Document.Objects.Single().Placement.RotationDegrees); Assert.Equal(before[0],p[0].Document.Snapshot()); Assert.Empty(p[2].Document.Objects);
        vm.UndoQueueCommand.Execute(null);
        Assert.Equal(before,p.Select(page=>page.Document.Snapshot())); Assert.True(p[0].Document.CanUndo);
        vm.RedoQueueCommand.Execute(null); Assert.Equal(90,p[1].Document.Objects.Single().Placement.RotationDegrees); Assert.Equal(locked,p[0].Document.Objects.Single());
    }
    [Fact]
    public void ObjectSelectionMovesDuplicatesAndDeletesAtomicallyWithoutTouchingLockedOrHiddenObjects()
    {
        using var vm=Session(out _,out _); var page=Pages(vm,1)[0];
        var a=new ShapeObject(Guid.NewGuid(),new(new(10,20,30,40)),SheetShapeKind.Arrow,new("#175E9C",0.8));
        var b=new TextObject(Guid.NewGuid(),new(new(60,70,80,30)),"Выделение",new("Arial",14));
        var locked=a with { Id=Guid.NewGuid(),Locked=true }; var hidden=a with { Id=Guid.NewGuid(),Visible=false };
        page.Document.AddObjects([a,b,locked,hidden]); vm.SelectAllSheetObjects(); Assert.Equal(new[] {a.Id,b.Id},vm.SelectedObjectIds);
        vm.MoveSheetSelection(1,-10); Assert.Equal(new RectD(11,10,30,40),page.Document.Objects[0].Placement.BoundsMm); Assert.Equal(new RectD(61,60,80,30),page.Document.Objects[1].Placement.BoundsMm);
        vm.UndoCommand.Execute(null); Assert.Equal(new SheetObject[] {a,b,locked,hidden},page.Document.Objects);
        vm.DuplicateSheetSelection(); var duplicates=vm.SelectedObjects.ToArray(); Assert.Equal(6,page.Document.Objects.Count);
        Assert.Equal(13,duplicates[0].Placement.BoundsMm.X); Assert.Equal(63,duplicates[1].Placement.BoundsMm.X);
        Assert.DoesNotContain(duplicates,o=>o.Id==a.Id || o.Id==b.Id); Assert.Null(vm.SelectedSheetObject); // Single-object inspector must not edit a random member.
        vm.UndoCommand.Execute(null); Assert.Equal(new SheetObject[] {a,b,locked,hidden},page.Document.Objects);
        vm.SelectAllSheetObjects(); vm.DeleteSheetSelection(); Assert.Equal(new SheetObject[] {locked,hidden},page.Document.Objects);
        vm.UndoCommand.Execute(null); Assert.Equal(new SheetObject[] {a,b,locked,hidden},page.Document.Objects);
    }
    [Fact]
    public void BusySessionRejectsDestructiveBatchCommands()
    {
        using var vm=Session(out _,out _); var p=Pages(vm); vm.SetQueueSelection(p); vm.IsBusy=true;
        Assert.False(vm.DeleteHighlightedPagesCommand.CanExecute(null)); Assert.False(vm.DuplicateHighlightedPagesCommand.CanExecute(null));
        vm.DeleteHighlightedPagesCommand.Execute(null); Assert.Equal(p,vm.Pages); vm.IsBusy=false;
        Assert.True(vm.DeleteHighlightedPagesCommand.CanExecute(null));
    }
    [Fact]
    public void DroppingAGroupMovesItTogetherAndUndoRestoresItsOriginalPositions()
    {
        using var vm=Session(out _,out _); var p=Pages(vm,5); vm.SetQueueSelection([p[0],p[2]],p[0]);
        vm.MoveQueueGroup([p[0],p[2]],p[4]); Assert.Equal(new[] {p[1],p[3],p[4],p[0],p[2]},vm.Pages);
        Assert.Equal(new[] {p[0],p[2]},vm.HighlightedPages); vm.UndoQueueCommand.Execute(null); Assert.Equal(p,vm.Pages);
        vm.MoveQueueGroup([p[0],p[2]],p[2]); Assert.Equal(p,vm.Pages); // A drop into the selected group must be a no-op.
    }
    [Fact]
    public async Task DuplicatingOriginalPhotoMakesAnEditableCopyOnSameSheetAndUndoesInOneStep()
    {
        using var vm=Session(out var dialogs,out _); await vm.ImportFilesAsync([Fixtures.CreateImage("ux002-duplicate-background.png")]);
        var page=vm.SelectedPage!; page.Edits.SetCrop(new(120,90,700,600)); page.RotationQuarterTurns=3;
        var annotation=Annotation.Create(AnnotationKind.Arrow,new(200,250),new(400,450),"#175E9C",4); page.Edits.Add(annotation);
        var before=page.Document.Snapshot(); vm.DuplicateSourceImage();
        Assert.Single(vm.Pages); Assert.True(page.IsSheetLayout); Assert.Equal(2,page.Document.Objects.Count);
        var copy=Assert.IsType<ContentObject>(page.Document.Objects[1]); Assert.NotEqual(before.PrimaryContentId,copy.Id);
        Assert.Equal(new RectD(120,90,700,600),copy.Edits.Crop); Assert.Equal(3,copy.Edits.QuarterTurns);
        Assert.NotEqual(annotation.Id,copy.Edits.Annotations.Single().Id); Assert.Equal(annotation.Start,copy.Edits.Annotations.Single().Start);
        Assert.Single(page.Document.Snapshot().Assets); Assert.Equal(before.PrimaryContent!.AssetId,copy.AssetId);
        vm.UndoCommand.Execute(null); Assert.Equal(before,page.Document.Snapshot()); Assert.False(page.IsSheetLayout); Assert.Empty(dialogs.Errors);
    }
}
