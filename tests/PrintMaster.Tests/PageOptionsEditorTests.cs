using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.IO;
using Xunit;
namespace PrintMaster.Tests;
public sealed class PageOptionsEditorTests
{
    private sealed class ReviewDialogs:IUserDialogs
    {
        public PrintSettingsSnapshot? Captured {get;private set;}
        public bool ReviewPrint(PrintReviewViewModel model){Captured=model.Settings.Snapshot();return false;}
        public string[]PickFiles()=>[];public string?PickPdfOutput()=>null;public string?PickLibreOffice()=>null;public bool ConfirmCropping(int count)=>false;public void Error(string message)=>throw new Exception(message);
    }
    private static PrintSettingsSnapshot Defaults()=>new PrintSettings{PrinterName="HP LaserJet Professional P1102"}.Snapshot();
    private static PrintPageItem Source()=>PrintPageItem.FromSource(new SourceAsset(Guid.NewGuid(),SourceKind.Image,"x.png",Fixtures.CreateImage(),0,1,new(400,300),new(100,75)));
    private static PrintSessionViewModel Session()=>new(new ClipboardTests.TestDialogs(),Path.Combine(Fixtures.Root,"pm020-editor-"+Guid.NewGuid().ToString("N")));
    [Fact] public void SourceOptionsStartInheritedAndCanBeFrozenOrReset()
    {
        var page=Source();var model=new PageOptionsEditorViewModel([page.Snapshot()],Defaults(),NotebookProfiles.Defaults);
        Assert.True(model.CanChangePaper);Assert.False(model.UseOwnSettings);Assert.Null(model.Result());
        model.UseOwnSettings=true;model.Settings.Paper=PaperPreset.A5;model.Settings.Landscape=true;model.Settings.Monochrome=MonochromeMode.Photo;model.Settings.Dpi=300;
        Assert.True(model.CanApply);var actual=model.Result()!;Assert.Equal(new SizeD(148,210),actual.PaperSizeMm);Assert.True(actual.Landscape);Assert.Equal(MonochromeMode.Photo,actual.Monochrome);Assert.Equal(300,actual.Dpi);
        model.UseOwnSettings=false;Assert.Null(model.Result());
    }
    [Theory][InlineData(217,200)][InlineData(150,357)]public void HpDimensionsCannotBeSilentlyClamped(double width,double height)
    {
        var model=new PageOptionsEditorViewModel([Source().Snapshot()],Defaults(),NotebookProfiles.Defaults){UseOwnSettings=true};model.Settings.Paper=PaperPreset.Custom;
        model.Settings.CustomWidthMm=width;model.Settings.CustomHeightMm=height;Assert.False(model.CanApply);Assert.Contains("P1102",model.Error);
        Assert.Equal(width,model.Settings.CustomWidthMm);Assert.Equal(height,model.Settings.CustomHeightMm);
    }
    [Fact]public void FixedLayoutWinsOverOldRawPaperOverrideInDialogAndGroupKeepsEachLayout()
    {
        using var vm=Session();var source=Source();var fixedPage=PrintPageItem.CreateBlank(new(new(148,210),false,SheetMargins.SafeDefault));vm.AddPage(source);vm.AddPage(fixedPage);
        var old=new PagePrintOptions(PaperPreset.Custom,new(400,600),true,ScaleMode.Fill,MonochromeMode.Auto,600,160);
        fixedPage.PrintOptions=old;source.PrintOptions=old with {Paper=PaperPreset.A5,PaperSizeMm=new(148,210)};
        var model=new PageOptionsEditorViewModel([fixedPage.Snapshot(),source.Snapshot()],Defaults(),NotebookProfiles.Defaults);
        Assert.False(model.CanChangePaper);Assert.Equal(PaperPreset.A5,model.Settings.Paper);Assert.False(model.Settings.Landscape);Assert.True(model.CanApply);
        model.Settings.Monochrome=MonochromeMode.Photo;model.Settings.Dpi=300;
        var before=source.PrintOptions;var undo=vm.UndoCount;vm.ApplyPageOptions([fixedPage,source],model.Result(),preserveLayout:true);
        Assert.Equal(new SizeD(148,210),vm.EffectiveSettings(fixedPage).PaperSizeMm);Assert.False(vm.EffectiveSettings(fixedPage).Landscape);
        Assert.Equal(before.PaperSizeMm,source.PrintOptions!.PaperSizeMm);Assert.Equal(before.Landscape,source.PrintOptions.Landscape);Assert.Equal(before.Scaling,source.PrintOptions.Scaling);
        Assert.Equal(MonochromeMode.Photo,source.PrintOptions.Monochrome);Assert.Equal(300,fixedPage.PrintOptions!.Dpi);Assert.Equal(undo+1,vm.UndoCount);
        vm.UndoCommand.Execute(null);Assert.Equal(before,source.PrintOptions);Assert.Equal(old,fixedPage.PrintOptions);
    }
    [Fact]public void ResetInheritsLiveSessionAndRestoresOverrideOnUndo()
    {
        using var vm=Session();var page=Source();vm.AddPage(page);vm.Settings.PrinterName="Preview";
        var options=new PagePrintOptions(PaperPreset.A5,new(148,210),true,ScaleMode.Fill,MonochromeMode.Photo,300,90);
        vm.ApplyPageOptions([page],options);Assert.Equal(210d*96/25.4,vm.CanvasWidth,6);Assert.Contains("300",vm.PageOptionsSummary);
        vm.Settings.Monochrome=MonochromeMode.Text;Assert.Equal(MonochromeMode.Photo,vm.EffectiveSettings(page).Monochrome);
        vm.ApplyPageOptions([page],null);Assert.Equal(MonochromeMode.Text,vm.EffectiveSettings(page).Monochrome);Assert.Equal(210d*96/25.4,vm.CanvasWidth,6);
        vm.UndoCommand.Execute(null);Assert.Equal(options,page.PrintOptions);Assert.Equal(MonochromeMode.Photo,vm.EffectiveSettings(page).Monochrome);
    }
    [Fact]public void InvalidThresholdDoesNotBecome255()
    {
        var model=new PageOptionsEditorViewModel([Source().Snapshot()],Defaults(),NotebookProfiles.Defaults){UseOwnSettings=true};model.Settings.Threshold=300;
        Assert.False(model.CanApply);Assert.Contains("255",model.Error);Assert.Equal(300,model.Settings.Threshold);
    }
    [Fact]public async Task InsertingAnotherImageKeepsIndividualPaperOrientationAndFillPlacement()
    {
        using var vm=Session();var page=Source();vm.AddPage(page);vm.Settings.PrinterName="Preview";
        var options=new PagePrintOptions(PaperPreset.A5,new(148,210),true,ScaleMode.Fill,MonochromeMode.Photo,300,90);
        vm.ApplyPageOptions([page],options);var before=page.Document.Snapshot();var size=before.ContentSize;
        var safe=vm.SafeAreaMm;var expected=PageLayout.Place(size,safe,ScaleMode.Fill);
        await vm.InsertSheetImagesFromAsync([Fixtures.CreateImage("pm020-insertion.png")]);
        Assert.True(page.IsSheetLayout);Assert.Equal(new SizeD(148,210),page.Document.Paper.SizeMm);Assert.True(page.Document.Paper.Landscape);
        Assert.Equal(expected,page.Document.PrimaryContent!.Placement.BoundsMm);Assert.Equal(options,page.PrintOptions);
        vm.UndoCommand.Execute(null);Assert.Equal(before,page.Document.Snapshot());Assert.Equal(options,page.PrintOptions);
    }
    [Fact]public async Task MixedQueueDoesNotReplaceInheritedSourcePaperWithFirstFixedSheet()
    {
        var dialogs=new ReviewDialogs();using var vm=new PrintSessionViewModel(dialogs,Path.Combine(Fixtures.Root,"pm020-mixed-"+Guid.NewGuid().ToString("N")));
        var fixedPage=PrintPageItem.CreateBlank(SheetPaper.A4);var source=Source();vm.AddPage(fixedPage);vm.AddPage(source);
        vm.Settings.PrinterName="HP LaserJet Professional P1102";vm.Settings.Paper=PaperPreset.A5;
        await vm.PrintCommand.ExecuteAsync(null);
        Assert.NotNull(dialogs.Captured);Assert.Equal(PaperPreset.A5,dialogs.Captured.Paper);
        Assert.Equal(new SizeD(148,210),ManualDuplexPlanner.ForPage(source.Snapshot(),dialogs.Captured).PaperSizeMm);
        Assert.Equal(new SizeD(210,297),ManualDuplexPlanner.ForPage(fixedPage.Snapshot(),dialogs.Captured).PaperSizeMm);
    }
    [Fact]public void PhotoGridDoesNotAttachGlobalNotebookProfileToIndividualA5()
    {
        using var vm=Session();var page=Source();vm.AddPage(page);vm.Settings.Paper=PaperPreset.Notebook;vm.Settings.Notebook=NotebookProfiles.Default(NotebookVariant.GridSingle);
        vm.ApplyPageOptions([page],new(PaperPreset.A5,new(148,210),false,ScaleMode.Fit,MonochromeMode.Photo,600,160));
        var request=vm.CapturePhotoGridRequest(page);Assert.Equal(new SizeD(148,210),request.Sheet.Paper.SizeMm);Assert.Null(request.Sheet.Paper.Notebook);
    }
}
