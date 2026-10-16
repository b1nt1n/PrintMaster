using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.IO;
using System.Security.Cryptography;
using Xunit;

namespace PrintMaster.Tests;

public sealed class SessionHistoryTests
{
    private static PrintSessionViewModel Session()=>ClipboardTests.CreateVm(new(),new());
    private static ShapeObject Rectangle(double x=10)=>new(Guid.NewGuid(),new(new(x,20,30,40)),SheetShapeKind.Rectangle,new());
    [Fact]
    public void AddingFirstPageCanUndoToEmptyQueueAndRedoSamePage()
    {
        using var vm=Session(); var page=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(page);
        Assert.True(vm.UndoCommand.CanExecute(null)); vm.UndoCommand.Execute(null);
        Assert.Empty(vm.Pages); Assert.Null(vm.SelectedPage); Assert.Equal(0,vm.Impressions);
        Assert.True(vm.RedoCommand.CanExecute(null)); vm.RedoCommand.Execute(null);
        Assert.Same(page,Assert.Single(vm.Pages)); Assert.Same(page,vm.SelectedPage);
    }
    [Fact]
    public void UndoAfterChangingActivePageStillTargetsMostRecentEdit()
    {
        using var vm=Session(); var a=PrintPageItem.CreateBlank(SheetPaper.A4); var b=PrintPageItem.CreateBlank(SheetPaper.A4);
        vm.AddPage(a); vm.AddPage(b);
        var first=Rectangle(); var last=Rectangle(60); a.Document.AddObject(first); b.Document.AddObject(last);
        vm.SelectedPage=a; vm.UndoCommand.Execute(null);
        Assert.Empty(b.Document.Objects); Assert.Equal(first,Assert.Single(a.Document.Objects)); Assert.Same(b,vm.SelectedPage);
        vm.UndoCommand.Execute(null); Assert.Empty(a.Document.Objects); Assert.Same(a,vm.SelectedPage);
        vm.RedoCommand.Execute(null); Assert.Equal(first,Assert.Single(a.Document.Objects));
        vm.RedoCommand.Execute(null); Assert.Equal(last,Assert.Single(b.Document.Objects)); Assert.Same(b,vm.SelectedPage);
    }
    [Fact]
    public void SinglePageRemoveButtonCanUndoEvenWhenNothingIsSelected()
    {
        using var vm=Session(); var page=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(page); page.Copies=5; page.IsIncluded=false;
        vm.RemovePageCommand.Execute(page); Assert.Empty(vm.Pages);
        Assert.True(vm.UndoCommand.CanExecute(null)); vm.UndoCommand.Execute(null);
        Assert.Same(page,Assert.Single(vm.Pages)); Assert.Equal(5,page.Copies); Assert.False(page.IsIncluded); Assert.Same(page,vm.SelectedPage);
    }
    [Fact]
    public void QueueMetadataAndObjectEditsUndoInOneChronologicalOrder()
    {
        using var vm=Session(); var a=PrintPageItem.CreateBlank(SheetPaper.A4); var b=PrintPageItem.CreateBlank(SheetPaper.A4);
        vm.AddPage(a); vm.AddPage(b); var item=Rectangle(); a.Document.AddObject(item);
        b.Copies=7; a.IsIncluded=false; vm.SelectedPage=b; a.Document.MoveObject(item.Id,8,3);
        vm.UndoQueueCommand.Execute(null); Assert.Equal(item.Placement,a.Document.Objects.Single().Placement); Assert.Equal(7,b.Copies); Assert.False(a.IsIncluded); Assert.Same(a,vm.SelectedPage);
        vm.UndoCommand.Execute(null); Assert.True(a.IsIncluded); Assert.Equal(7,b.Copies);
        vm.UndoCommand.Execute(null); Assert.Equal(1,b.Copies); Assert.Equal(item,Assert.Single(a.Document.Objects));
        vm.RedoCommand.Execute(null); Assert.Equal(7,b.Copies);
        vm.RedoQueueCommand.Execute(null); Assert.False(a.IsIncluded);
        vm.RedoCommand.Execute(null); Assert.Equal(new RectD(18,23,30,40),a.Document.Objects.Single().Placement.BoundsMm);
    }
    [Fact]
    public void SingleMoveButtonsAndDropUseTheSameHistory()
    {
        using var vm=Session(); var p=Enumerable.Range(0,4).Select(_=>PrintPageItem.CreateBlank(SheetPaper.A4)).ToArray(); foreach(var page in p) vm.AddPage(page);
        vm.MoveDownCommand.Execute(p[0]); vm.MoveUpCommand.Execute(p[3]); vm.MovePage(p[2],p[0]);
        Assert.Equal(new[] {p[1],p[2],p[0],p[3]},vm.Pages);
        vm.UndoCommand.Execute(null); Assert.Equal(new[] {p[1],p[0],p[3],p[2]},vm.Pages);
        vm.UndoCommand.Execute(null); Assert.Equal(new[] {p[1],p[0],p[2],p[3]},vm.Pages);
        vm.UndoQueueCommand.Execute(null); Assert.Equal(p,vm.Pages);
        vm.RedoCommand.Execute(null); vm.RedoCommand.Execute(null); vm.RedoCommand.Execute(null); Assert.Equal(new[] {p[1],p[2],p[0],p[3]},vm.Pages);
    }
    [Fact]
    public void BatchFlagsCopiesAndRotationEachUndoAsOneStep()
    {
        using var vm=Session(); var p=Enumerable.Range(0,3).Select(_=>PrintPageItem.CreateBlank(SheetPaper.A4)).ToArray();
        foreach(var page in p) { page.Document.AddObject(Rectangle()); vm.AddPage(page); }
        var start=vm.UndoCount; vm.SelectNoneCommand.Execute(null); Assert.Equal(start+1,vm.UndoCount); Assert.All(p,page=>Assert.False(page.IsIncluded));
        vm.SelectAllCommand.Execute(null); Assert.Equal(start+2,vm.UndoCount);
        vm.SetQueueSelection(p,p[1]); vm.BatchCopies=9; vm.SetHighlightedCopiesCommand.Execute(null); Assert.Equal(start+3,vm.UndoCount);
        vm.RotateHighlightedPagesCommand.Execute(null); Assert.Equal(start+4,vm.UndoCount); Assert.All(p,page=>Assert.Equal(90,page.Document.Objects.Single().Placement.RotationDegrees));
        vm.UndoCommand.Execute(null); Assert.All(p,page=>Assert.Equal(0,page.Document.Objects.Single().Placement.RotationDegrees));
        vm.UndoCommand.Execute(null); Assert.All(p,page=>Assert.Equal(1,page.Copies));
        vm.UndoCommand.Execute(null); Assert.All(p,page=>Assert.False(page.IsIncluded));
        vm.UndoCommand.Execute(null); Assert.All(p,page=>Assert.True(page.IsIncluded));
    }
    [Fact]
    public void RedoSurvivesNavigationNoOpsAndCanceledTextButANewMutationDiscardsIt()
    {
        using var vm=Session(); var a=PrintPageItem.CreateBlank(SheetPaper.A4); var b=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(a); vm.AddPage(b);
        var item=Rectangle(); a.Document.AddObject(item); vm.UndoCommand.Execute(null); var redo=vm.RedoCount; var undo=vm.UndoCount;
        vm.SelectedPage=b; vm.SetQueueSelection([a,b],b); vm.SelectAllCommand.Execute(null); vm.BatchCopies=1; vm.SetHighlightedCopiesCommand.Execute(null);
        vm.BeginTextEditing(new(15,15)); vm.EditingText="Отмененный ввод"; vm.CancelTextEditCommand.Execute(null);
        Assert.Equal(undo,vm.UndoCount); Assert.Equal(redo,vm.RedoCount); Assert.Empty(b.Document.Objects);
        vm.BeginFlowTextCommand.Execute(null); vm.EditingText="Еще отмена"; vm.CancelTextEditCommand.Execute(null);
        Assert.Equal(2,vm.Pages.Count); Assert.Equal(redo,vm.RedoCount);
        vm.RedoCommand.Execute(null); Assert.Equal(item,Assert.Single(a.Document.Objects)); vm.UndoCommand.Execute(null);
        b.Copies=3; Assert.Equal(0,vm.RedoCount); Assert.False(vm.RedoCommand.CanExecute(null));
    }
    [Fact]
    public void SuccessfulInlineTextSessionIsOneStepAndNoOpEditKeepsRedo()
    {
        using var vm=Session(); var page=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(page); var start=vm.UndoCount;
        vm.BeginTextEditing(new(15,15)); vm.EditingText="А"; vm.EditingText="Абзац"; vm.TextBold=true; vm.FinishTextEditCommand.Execute(null);
        Assert.Equal(start+1,vm.UndoCount); var text=Assert.IsType<TextObject>(Assert.Single(page.Document.Objects)); Assert.Equal("Абзац",text.Text); Assert.True(text.Style.Bold);
        vm.UndoCommand.Execute(null); Assert.Empty(page.Document.Objects); vm.RedoCommand.Execute(null); Assert.Equal(text,Assert.Single(page.Document.Objects));
        page.Document.MoveObject(text.Id,3,2); vm.UndoCommand.Execute(null); var redo=vm.RedoCount;
        vm.BeginTextEditing(new(15,15),text); vm.FinishTextEditCommand.Execute(null); Assert.Equal(redo,vm.RedoCount);
        vm.RedoCommand.Execute(null); Assert.Equal(18,page.Document.Objects.Single().Placement.BoundsMm.X);
    }
    [Fact]
    public async Task OneImportUndoesEveryImportedPageAndKeepsOriginalAndOwnedMaterials()
    {
        using var vm=Session(); var prefix=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(prefix);
        var image=Fixtures.CreateImage("ux003-material.png"); var pdf=Fixtures.CreatePdf("ux003-pages.pdf",3); var hash=SHA256.HashData(File.ReadAllBytes(image));
        await vm.ImportFilesAsync([pdf,image]); var imported=vm.Pages.Skip(1).ToArray(); Assert.Equal(4,imported.Length);
        var source=imported.Last().Document.PrimaryAsset!; var owned=source.WorkingPath!;
        vm.UndoCommand.Execute(null); Assert.Same(prefix,Assert.Single(vm.Pages)); Assert.True(File.Exists(owned)); Assert.Equal(hash,SHA256.HashData(File.ReadAllBytes(image)));
        vm.RedoCommand.Execute(null); Assert.Equal(imported,vm.Pages.Skip(1)); Assert.Equal(hash,SHA256.HashData(File.ReadAllBytes(owned)));
        vm.SetQueueSelection(imported); vm.DeleteHighlightedPagesCommand.Execute(null); vm.UndoCommand.Execute(null);
        Assert.Equal(imported,vm.HighlightedPages); Assert.Equal(5,vm.Pages.Count); Assert.True(File.Exists(image)); Assert.True(File.Exists(owned));
    }
    [Fact]
    public async Task PasteIntoEmptySessionUndoesSheetAndContentTogether()
    {
        var clipboard=new FakeClipboard { Content=new([],Text:"Текст из буфера") }; using var vm=ClipboardTests.CreateVm(new(),clipboard);
        await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto); var page=Assert.Single(vm.Pages); Assert.Equal("Текст из буфера",page.Document.Objects.OfType<TextObject>().Single().Text);
        Assert.Equal(1,vm.UndoCount); vm.UndoCommand.Execute(null); Assert.Empty(vm.Pages); Assert.True(vm.RedoCommand.CanExecute(null));
        vm.RedoCommand.Execute(null); Assert.Same(page,Assert.Single(vm.Pages)); Assert.Equal("Текст из буфера",page.Document.Objects.OfType<TextObject>().Single().Text);
    }
    [Fact]
    public void HistoryRetainsOnlyTheLastHundredActionsAndBusyModeDoesNotApplyUndo()
    {
        using var vm=Session(); var page=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(page); var shape=Rectangle(); page.Document.AddObject(shape);
        for(var i=0;i<125;i++) page.Document.MoveObject(shape.Id,1,0);
        Assert.Equal(100,vm.UndoCount); vm.IsBusy=true; Assert.False(vm.UndoCommand.CanExecute(null)); vm.UndoCommand.Execute(null);
        Assert.Equal(135,page.Document.Objects.Single().Placement.BoundsMm.X); vm.IsBusy=false;
        while(vm.UndoCommand.CanExecute(null)) vm.UndoCommand.Execute(null);
        Assert.Equal(35,page.Document.Objects.Single().Placement.BoundsMm.X); Assert.Equal(100,vm.RedoCount); Assert.Single(vm.Pages);
        while(vm.RedoCommand.CanExecute(null)) vm.RedoCommand.Execute(null); Assert.Equal(135,page.Document.Objects.Single().Placement.BoundsMm.X); Assert.Equal(100,vm.UndoCount);
    }
    [Fact]
    public async Task LoadingAProjectResetsHistoryAndARejectedLoadPreservesIt()
    {
        using var vm=Session(); var page=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(page); page.Document.AddObject(Rectangle()); var path=ProjectFileTests.PathFor("ux003-reset");
        Assert.True(await vm.SaveProjectToAsync(path)); var count=vm.UndoCount; Assert.False(await vm.OpenProjectFromAsync(path+".missing")); Assert.Equal(count,vm.UndoCount);
        Assert.True(await vm.OpenProjectFromAsync(path)); Assert.Equal(0,vm.UndoCount); Assert.Equal(0,vm.RedoCount); Assert.False(vm.UndoCommand.CanExecute(null));
        vm.SelectedPage!.Copies=4; Assert.Equal(1,vm.UndoCount); vm.UndoCommand.Execute(null); Assert.Equal(1,vm.SelectedPage.Copies);
    }
    [Fact]
    public void ReflowAndEditsOnItsLastPageUndoFromAnotherPageWithoutLosingManualObjects()
    {
        using var vm=Session(); var first=PrintPageItem.CreateBlank(new(new(148,210),false,SheetMargins.SafeDefault)); var other=PrintPageItem.CreateBlank(SheetPaper.A4);
        vm.AddPage(first); vm.AddPage(other); vm.SelectedPage=first; vm.BeginFlowTextCommand.Execute(null);
        var source=string.Join("\n",Enumerable.Range(1,90).Select(i=>$"Строка {i:D3}: сохранность текста и объектов.")); vm.EditingText=source; vm.FinishTextEditCommand.Execute(null);
        var members=vm.Pages.Where(p=>p.Document.Objects.OfType<TextObject>().Any(t=>t.Flow!=null)).ToArray();
        Assert.True(members.Length>1); var story=members[0].Document.Objects.OfType<TextObject>().Single().Flow!.StoryId;
        var manual=Rectangle(65); members.Last().Document.AddObject(manual); vm.SelectedPage=members[0]; vm.TextFontSizePt=24;
        var grown=vm.Pages.Where(p=>p.Document.Objects.OfType<TextObject>().Any(t=>t.Flow?.StoryId==story)).ToArray(); Assert.True(grown.Length>members.Length);
        grown.Last().Copies=7; vm.SelectedPage=other; vm.UndoCommand.Execute(null); Assert.Equal(1,grown.Last().Copies); Assert.Same(grown.Last(),vm.SelectedPage);
        vm.SelectedPage=other; vm.UndoCommand.Execute(null); Assert.Equal(source,vm.StoryText(story)); Assert.All(members,p=>Assert.Contains(p,vm.Pages)); Assert.Equal(members.Length+1,vm.Pages.Count);
        Assert.Contains(vm.Pages.SelectMany(p=>p.Document.Objects),o=>o.Id==manual.Id);
        vm.SelectedPage=other; vm.UndoCommand.Execute(null); Assert.DoesNotContain(vm.Pages.SelectMany(p=>p.Document.Objects),o=>o.Id==manual.Id);
        vm.RedoCommand.Execute(null); Assert.Contains(vm.Pages.SelectMany(p=>p.Document.Objects),o=>o.Id==manual.Id); vm.RedoCommand.Execute(null); vm.RedoCommand.Execute(null);
        Assert.Equal(source,vm.StoryText(story)); Assert.Equal(7,vm.Pages.Single(p=>p.Id==grown.Last().Id).Copies);
    }
    [Fact]
    public async Task UndoBackToSavedStateClearsDirtyFlagButKeepsRealSettingChangesDirty()
    {
        using var vm=Session(); var page=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(page);
        Assert.True(await vm.SaveProjectToAsync(ProjectFileTests.PathFor("ux003-save-marker"))); page.Copies=5; Assert.True(vm.HasUnsavedChanges);
        vm.UndoCommand.Execute(null); Assert.Equal(1,page.Copies); Assert.False(vm.HasUnsavedChanges);
        var dpi=vm.Settings.Dpi; vm.Settings.Dpi=300; page.Copies=6; vm.UndoCommand.Execute(null);
        Assert.Equal(1,page.Copies); Assert.True(vm.HasUnsavedChanges);
        vm.Settings.Dpi=dpi; Assert.False(vm.HasUnsavedChanges);
    }
    [Fact]
    public async Task CancelingInlineDraftKeepsSavedProjectCleanAndLeavesNoHistoryEntry()
    {
        using var vm=Session(); var page=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(page);
        Assert.True(await vm.SaveProjectToAsync(ProjectFileTests.PathFor("ux003-cancel-clean"))); var count=vm.UndoCount;
        vm.BeginTextEditing(new(15,15)); vm.EditingText="Не сохранять"; vm.CancelTextEditCommand.Execute(null);
        Assert.Empty(page.Document.Objects); Assert.Equal(count,vm.UndoCount); Assert.False(vm.HasUnsavedChanges);
    }
}
