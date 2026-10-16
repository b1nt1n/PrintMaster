using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using Xunit;

namespace PrintMaster.Tests;

public sealed class ViewModelReviewRegressionTests
{
    private static PrintSessionViewModel Session()=>ClipboardTests.CreateVm(new(),new());

    [Theory]
    [InlineData(1,false)]
    [InlineData(999,true)]
    public void CopyButtonsAtLimitsDoNotThrowOrCreateHistory(int copies,bool increase)
    {
        using var vm=Session();var page=PrintPageItem.CreateBlank(SheetPaper.A4);vm.AddPage(page);page.Copies=copies;
        var history=vm.UndoCount;var command=increase ? vm.IncreaseCopiesCommand : vm.DecreaseCopiesCommand;
        Assert.False(command.CanExecute(page));
        command.Execute(page);
        Assert.Equal(copies,page.Copies);Assert.Equal(history,vm.UndoCount);
    }

    [Fact]
    public void CopyButtonAvailabilityTracksBoundariesBusyStateAndQueueMembership()
    {
        using var vm=Session();var page=PrintPageItem.CreateBlank(SheetPaper.A4);vm.AddPage(page);
        var notifications=new List<string?>();page.PropertyChanged+=(_,e)=>notifications.Add(e.PropertyName);
        var commandChanges=0;vm.IncreaseCopiesCommand.CanExecuteChanged+=(_,_)=>commandChanges++;
        Assert.True(page.CanIncreaseCopies);Assert.False(page.CanDecreaseCopies);
        page.Copies=999;
        Assert.False(page.CanIncreaseCopies);Assert.True(page.CanDecreaseCopies);
        Assert.Contains(nameof(PrintPageItem.CanIncreaseCopies),notifications);Assert.Contains(nameof(PrintPageItem.CanDecreaseCopies),notifications);
        Assert.True(commandChanges>0);Assert.False(vm.IncreaseCopiesCommand.CanExecute(page));Assert.True(vm.DecreaseCopiesCommand.CanExecute(page));
        vm.IsBusy=true;Assert.False(vm.DecreaseCopiesCommand.CanExecute(page));vm.DecreaseCopiesCommand.Execute(page);Assert.Equal(999,page.Copies);
        vm.IsBusy=false;vm.RemovePageCommand.Execute(page);Assert.False(vm.DecreaseCopiesCommand.CanExecute(page));
        vm.DecreaseCopiesCommand.Execute(page);Assert.Equal(999,page.Copies);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void SingleMoveButtonRebindsPageAfterCommittingActiveTextFlow(int direction)
    {
        using var vm=Session();var a=PrintPageItem.CreateBlank(SheetPaper.A4);var b=PrintPageItem.CreateBlank(SheetPaper.A4);vm.AddPage(a);vm.AddPage(b);
        var editing=direction<0 ? b : a;vm.SelectedPage=editing;vm.BeginFlowTextCommand.Execute(null);vm.EditingText="Текст должен сохраниться при перестановке.";
        var command=direction<0 ? vm.MoveUpCommand : vm.MoveDownCommand;command.Execute(editing);
        Assert.False(vm.IsEditingText);Assert.Equal(new[]{b.Id,a.Id},vm.Pages.Select(p=>p.Id));
        Assert.Equal(2,vm.Pages.Count);Assert.Equal(2,vm.Pages.Select(p=>p.Id).Distinct().Count());
        Assert.Equal("Текст должен сохраниться при перестановке.",vm.Pages.Single(p=>p.Id==editing.Id).Document.Objects.OfType<TextObject>().Single().Text);
        vm.UndoCommand.Execute(null);Assert.Equal(new[]{a.Id,b.Id},vm.Pages.Select(p=>p.Id));
    }

    [Theory]
    [InlineData(false,false)]
    [InlineData(false,true)]
    [InlineData(true,false)]
    [InlineData(true,true)]
    public void DragMoveRebindsBothSourceAndTargetAfterTextFlowCommit(bool group,bool editTarget)
    {
        using var vm=Session();var a=PrintPageItem.CreateBlank(SheetPaper.A4);var b=PrintPageItem.CreateBlank(SheetPaper.A4);vm.AddPage(a);vm.AddPage(b);
        vm.SelectedPage=editTarget ? b : a;vm.BeginFlowTextCommand.Execute(null);vm.EditingText="Содержимое активного продолжения.";
        if(group)vm.MoveQueueGroup([a],b);else vm.MovePage(a,b);
        Assert.False(vm.IsEditingText);Assert.Equal(new[]{b.Id,a.Id},vm.Pages.Select(p=>p.Id));
        Assert.Equal(2,vm.Pages.Count);Assert.Equal(2,vm.Pages.Select(p=>p.Id).Distinct().Count());
        Assert.Single(vm.Pages.SelectMany(p=>p.Document.Objects).OfType<TextObject>());
        vm.UndoCommand.Execute(null);Assert.Equal(new[]{a.Id,b.Id},vm.Pages.Select(p=>p.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueueDragPayloadStillResolvesAfterFocusAlreadyCommittedTheFlow(bool editTarget)
    {
        using var vm=Session();var a=PrintPageItem.CreateBlank(SheetPaper.A4);var b=PrintPageItem.CreateBlank(SheetPaper.A4);vm.AddPage(a);vm.AddPage(b);
        vm.SelectedPage=editTarget ? b : a;vm.BeginFlowTextCommand.Execute(null);vm.EditingText="Фокус завершил ввод до отпускания мыши.";vm.FinishTextEditCommand.Execute(null);
        Assert.DoesNotContain(editTarget ? b : a,vm.Pages);
        vm.MoveQueueGroup([a],b);Assert.Equal(new[]{b.Id,a.Id},vm.Pages.Select(p=>p.Id));Assert.Equal(2,vm.Pages.Count);
        Assert.Single(vm.Pages.SelectMany(p=>p.Document.Objects).OfType<TextObject>());vm.UndoCommand.Execute(null);Assert.Equal(new[]{a.Id,b.Id},vm.Pages.Select(p=>p.Id));
    }

    [Theory]
    [InlineData(NotebookVariant.GridSingle)]
    [InlineData(NotebookVariant.GridSpread)]
    [InlineData(NotebookVariant.RuledSingle)]
    [InlineData(NotebookVariant.RuledSpread)]
    public void DuplicatingNotebookKeepsEditableStoryAndCreatesIndependentIdentity(NotebookVariant variant)
    {
        using var vm=Session();var page=PrintPageItem.CreateBlank(NotebookProfiles.Default(variant).ToPaper());vm.AddPage(page);
        vm.SetNotebookStory(page,"Исходный текст тетрадного листа.",new());var story=page.Document.NotebookText!;
        vm.SetQueueSelection([page],page);vm.DuplicateHighlightedPagesCommand.Execute(null);var copy=Assert.Single(vm.HighlightedPages);
        Assert.NotEqual(page.Id,copy.Id);Assert.NotEqual(story.Id,copy.Document.NotebookText!.Id);
        Assert.Equal(story.Source,copy.Document.NotebookText.Source);Assert.Empty(NotebookTextLayout.Remainder(copy.Document.Snapshot()));
        Assert.All(copy.Document.Objects.OfType<TextObject>(),t=>{Assert.True(t.Flow?.Notebook);Assert.Equal(copy.Document.NotebookText.Id,t.Flow!.StoryId);});
        vm.SelectedPage=copy;vm.BeginFlowTextCommand.Execute(null);vm.EditingText="Правка только копии.";vm.FinishTextEditCommand.Execute(null);
        Assert.Equal("Правка только копии.",copy.Document.NotebookText.Source);Assert.Equal(story,page.Document.NotebookText);
        Assert.DoesNotContain(copy.Document.Objects.OfType<TextObject>(),t=>t.Flow==null);
        vm.UndoCommand.Execute(null);Assert.Equal(story.Source,copy.Document.NotebookText.Source);
    }

    [Theory]
    [InlineData(false,NotebookVariant.GridSpread)]
    [InlineData(true,NotebookVariant.GridSpread)]
    [InlineData(false,NotebookVariant.RuledSpread)]
    [InlineData(true,NotebookVariant.RuledSpread)]
    public async Task CopyingNotebookFragmentOrMixedGroupPastesStandaloneEditableTextWithoutChangingSource(bool group,NotebookVariant variant)
    {
        var dialogs=new ClipboardTests.TestDialogs();var clipboard=new FakeClipboard();using var vm=ClipboardTests.CreateVm(dialogs,clipboard);
        var page=PrintPageItem.CreateBlank(NotebookProfiles.Default(variant).ToPaper());vm.AddPage(page);
        vm.SetNotebookStory(page,string.Join("\n",Enumerable.Range(1,30).Select(n=>$"Строка {n:D2}")),new(FontSizePt:14));
        var fragment=page.Document.Objects.OfType<TextObject>().Last();Assert.NotEmpty(fragment.Text);
        var shape=new ShapeObject(Guid.NewGuid(),new(new(20,80,15,15)),SheetShapeKind.Rectangle,new());if(group)page.Document.AddObject(shape);
        var before=page.Document.Snapshot();vm.SetObjectSelection(group ? [fragment.Id,shape.Id] : [fragment.Id]);
        Assert.True(await vm.CopyContextAsync(false));Assert.Empty(dialogs.Errors);Assert.Equal(before,page.Document.Snapshot());
        var target=PrintPageItem.CreateBlank(SheetPaper.A4);vm.AddPage(target);vm.SelectedPage=target;
        await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto);
        var text=Assert.Single(target.Document.Objects.OfType<TextObject>());Assert.Equal(fragment.Text,text.Text);Assert.Equal(fragment.Style,text.Style);
        Assert.Null(text.Flow);Assert.Null(target.Document.NotebookText);Assert.Equal(group ? 2 : 1,target.Document.Objects.Count);
        vm.SetObjectSelection([text.Id]);vm.EditSelectedTextCommand.Execute(null);vm.EditingText="Самостоятельная правка.";vm.FinishTextEditCommand.Execute(null);
        Assert.Equal("Самостоятельная правка.",target.Document.Objects.OfType<TextObject>().Single().Text);Assert.Equal(before,page.Document.Snapshot());
        vm.UndoCommand.Execute(null);Assert.Equal(fragment.Text,target.Document.Objects.OfType<TextObject>().Single().Text);
        vm.UndoCommand.Execute(null);Assert.Empty(target.Document.Objects);Assert.Empty(dialogs.Errors);
    }

    [Theory]
    [InlineData(false,false,false)]
    [InlineData(true,false,false)]
    [InlineData(false,true,false)]
    [InlineData(true,true,false)]
    [InlineData(false,false,true)]
    [InlineData(true,false,true)]
    [InlineData(false,true,true)]
    [InlineData(true,true,true)]
    public async Task CuttingNotebookFragmentPreservesUnselectedTextAndHiddenOverflow(bool group,bool overflow,bool cutLeft)
    {
        var dialogs=new ClipboardTests.TestDialogs();var clipboard=new FakeClipboard();using var vm=ClipboardTests.CreateVm(dialogs,clipboard);
        var page=PrintPageItem.CreateBlank(NotebookProfiles.Default(NotebookVariant.GridSpread).ToPaper());vm.AddPage(page);
        var source=string.Join("\n",Enumerable.Range(1,overflow ? 90 : 30).Select(n=>$"Строка {n:D2}"));vm.SetNotebookStory(page,source,new(FontSizePt:14));
        var parts=page.Document.Objects.OfType<TextObject>().OrderBy(t=>t.Flow!.PartIndex).ToArray();Assert.Equal(2,parts.Length);Assert.NotEmpty(parts[1].Text);
        var fragment=parts[cutLeft ? 0 : 1];var unselected=parts[cutLeft ? 1 : 0].Text;
        var expected=source.Remove(cutLeft ? 0 : parts[0].Text.Length,fragment.Text.Length);
        var remainder=NotebookTextLayout.Remainder(page.Document.Snapshot());Assert.Equal(overflow,remainder.Length>0);
        var shape=new ShapeObject(Guid.NewGuid(),new(new(20,80,15,15)),SheetShapeKind.Rectangle,new());if(group)page.Document.AddObject(shape);
        var before=page.Document.Snapshot();var undo=vm.UndoCount;vm.SetObjectSelection(group ? [fragment.Id,shape.Id] : [fragment.Id]);
        Assert.True(await vm.CopyContextAsync(false,true));Assert.Empty(dialogs.Errors);Assert.Equal(undo+1,vm.UndoCount);
        Assert.NotNull(page.Document.NotebookText);Assert.Equal(expected,page.Document.NotebookText.Source);Assert.StartsWith(unselected,page.Document.NotebookText.Source);
        if(overflow)Assert.EndsWith(remainder,page.Document.NotebookText.Source);
        Assert.DoesNotContain(page.Document.Objects,o=>o.Id==shape.Id);Assert.All(page.Document.Objects.OfType<TextObject>(),t=>Assert.True(t.Flow?.Notebook));
        vm.UndoCommand.Execute(null);Assert.Equal(before,page.Document.Snapshot());
        var target=PrintPageItem.CreateBlank(SheetPaper.A4);vm.AddPage(target);vm.SelectedPage=target;await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto);
        Assert.Equal(fragment.Text,Assert.Single(target.Document.Objects.OfType<TextObject>()).Text);Assert.Equal(before,page.Document.Snapshot());Assert.Empty(dialogs.Errors);
    }

    [Fact]
    public async Task FailedNotebookCutKeepsEveryCharacterAndDoesNotCreateHistory()
    {
        var dialogs=new ClipboardTests.TestDialogs();var clipboard=new FakeClipboard{WriteError=new System.IO.IOException("Буфер занят")};using var vm=ClipboardTests.CreateVm(dialogs,clipboard);
        var page=PrintPageItem.CreateBlank(NotebookProfiles.Default(NotebookVariant.GridSpread).ToPaper());vm.AddPage(page);
        vm.SetNotebookStory(page,string.Join("\n",Enumerable.Range(1,90).Select(n=>$"Строка {n:D2}")),new(FontSizePt:14));
        var before=page.Document.Snapshot();var undo=vm.UndoCount;vm.SetObjectSelection([page.Document.Objects.OfType<TextObject>().Last().Id]);
        Assert.False(await vm.CopyContextAsync(false,true));Assert.Equal(before,page.Document.Snapshot());Assert.Equal(undo,vm.UndoCount);Assert.Single(dialogs.Errors);
    }
}
