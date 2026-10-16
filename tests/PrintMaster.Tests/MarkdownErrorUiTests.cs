using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.IO;
using Xunit;

namespace PrintMaster.Tests;

public sealed class MarkdownErrorUiTests
{
    private sealed class Dialogs : IUserDialogs
    {
        public Action<MarkdownEditorViewModel>? DuringEdit {get;set;}
        public string[] PickFiles()=>[];
        public string? PickPdfOutput()=>null;
        public string? PickLibreOffice()=>null;
        public bool ConfirmCropping(int count)=>false;
        public void Error(string message)=>throw new InvalidOperationException(message);
        public bool EditMarkdown(MarkdownEditorViewModel model) {DuringEdit?.Invoke(model);return false;}
    }

    private static PrintSessionViewModel Session(Dialogs dialogs)=>new(dialogs,
        Path.Combine(Fixtures.Root,"markdown-error-session-"+Guid.NewGuid().ToString("N")));

    [Fact]
    public async Task InvalidActiveDraftWarnsMainWindowAndCancelRestoresPrintingOfUnchangedPages()
    {
        var dialogs=new Dialogs();using var vm=Session(dialogs);
        var definition=new MarkdownDocument(Guid.NewGuid(),"# Прежний текст\n\n$x^2$",MarkdownStyle.Default,[],"Формулы");
        await vm.ApplyMarkdownAsync(new(definition,[]),SheetPaper.A4,null,default);
        var page=Assert.Single(vm.Pages);var snapshot=page.Document.Snapshot();var undo=vm.UndoCount;
        Assert.True(vm.PrintCommand.CanExecute(null));
        dialogs.DuringEdit=editor=>
        {
            editor.Source="# Черновик\n\n$$x^3$$";
            editor.FontSizePt=0; // Synchronous validation exercises the same blocking-error UI channel as formula failures.
            Assert.True(editor.HasBlockingError);Assert.True(vm.HasActiveMarkdownDraftError);Assert.True(vm.HasMarkdownWarning);
            Assert.False(editor.CanApply);Assert.False(vm.PrintCommand.CanExecute(null));
            Assert.Contains("Черновик не применён",vm.MarkdownWarning);Assert.Contains(".md",vm.MarkdownWarning);
            Assert.Equal("# Черновик\n\n$$x^3$$",editor.Source);
        };
        await vm.EditMarkdownCommand.ExecuteAsync(null);
        Assert.False(vm.HasActiveMarkdownDraftError);Assert.True(vm.PrintCommand.CanExecute(null));Assert.True(vm.HasMarkdownWarning);
        Assert.Contains("Правки не применены",vm.MarkdownWarning);Assert.Contains("прежние страницы",vm.MarkdownWarning);
        Assert.Equal(snapshot,Assert.Single(vm.Pages).Document.Snapshot());Assert.Equal(undo,vm.UndoCount);
        Assert.Equal(definition.Source,vm.CaptureProject().MarkdownDocuments.Single().Source);
        vm.DismissMarkdownWarningCommand.Execute(null);Assert.False(vm.HasMarkdownWarning);Assert.True(vm.PrintCommand.CanExecute(null));
    }

    [Fact]
    public async Task FailedMarkdownFileImportLeavesVisibleFilenameAndLineWithoutChangingTheQueueOrFile()
    {
        var dialogs=new Dialogs();using var vm=Session(dialogs);var page=PrintPageItem.CreateBlank(SheetPaper.A4);vm.AddPage(page);
        var snapshot=page.Document.Snapshot();var undo=vm.UndoCount;
        var path=Path.Combine(Fixtures.Root,"broken-formula-"+Guid.NewGuid().ToString("N")+".md");
        const string source="# Пример\n\n$$\\printmasterUnsupportedUiCommand{x}$$";await File.WriteAllTextAsync(path,source);
        var error=await Assert.ThrowsAsync<MarkdownLayoutException>(()=>vm.ImportMarkdownAsync(path,default));
        Assert.Equal(3,error.Line);Assert.True(vm.HasMarkdownWarning);Assert.Contains(Path.GetFileName(path),vm.MarkdownWarning);
        Assert.Contains("Строка 3",vm.MarkdownWarning);Assert.Contains("printmasterUnsupportedUiCommand",vm.MarkdownWarning);Assert.Contains("не добавлен",vm.MarkdownWarning);
        Assert.False(vm.HasActiveMarkdownDraftError);Assert.True(vm.PrintCommand.CanExecute(null));Assert.Equal(source,await File.ReadAllTextAsync(path));
        Assert.Equal(snapshot,Assert.Single(vm.Pages).Document.Snapshot());Assert.Equal(undo,vm.UndoCount);
    }
}
