using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.IO;
using Xunit;

namespace PrintMaster.Tests;

public sealed class PrintWorkflowTests
{
    [Fact]
    public void ReviewShowsPhysicalPairsAndBlankSideForUnequalCopies()
    {
        var a=PrintPageItem.CreateBlank(SheetPaper.A4); a.Copies=2;
        var b=PrintPageItem.CreateBlank(SheetPaper.A4);
        using var review=new PrintReviewViewModel([a.Snapshot(),b.Snapshot()],new() { PrinterName="HP LaserJet Professional P1102",ManualDuplex=true },[]);
        Assert.True(review.ShowDuplexPlan); Assert.Contains("2 листа бумаги",review.DuplexSummary);
        Assert.Contains("3 оттиска",review.DuplexSummary); Assert.Contains("1 пустая сторона",review.DuplexSummary);
        Assert.Contains("Лист 1: стр. 1 / стр. 2",review.DuplexPairs); Assert.Contains("Лист 2: стр. 1 / пусто",review.DuplexPairs);
        review.Settings.ManualDuplex=false; Assert.False(review.ShowDuplexPlan); Assert.Empty(review.DuplexPairs);
    }
    [Fact]
    public void SelectingAndResizingSheetsDoesNotChangeJobAndUndoRestoresLayout()
    {
        using var vm=NewSession(new Dialogs());
        vm.Settings.PrinterName="Microsoft Print to PDF"; vm.Settings.Paper=PaperPreset.A4; vm.Settings.Threshold=173;
        var before=ProjectPrintSettings.Capture(vm.Settings);
        var a=PrintPageItem.CreateBlank(SheetPaper.A4); var b=PrintPageItem.CreateBlank(new(new(148,210),false,SheetMargins.SafeDefault));
        vm.AddPage(a); vm.AddPage(b); vm.SelectedPage=b;
        Assert.Equal(before,ProjectPrintSettings.Capture(vm.Settings)); Assert.Equal(PaperPreset.A5,vm.LayoutSettings.Paper);
        vm.LayoutSettings.Paper=PaperPreset.Notebook;
        Assert.Equal(new SizeD(167,208),b.Document.Paper.SizeMm); Assert.NotNull(b.Document.Paper.Notebook); Assert.Equal(before,ProjectPrintSettings.Capture(vm.Settings));
        vm.UndoCommand.Execute(null);
        Assert.Equal(new SizeD(148,210),b.Document.Paper.SizeMm); Assert.Equal(PaperPreset.A5,vm.LayoutSettings.Paper);
        Assert.Equal(before,ProjectPrintSettings.Capture(vm.Settings)); Assert.Equal(SheetPaper.A4,a.Document.Paper);
    }
    [Fact]
    public void ReviewUsesMarkedLayoutAndKeepsSourceOptionsUnchanged()
    {
        var source=new PrintSettings { PrinterName="Microsoft Print to PDF",Paper=PaperPreset.A4,Dpi=300 };
        var before=ProjectPrintSettings.Capture(source);
        var page=PrintPageItem.CreateBlank(new(new(148,210),true,SheetMargins.SafeDefault)); page.Copies=4;
        using var review=new PrintReviewViewModel([page.Snapshot()],source,[source.PrinterName]);
        Assert.Equal(PaperPreset.A5,review.Settings.Paper); Assert.True(review.Settings.Landscape); Assert.Equal("1 страница · 4 оттиска",review.Summary);
        Assert.False(review.HasAutomaticLayout); Assert.True(review.CanSubmit);
        review.Settings.Paper=PaperPreset.A4;
        // The fixed layout remains the authority even if the inherited draft changes.
        // Changing the draft starts a fresh driver preflight, so submission waits.
        Assert.True(review.IsLoading); Assert.False(review.CanSubmit); Assert.False(review.TryAccept(true)); Assert.Empty(review.Error);
        var effective=ManualDuplexPlanner.ForPage(page.Snapshot(),review.Settings.Snapshot());
        Assert.Equal(new SizeD(148,210),effective.PaperSizeMm); Assert.True(effective.Landscape);
        Assert.Equal(before,ProjectPrintSettings.Capture(source)); Assert.Equal(new SizeD(148,210),page.Document.Paper.SizeMm);
    }
    [Fact]
    public void FixedMixedFormatsCannotBeAcceptedAndClippingNeedsExplicitAcknowledgement()
    {
        var options=new PrintSettings { PrinterName="Microsoft Print to PDF" };
        var a=PrintPageItem.CreateBlank(SheetPaper.A4); var b=PrintPageItem.CreateBlank(new(new(148,210),false,SheetMargins.SafeDefault));
        using(var mixed=new PrintReviewViewModel([a.Snapshot(),b.Snapshot()],options,[options.PrinterName]))
        { Assert.Contains("Размер бумаги",mixed.Error); Assert.False(mixed.TryAccept(true)); Assert.Null(mixed.AcceptedSettings); }
        a.Document.AddObject(new ShapeObject(Guid.NewGuid(),new(new(0,0,20,20)),SheetShapeKind.Rectangle,new("#000000",0.5)));
        using var clipped=new PrintReviewViewModel([a.Snapshot()],options,[options.PrinterName]);
        Assert.True(clipped.AllowCropping); Assert.NotEmpty(clipped.Warning); Assert.False(clipped.TryAccept(false));
        Assert.True(clipped.TryAccept(true)); Assert.True(clipped.AcceptedSettings!.AllowCropping);
        Assert.Equal(new RectD(0,0,20,20),a.Document.Objects[0].Placement.BoundsMm);
    }
    [Fact]
    public async Task CancellingReviewDoesNotChangeProjectOptionsOrAskForOutput()
    {
        var dialogs=new Dialogs { ReviewAction=review=>{ review.Settings.Dpi=300; review.Settings.Paper=PaperPreset.A5; return false; } };
        using var vm=NewSession(dialogs); vm.Settings.PrinterName="Microsoft Print to PDF"; var page=PrintPageItem.CreateBlank(SheetPaper.A4);
        vm.AddPage(page); var before=vm.CaptureProject(); var undo=vm.UndoCount;
        await vm.PrintCommand.ExecuteAsync(null);
        Assert.Equal(before.Print,vm.CaptureProject().Print); Assert.Equal(before.Pages[0].Sheet,page.Document.Snapshot());
        Assert.Equal(undo,vm.UndoCount); Assert.Equal(1,dialogs.ReviewCalls); Assert.Equal(0,dialogs.OutputCalls); Assert.Empty(dialogs.Errors);
    }
    [Fact]
    public async Task PreferencesRoundTripAndInvalidSaveKeepsExistingFile()
    {
        var path=Path.Combine(Fixtures.Root,"ux005-prefs-"+Guid.NewGuid().ToString("N")+".json");
        var store=new ApplicationPreferencesStore(path);
        var value=new ApplicationPreferences { PrinterName="Microsoft Print to PDF",Dpi=300,Monochrome=MonochromeMode.Photo,Threshold=143,AutosaveEnabled=false,AutosaveDelaySeconds=7 };
        await store.SaveAsync(value,default); var bytes=await File.ReadAllBytesAsync(path);
        Assert.Equal(value,await store.LoadAsync(default));
        await Assert.ThrowsAsync<ArgumentException>(()=>store.SaveAsync(value with { Dpi=1200 },default));
        Assert.Equal(bytes,await File.ReadAllBytesAsync(path)); Assert.Empty(Directory.GetFiles(Fixtures.Root,Path.GetFileName(path)+".*.tmp"));
    }
    [Fact]
    public async Task PreferencesApplyToNewSessionButNeverOverwriteOpenedProjectOrSavedDefaults()
    {
        var store=new Store(new ApplicationPreferences { PrinterName="Microsoft Print to PDF",Dpi=300,Monochrome=MonochromeMode.Photo,Threshold=117,AutosaveEnabled=false });
        using var vm=NewSession(new Dialogs(),store); await vm.InitializeAsync();
        Assert.Equal(300,vm.Settings.Dpi); Assert.Equal(117,vm.Settings.Threshold);
        var page=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(page);
        var project=new ProjectSession(ProjectPrintSettings.Capture(new PrintSettings { PrinterName="Microsoft Print to PDF",Dpi=600,Threshold=203 }),
            vm.CaptureProject().Editor,vm.CaptureProject().Pages);
        var path=Path.Combine(Fixtures.Root,"ux005-project-"+Guid.NewGuid().ToString("N")+".printmaster");
        await new ProjectFileService().SaveAsync(project,path,true,default);
        Assert.True(await vm.OpenProjectFromAsync(path)); Assert.Equal(600,vm.Settings.Dpi); Assert.Equal(203,vm.Settings.Threshold);
        Assert.Equal(300,store.Value.Dpi); Assert.Equal(117,store.Value.Threshold); Assert.Equal(0,store.Saves);
        await vm.FlushRecoveryAsync(); Assert.False(File.Exists(vm.RecoveryFilePath));
    }
    [Fact]
    public async Task SettingsCancellationAndSavingDefaultsDoNotEditOpenProject()
    {
        var store=new Store(new()); var dialogs=new Dialogs(); using var vm=NewSession(dialogs,store);
        vm.AddPage(PrintPageItem.CreateBlank(SheetPaper.A4)); var before=vm.CaptureProject();
        dialogs.SettingsAction=draft=>{ draft.Dpi=300; return false; };
        await vm.ApplicationSettingsCommand.ExecuteAsync(null);
        Assert.Equal(0,store.Saves); Assert.Equal(before.Print,vm.CaptureProject().Print);
        dialogs.SettingsAction=draft=>{ draft.Dpi=300; draft.AutosaveEnabled=false; return draft.TryAccept(); };
        await vm.ApplicationSettingsCommand.ExecuteAsync(null);
        Assert.Equal(1,store.Saves); Assert.Equal(300,store.Value.Dpi); Assert.False(store.Value.AutosaveEnabled);
        Assert.Equal(before.Print,vm.CaptureProject().Print); Assert.Equal(before.Pages[0].Sheet,vm.CaptureProject().Pages[0].Sheet);
    }
    [Fact]
    public async Task DriverFailureCannotBeClearedByAcceptingReview()
    {
        using var review=new PrintReviewViewModel([PrintPageItem.CreateBlank(SheetPaper.A4).Snapshot()],
            new PrintSettings { PrinterName="PrintMaster-test-nonexistent-printer" },[]);
        await review.RefreshAsync();
        Assert.NotEmpty(review.Error); var error=review.Error;
        Assert.False(review.CanSubmit); Assert.False(review.TryAccept(true)); Assert.Equal(error,review.Error); Assert.Null(review.AcceptedSettings);
    }
    [Fact] [Trait("Category","Printer")]
    public async Task AcceptedReviewPrintsMarkedLayoutAndCopiesWithoutChangingActiveSheet()
    {
        var output=Path.Combine(Fixtures.Root,"ux005-confirmed-"+Guid.NewGuid().ToString("N")+".pdf");
        var dialogs=new Dialogs { OutputPath=output,ReviewAction=review=>review.TryAccept(false) };
        using var vm=NewSession(dialogs); await vm.InitializeAsync(); vm.Settings.PrinterName="Microsoft Print to PDF";
        var marked=PrintPageItem.CreateBlank(new(new(148,210),true,SheetMargins.SafeDefault)); marked.Copies=2;
        marked.Document.AddObject(new ShapeObject(Guid.NewGuid(),new(new(20,30,40,20)),SheetShapeKind.Rectangle,new("#000000",0.5)));
        var active=PrintPageItem.CreateBlank(SheetPaper.A4); active.IsIncluded=false;
        vm.AddPage(marked); vm.AddPage(active); vm.SelectedPage=active; var original=active.Document.Snapshot();
        await vm.PrintCommand.ExecuteAsync(null);
        Assert.Equal(1,dialogs.ReviewCalls); Assert.Equal(1,dialogs.OutputCalls); Assert.Empty(dialogs.Errors);
        Assert.Equal(original,active.Document.Snapshot()); Assert.Equal(PaperPreset.A4,vm.LayoutSettings.Paper);
        Assert.Equal(PaperPreset.A5,vm.Settings.Paper); Assert.True(vm.Settings.Landscape); Assert.Equal(2,vm.ProgressCompleted);
        var pages=new PdfiumRenderer().ReadPages(output); Assert.Equal(2,pages.Count);
        Assert.InRange(pages[0].Width/72*25.4,209,211); Assert.InRange(pages[0].Height/72*25.4,147,149);
    }
    private static PrintSessionViewModel NewSession(Dialogs dialogs,IApplicationPreferencesStore? store=null)=>
        new(dialogs,Path.Combine(Fixtures.Root,"ux005-recovery-"+Guid.NewGuid().ToString("N")),preferences:store);
    private sealed class Store(ApplicationPreferences value) : IApplicationPreferencesStore
    {
        public ApplicationPreferences Value=value; public int Saves;
        public Task<ApplicationPreferences> LoadAsync(CancellationToken token)=>Task.FromResult(Value);
        public Task SaveAsync(ApplicationPreferences value,CancellationToken token) { value.Validate(); Value=value; Saves++; return Task.CompletedTask; }
    }
    private sealed class Dialogs : IUserDialogs
    {
        public Func<PrintReviewViewModel,bool>? ReviewAction; public Func<ApplicationSettingsViewModel,bool>? SettingsAction;
        public int ReviewCalls,OutputCalls; public string? OutputPath; public List<string> Errors=[];
        public bool ReviewPrint(PrintReviewViewModel review) { ReviewCalls++; return ReviewAction?.Invoke(review) ?? false; }
        public bool EditApplicationSettings(ApplicationSettingsViewModel settings)=>SettingsAction?.Invoke(settings) ?? false;
        public string[] PickFiles()=>[]; public string? PickPdfOutput() { OutputCalls++; return OutputPath; }
        public string? PickLibreOffice()=>null; public bool ConfirmCropping(int count)=>false; public void Error(string message)=>Errors.Add(message);
    }
}
