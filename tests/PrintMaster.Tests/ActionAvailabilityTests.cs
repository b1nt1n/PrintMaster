using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.IO;
using Xunit;

namespace PrintMaster.Tests;

public sealed class ActionAvailabilityTests
{
    private static PrintSessionViewModel Session(IUserDialogs? dialogs=null,IPrintJobRunner? runner=null,FakeClipboard? clipboard=null)=>
        new(dialogs ?? new ClipboardTests.TestDialogs(),Path.Combine(Fixtures.Root,"ux014-availability-recovery-"+Guid.NewGuid().ToString("N")),
            clipboard ?? new FakeClipboard(),new MemoryApplicationPreferencesStore(),runner,new MemoryWorkspaceStateStore());
    private static ShapeObject Shape(double x=10)=>new(Guid.NewGuid(),new(new(x,20,30,40)),SheetShapeKind.Rectangle,new());

    [Fact]
    public void EmptyQueueAndUnmarkedPagesExplainDifferentNextSteps()
    {
        using var vm=Session();
        Assert.False(vm.PrintCommand.CanExecute(null)); Assert.Contains("галочками",vm.PrintActionAvailability);
        Assert.False(vm.ExportPagesCommand.CanExecute(null)); Assert.Contains("создайте лист",vm.ExportActionAvailability);
        var page=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(page); page.IsIncluded=false;
        Assert.False(vm.PrintCommand.CanExecute(null)); Assert.True(vm.ExportPagesCommand.CanExecute(null));
        Assert.Contains("галочками",vm.PrintActionAvailability); Assert.Contains("PNG или PDF",vm.ExportActionAvailability);
        page.IsIncluded=true; page.Copies=3;
        Assert.True(vm.PrintCommand.CanExecute(null)); Assert.Contains("1 страница · 3 оттиска",vm.PrintActionAvailability);
    }
    [Fact]
    public void InvalidPrinterAndPaperDraftStillAllowOpeningPrintReview()
    {
        using var vm=Session(); vm.AddPage(PrintPageItem.CreateBlank(SheetPaper.A4));
        vm.Settings.PrinterName=""; vm.Settings.Paper=PaperPreset.Custom; vm.Settings.CustomWidthMm=10000;
        Assert.True(vm.PrintCommand.CanExecute(null)); Assert.Contains("параметры можно выбрать",vm.PrintActionAvailability);
    }
    [Fact]
    public void BusyReasonOverridesMissingSelectionAndHistoryWithoutChangingState()
    {
        using var vm=Session(); var page=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(page); var before=vm.CaptureProject();
        vm.IsBusy=true;
        Assert.False(vm.PrintCommand.CanExecute(null)); Assert.False(vm.ExportPagesCommand.CanExecute(null)); Assert.False(vm.UndoCommand.CanExecute(null));
        foreach(var hint in new[] {vm.PrintActionAvailability,vm.ExportActionAvailability,vm.UndoActionAvailability,vm.QueueSelectionActionAvailability,vm.EditorActionAvailability})
            Assert.Contains("отмените",hint);
        Assert.Equal(before.Print,vm.CaptureProject().Print); Assert.Equal(before.Pages[0].Sheet,page.Document.Snapshot()); vm.IsBusy=false;
        Assert.True(vm.PrintCommand.CanExecute(null)); Assert.True(vm.UndoCommand.CanExecute(null));
    }
    [Fact]
    public void PreviewAndTextDraftExplainHistoryRestrictionThenRecover()
    {
        using var vm=Session(); vm.AddPage(PrintPageItem.CreateBlank(SheetPaper.A4));
        Assert.True(vm.UndoCommand.CanExecute(null)); vm.ShowPrintPreview=true;
        Assert.False(vm.UndoCommand.CanExecute(null)); Assert.Contains("Печатный вид",vm.UndoActionAvailability);
        Assert.False(vm.InsertSheetImagesCommand.CanExecute(null)); Assert.Contains("Печатный вид",vm.InsertImageActionAvailability);
        vm.ShowPrintPreview=false; vm.BeginTextEditing(new(15,15));
        Assert.True(vm.IsEditingText); Assert.False(vm.UndoCommand.CanExecute(null)); Assert.Contains("Готово",vm.UndoActionAvailability);
        vm.FinishTextEditing(false); Assert.True(vm.UndoCommand.CanExecute(null));
        vm.UndoCommand.Execute(null); Assert.True(vm.RedoCommand.CanExecute(null)); Assert.Contains("Повторить:",vm.RedoActionAvailability);
    }
    [Fact]
    public void DocumentViewingAndMissingPhotoGiveDistinctEditorRecoveryActions()
    {
        using var vm=Session(); var pdf=Fixtures.CreatePdf("ux014-availability-"+Guid.NewGuid().ToString("N")+".pdf",1);
        var document=PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Pdf,pdf,pdf,0,1,new(595,842),new(210,297)));
        vm.AddPage(document); vm.SelectedPage=document;
        Assert.False(vm.CanEdit); Assert.False(vm.InsertSheetImagesCommand.CanExecute(null)); Assert.Contains("PDF и DOCX",vm.EditorActionAvailability);
        Assert.True(vm.RotateCommand.CanExecute(null)); Assert.False(vm.SaveCopyCommand.CanExecute(null)); Assert.Contains("Экспорт листов",vm.SaveCopyActionAvailability);
        var missing=Path.Combine(Fixtures.Root,"ux014-missing-"+Guid.NewGuid().ToString("N")+".png");
        var photo=PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Image,missing,missing,0,1,new(800,600),new(160,120)));
        vm.AddPage(photo); vm.SelectedPage=photo;
        Assert.False(vm.CanEdit); Assert.False(vm.InsertSheetImagesCommand.CanExecute(null)); Assert.Contains("Переподключить исходники",vm.EditorActionAvailability);
    }
    [Fact]
    public void ImageCopyAndWholePageExportHintsKeepTheirDifferentMeanings()
    {
        using var vm=Session(); var image=Fixtures.CreateImage("ux014-availability-"+Guid.NewGuid().ToString("N")+".png");
        var page=PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Image,image,image,0,1,new(800,600),new(160,120)));
        vm.AddPage(page); vm.SelectedPage=page;
        Assert.True(vm.SaveCopyCommand.CanExecute(null)); Assert.True(vm.ResetCropCommand.CanExecute(null)); Assert.True(vm.InsertSheetImagesCommand.CanExecute(null));
        Assert.Contains("исходного изображения",vm.SaveCopyActionAvailability); Assert.Contains("целого листа",vm.SaveCopyActionAvailability);
        var blank=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(blank); vm.SelectedPage=blank;
        Assert.False(vm.SaveCopyCommand.CanExecute(null)); Assert.False(vm.ResetCropCommand.CanExecute(null)); Assert.Contains("Выберите страницу",vm.ResetCropActionAvailability);
    }
    [Fact]
    public void QueueBoundaryHintsFollowTheActualHighlightedGroup()
    {
        using var vm=Session(); var pages=Enumerable.Range(0,3).Select(_=>PrintPageItem.CreateBlank(SheetPaper.A4)).ToArray(); foreach(var page in pages) vm.AddPage(page);
        vm.SetQueueSelection([],pages[0]);
        Assert.False(vm.DeleteHighlightedPagesCommand.CanExecute(null)); Assert.Contains("Выделите страницы",vm.QueueSelectionActionAvailability);
        vm.SetQueueSelection(pages.Take(2),pages[0]);
        Assert.False(vm.MoveHighlightedPagesCommand.CanExecute(-1)); Assert.Contains("начале очереди",vm.GetActionHint(vm.MoveHighlightedPagesCommand,-1));
        Assert.True(vm.MoveHighlightedPagesCommand.CanExecute(1)); Assert.Contains("Переместить",vm.QueueMoveDownActionAvailability);
        vm.MoveHighlightedPagesCommand.Execute(1);
        Assert.False(vm.MoveHighlightedPagesCommand.CanExecute(1)); Assert.Contains("конце очереди",vm.QueueMoveDownActionAvailability);
        Assert.True(vm.MoveHighlightedPagesCommand.CanExecute(-1)); Assert.Contains("Переместить",vm.QueueMoveUpActionAvailability);
    }
    [Fact]
    public void ObjectBoundaryHintsFollowFrontBackOrderAndSelection()
    {
        using var vm=Session(); var page=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(page); var back=Shape(); var front=Shape(60); page.Document.AddObjects([back,front]);
        vm.SetObjectSelection([]); Assert.False(vm.AlignSheetSelectionCommand.CanExecute("left")); Assert.Contains("Выделите объекты",vm.ObjectSelectionActionAvailability);
        vm.SetObjectSelection([back.Id]); Assert.False(vm.OrderSheetSelectionCommand.CanExecute(-1)); Assert.Contains("заднем плане",vm.GetActionHint(vm.OrderSheetSelectionCommand,-1));
        Assert.True(vm.OrderSheetSelectionCommand.CanExecute(2)); Assert.Contains("Изменить порядок",vm.GetActionHint(vm.OrderSheetSelectionCommand,2));
        vm.SetObjectSelection([front.Id]); Assert.False(vm.OrderSheetSelectionCommand.CanExecute(1)); Assert.Contains("переднем плане",vm.GetActionHint(vm.OrderSheetSelectionCommand,1));
        Assert.True(vm.AlignSheetObjectCommand.CanExecute("left")); Assert.Contains("Изменить выбранный",vm.ObjectEditActionAvailability);
        Assert.False(vm.RotateCommand.CanExecute(null)); Assert.Contains("Выберите фотографию",vm.RotateActionAvailability);
    }
    [Fact]
    public void HintNotificationsFollowBusySelectionHistoryAndPreviewChanges()
    {
        using var vm=Session(); var names=new HashSet<string>(); vm.PropertyChanged+=(_,e)=>{ if(e.PropertyName!=null) names.Add(e.PropertyName); };
        vm.IsBusy=true; Assert.Contains(nameof(vm.PrintActionAvailability),names); Assert.Contains(nameof(vm.EditorActionAvailability),names);
        names.Clear(); vm.IsBusy=false; vm.AddPage(PrintPageItem.CreateBlank(SheetPaper.A4));
        Assert.Contains(nameof(vm.UndoActionAvailability),names); Assert.Contains(nameof(vm.ExportActionAvailability),names);
        names.Clear(); vm.ShowPrintPreview=true; Assert.Contains(nameof(vm.InsertImageActionAvailability),names); Assert.Contains(nameof(vm.UndoActionAvailability),names);
    }
    [Fact]
    public void ClipboardHintReadsLatestExternalContentsWithoutPerformingPaste()
    {
        var clipboard=new FakeClipboard(); using var vm=Session(clipboard:clipboard);
        Assert.Contains("Скопируйте",vm.PasteActionAvailability); Assert.False(vm.CanPasteClipboard);
        clipboard.Content=new([],Text:"Вставить позже");
        Assert.True(vm.CanPasteClipboard); Assert.Contains("Вставить из буфера",vm.GetActionHint(vm.PasteClipboardCommand,PasteDestination.Queue)); Assert.Equal(0,clipboard.Reads);
    }
    [Fact]
    public void RequestedCancellationExplainsWaitingAndNotifiesAllDisabledActionHints()
    {
        using var vm=Session(); vm.BeginOperation(SessionOperationKind.Export,total:2);
        Assert.True(vm.CancelCommand.CanExecute(null)); Assert.Contains("Запросить отмену",vm.GetActionHint(vm.CancelCommand));
        var changed=new HashSet<string>(); vm.PropertyChanged+=(_,e)=>{ if(e.PropertyName!=null) changed.Add(e.PropertyName); };
        vm.CancelCommand.Execute(null);
        Assert.False(vm.CancelCommand.CanExecute(null)); Assert.True(vm.CancellationRequested);
        Assert.Contains("Отмена уже запрошена",vm.GetActionHint(vm.CancelCommand));
        Assert.Contains("Дождитесь",vm.IdleActionAvailability); Assert.DoesNotContain("отмените",vm.IdleActionAvailability);
        Assert.DoesNotContain("отмените",vm.PrintActionAvailability);
        Assert.Contains(nameof(vm.CancelActionAvailability),changed); Assert.Contains(nameof(vm.PrintActionAvailability),changed);
        vm.EndOperation(); Assert.Contains("нет выполняющейся операции",vm.GetActionHint(vm.CancelCommand));
    }
    [Fact]
    public async Task PendingDuplexHintTakesPriorityEvenWhenNoQueuePagesAreMarked()
    {
        var runner=new NoPaperRunner(); using var vm=Session(new DuplexDialogs(),runner);
        vm.Settings.PrinterName="Availability test printer"; vm.Settings.ManualDuplex=true;
        var pages=new[] {PrintPageItem.CreateBlank(SheetPaper.A4),PrintPageItem.CreateBlank(SheetPaper.A4)};
        foreach(var page in pages) vm.AddPage(page);
        await vm.PrintCommand.ExecuteAsync(null); Assert.True(vm.HasPendingPaperJob); Assert.Equal(1,runner.Calls);
        foreach(var page in pages) page.IsIncluded=false;
        Assert.True(vm.PrintCommand.CanExecute(null)); Assert.Contains("Продолжить ожидающее",vm.PrintActionAvailability); Assert.DoesNotContain("Отметьте",vm.PrintActionAvailability);
        vm.DiscardPaperJobCommand.Execute(null); Assert.False(vm.HasPendingPaperJob); Assert.False(vm.PrintCommand.CanExecute(null));
    }
    private sealed class NoPaperRunner : IPrintJobRunner
    {
        public int Calls;
        public Task PrintAsync(IReadOnlyList<PageSnapshot> pages,PrintSettingsSnapshot settings,IProgress<PrintProgress>? progress,CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; return Task.CompletedTask; }
    }
    private sealed class DuplexDialogs : IUserDialogs
    {
        public string[] PickFiles()=>[]; public string? PickPdfOutput()=>null; public string? PickLibreOffice()=>null;
        public bool ConfirmCropping(int count)=>false; public void Error(string message)=>throw new InvalidOperationException(message);
        public bool ReviewPrint(PrintReviewViewModel model)=>model.TryAccept(true);
        public bool ShowPaperGuide(PaperGuideViewModel model)=>false;
    }
}
