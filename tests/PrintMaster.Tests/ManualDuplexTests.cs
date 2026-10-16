using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using Xunit;

namespace PrintMaster.Tests;

public sealed class ManualDuplexTests
{
    [Theory]
    [InlineData(1,"1","")]
    [InlineData(2,"1","2")]
    [InlineData(3,"1,3","_,2")]
    [InlineData(4,"1,3","4,2")]
    [InlineData(5,"1,3,5","_,4,2")]
    [InlineData(6,"1,3,5","6,4,2")]
    public void FaceDownPassesKeepPhysicalSheetPairingAndBlankFeeds(int count,string front,string back)
    {
        var pages=Enumerable.Range(0,count).Select(_=>Page()).ToArray();
        var plan=ManualDuplexPlanner.Build(pages,Settings(),true);
        Assert.Equal(front,string.Join(",",plan.Sheets.Select(s=>s.Front.QueueOrdinal)));
        Assert.Equal(back,plan.BackPackets.Count==0 ? "" : string.Join(",",plan.Sheets.Reverse().Select(s=>s.Back.QueueOrdinal?.ToString() ?? "_")));
        Assert.Equal(count,plan.ContentImpressions); Assert.Equal((count+1)/2,plan.Sheets.Count);
        if(count%2==1) Assert.True(plan.Sheets.Last().Back.IsBlank);
        foreach(var sheet in plan.Sheets) Assert.Equal(sheet.Front.Settings.PaperSizeMm,sheet.Back.Page.GetSheet().Paper.SizeMm);
    }
    [Fact]
    public void DifferentSideCopiesDoNotTurnTwoFrontCopiesIntoFrontAndBack()
    {
        var a=Page(2); var b=Page(1); var plan=ManualDuplexPlanner.Build([a,b],Settings());
        Assert.Equal(2,plan.Sheets.Count); Assert.Equal(3,plan.ContentImpressions);
        Assert.All(plan.Sheets,s=>Assert.Equal(a.Id,s.Front.Page.Id));
        Assert.Equal(b.Id,plan.Sheets[0].Back.Page.Id); Assert.True(plan.Sheets[1].Back.IsBlank);
        Assert.Equal(new[] { 1,1 },plan.FrontPackets.SelectMany(p=>p.Pages).Select(p=>p.Copies));
        Assert.Equal(new[] { plan.Sheets[1].Back.Page.Id,b.Id },plan.BackPackets.SelectMany(p=>p.Pages).Select(p=>p.Id));
    }
    [Fact]
    public void BlankFrontFeedsPreserveExtraBackCopiesAndActualCustomSize()
    {
        var paper=NotebookProfiles.Default(NotebookVariant.RuledSpread).ToPaper();
        var a=Page(1,paper); var b=Page(3,paper); var plan=ManualDuplexPlanner.Build([a,b],Settings());
        Assert.Equal(3,plan.Sheets.Count); Assert.Equal(4,plan.ContentImpressions);
        Assert.True(plan.Sheets[1].Front.IsBlank); Assert.True(plan.Sheets[2].Front.IsBlank);
        Assert.All(plan.Sheets,s=>Assert.Equal(new SizeD(208,334),s.Front.Page.GetSheet().Paper.SizeMm));
    }
    [Fact]
    public async Task CancelAtFlipResumesOnlyBackPassAndCanChangeBackRotation()
    {
        var a=Page(); var b=Page(); var runner=new RecordingRunner(); var dialogs=new Prompts();
        dialogs.Decisions.Enqueue(false); dialogs.Decisions.Enqueue(true);
        var workflow=new PaperPrintWorkflow(runner,dialogs,_=>null,_=>true,_=>Task.CompletedTask);
        Assert.False(await workflow.StartAsync([a,b],Settings() with { ManualDuplex=true },null,default));
        Assert.True(workflow.HasPending); Assert.Equal(a.Id,Assert.Single(runner.Calls).Pages.Single().Id);
        dialogs.Rotation=180;
        Assert.True(await workflow.ContinueAsync(null,default)); Assert.False(workflow.HasPending);
        Assert.Equal(2,runner.Calls.Count); Assert.Equal(b.Id,runner.Calls[1].Pages.Single().Id); Assert.Equal(180,runner.Calls[1].Settings.PageRotationDegrees);
        Assert.All(dialogs.Steps,s=>Assert.Equal(PaperGuideStep.Reload,s));
    }
    [Fact]
    public async Task HiddenLoadingHintNeverSkipsMandatoryFlip()
    {
        var paper=NotebookProfiles.Default(NotebookVariant.GridSpread).ToPaper(); var runner=new RecordingRunner(); var prompts=new Prompts();
        var workflow=new PaperPrintWorkflow(runner,prompts,_=>null,_=>true,_=>Task.CompletedTask);
        await workflow.StartAsync([Page(paper:paper),Page(paper:paper)],Settings() with { ManualDuplex=true },null,default);
        Assert.Equal(new[] { PaperGuideStep.Reload },prompts.Steps); Assert.Equal(2,runner.Calls.Count);
    }
    [Fact]
    public async Task DecliningInitialHintSendsNothingAndDoesNotSaveHidePreference()
    {
        var paper=NotebookProfiles.Default(NotebookVariant.GridSingle).ToPaper(); var runner=new RecordingRunner(); var prompts=new Prompts();
        prompts.Decisions.Enqueue(false); var remembers=0;
        var workflow=new PaperPrintWorkflow(runner,prompts,_=>null,_=>false,_=>{ remembers++; return Task.CompletedTask; });
        Assert.False(await workflow.StartAsync([Page(paper:paper)],Settings(),null,default));
        Assert.Empty(runner.Calls); Assert.Equal(0,remembers); Assert.True(workflow.HasPending);
    }
    [Fact]
    public async Task StockBatchesFinishBothPassesBeforeSwitchingPaperAndKeepQueueOrder()
    {
        var a=Page(); var b=Page(); var paper=NotebookProfiles.Default(NotebookVariant.GridSpread).ToPaper(); var c=Page(paper:paper); var d=Page(paper:paper);
        var runner=new RecordingRunner(); var prompts=new Prompts(); var workflow=new PaperPrintWorkflow(runner,prompts,_=>null,_=>true,_=>Task.CompletedTask);
        await workflow.StartAsync([a,b,c,d],Settings() with { ManualDuplex=true },null,default);
        Assert.Equal(new[] { a.Id,b.Id,c.Id,d.Id },runner.Calls.SelectMany(call=>call.Pages).Select(page=>page.Id));
        Assert.Equal(new[] { PaperGuideStep.Reload,PaperGuideStep.ChangeStock,PaperGuideStep.Reload },prompts.Steps);
    }
    [Fact]
    public async Task NativeFailureDoesNotOfferAutomaticReplay()
    {
        var runner=new RecordingRunner { Throw=true }; var workflow=new PaperPrintWorkflow(runner,new Prompts(),_=>null,_=>true,_=>Task.CompletedTask);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>workflow.StartAsync([Page(),Page()],Settings() with { ManualDuplex=true },null,default));
        Assert.False(workflow.HasPending); Assert.Single(runner.Calls);
    }
    [Fact]
    public async Task MixedPdfCannotOverwriteOneOutputFileThroughMultipleJobs()
    {
        var runner=new RecordingRunner(); var workflow=new PaperPrintWorkflow(runner,new Prompts(),_=>null,_=>true,_=>Task.CompletedTask);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>workflow.StartAsync([Page(),Page(paper:new(new(148,210),false,SheetMargins.SafeDefault))],Settings() with { OutputPath="unused.pdf" },null,default));
        Assert.Empty(runner.Calls); Assert.False(workflow.HasPending);
    }
    [Theory]
    [InlineData(0,4,5)] [InlineData(180,6,10)]
    public void ViewportMovesInkInPaperCoordinatesWithAsymmetricMargins(int rotation,double x,double y)
    {
        var settings=Settings() with { PrintOffsetXmm=1,PrintOffsetYmm=2,PageRotationDegrees=rotation };
        var viewport=PrintViewport.SourceArea(settings,new(5,7,200,282));
        Assert.Equal(new RectD(x,y,200,282),viewport);
    }
    [Fact]
    public void SuppressionKeysSeparatePrinterVariantAndSize()
    {
        var a=Settings() with { Paper=PaperPreset.Notebook,Notebook=NotebookProfiles.Default(NotebookVariant.GridSingle),PaperSizeMm=new(167,208) };
        Assert.NotEqual(ManualDuplexPlanner.GuideKey(a),ManualDuplexPlanner.GuideKey(a with { PaperSizeMm=new(166,208) }));
        Assert.NotEqual(ManualDuplexPlanner.GuideKey(a),ManualDuplexPlanner.GuideKey(a with { PrinterName="Other" }));
        Assert.NotEqual(ManualDuplexPlanner.GuideKey(a),ManualDuplexPlanner.GuideKey(a with { Notebook=NotebookProfiles.Default(NotebookVariant.RuledSingle) }));
    }
    private static PageSnapshot Page(int copies=1,SheetPaper? paper=null)=>PrintPageItem.CreateBlank(paper ?? SheetPaper.A4).Snapshot() with { Copies=copies };
    private static PrintSettingsSnapshot Settings()=>new PrintSettings { PrinterName="Test printer" }.Snapshot();
    private sealed class RecordingRunner : IPrintJobRunner
    {
        public List<PrintPacket> Calls { get; }=[]; public bool Throw;
        public Task PrintAsync(IReadOnlyList<PageSnapshot> pages,PrintSettingsSnapshot settings,IProgress<PrintProgress>? progress,CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls.Add(new(pages.ToArray(),settings)); if(Throw) throw new InvalidOperationException("Test native failure"); return Task.CompletedTask; }
    }
    private sealed class Prompts : IUserDialogs
    {
        public Queue<bool> Decisions { get; }=new(); public List<PaperGuideStep> Steps { get; }=[]; public int Rotation;
        public bool ShowPaperGuide(PaperGuideViewModel model)
        { Steps.Add(model.Step); model.BackRotationDegrees=Rotation; return Decisions.Count==0 || Decisions.Dequeue(); }
        public string[] PickFiles()=>[]; public string? PickPdfOutput()=>null; public string? PickLibreOffice()=>null;
        public bool ConfirmCropping(int count)=>false; public void Error(string message) { }
    }
}
