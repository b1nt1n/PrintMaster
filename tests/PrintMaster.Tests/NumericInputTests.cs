using PrintMaster.Models;
using PrintMaster.ViewModels;
using PrintMaster.Services;
using System.Globalization;
using Xunit;

namespace PrintMaster.Tests;

public sealed class NumericInputTests
{
    [Theory]
    [InlineData("ru-RU","12,5",12.5)] [InlineData("en-US","12,5",12.5)]
    [InlineData("ru-RU","12.5",12.5)] [InlineData("en-US","12.5",12.5)]
    [InlineData("ro-RO","-0,75",-.75)] [InlineData("en-US"," 1e2 ",100)]
    public void DecimalInputHasTheSameMeaningAcrossCultures(string culture,string input,double expected)
    {
        var before=CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo(culture); Assert.True(NumericInputRules.TryParse(input,out var value)); Assert.Equal(expected,value); }
        finally { CultureInfo.CurrentCulture=before; }
    }
    [Theory]
    [InlineData("")] [InlineData("-")] [InlineData(".")] [InlineData("abc")] [InlineData("NaN")] [InlineData("Infinity")]
    [InlineData("1e999")] [InlineData("1,234.5")] [InlineData("1 234")] [InlineData("1,2,3")] [InlineData("1.2.3")] [InlineData("50%")]
    public void InvalidOrAmbiguousInputIsRejected(string input)=>Assert.False(NumericInputRules.TryParse(input,out _));

    [Theory]
    [InlineData("0")] [InlineData("-1")] [InlineData("1000")] [InlineData("2.5")] [InlineData("2,5")]
    public void CopiesNeverRoundOrClamp(string input)
    { var rule=new NumericInputRules(1,999,"коп.",Integer:true); Assert.False(rule.TryValidate(input,out _,out var error)); Assert.Contains("999",error); }

    [Fact]
    public void BoundaryAndOptionalInputRetainTheirExplicitRules()
    {
        var positive=new NumericInputRules(0,1000,"pt",MinimumExclusive:true);
        Assert.False(positive.TryValidate("0",out _,out _)); Assert.True(positive.TryValidate("0.0001",out var tiny,out _)); Assert.Equal(.0001,tiny);
        Assert.True(positive.TryValidate("1000",out _,out _)); Assert.False(positive.TryValidate("1000.01",out _,out _));
        Assert.True(new NumericInputRules(5,800,AllowPercent:true).TryValidate("175,5%",out var zoom,out _)); Assert.Equal(175.5,zoom);
        Assert.True(new NumericInputRules(.05,20,AllowEmpty:true).TryValidate("",out _,out _));
        Assert.False(new NumericInputRules(.05,20).TryValidate("",out _,out _));
    }
    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(1001)] [InlineData(double.NaN)]
    public void InvalidPaperSizeIsNotSilentlyReplaced(double entered)
    {
        var settings=new PrintSettings { PrinterName="Microsoft Print to PDF",Paper=PaperPreset.Custom,CustomWidthMm=entered };
        Assert.Equal(entered,settings.CustomWidthMm);
        Assert.Throws<ArgumentException>(()=>settings.Snapshot());
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(1000)]
    public void InvalidCopiesAreRejectedBeforeChangingThePage(int entered)
    {
        var page=PrintPageItem.CreateBlank(SheetPaper.A4); page.Copies=4;
        Assert.Throws<ArgumentOutOfRangeException>(()=>page.Copies=entered);
        Assert.Equal(4,page.Copies);
    }

    [Fact]
    public void InvalidBatchAndZoomDoNotChangeTheirPreviousMeaning()
    {
        using var vm=DrawingStyleTests.Session(); var page=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(page);
        vm.BatchCopies=1000; vm.SetHighlightedCopiesCommand.Execute(null); Assert.Equal(1,page.Copies); Assert.Contains("999",vm.Status);
        vm.ZoomText="175,5"; Assert.Equal(175.5,vm.Zoom);
        vm.ZoomText="0"; Assert.Equal(175.5,vm.Zoom); Assert.Contains("800",vm.Status);
        vm.ZoomText="1000"; Assert.Equal(175.5,vm.Zoom); Assert.Contains("800",vm.Status);
    }

    [Fact]
    public async Task PendingInvalidInputStopsPrintSaveAndGroupCopies()
    {
        using var vm=DrawingStyleTests.Session(); var page=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(page);
        vm.CommitInputs=()=>false; vm.BatchCopies=5;
        vm.SetHighlightedCopiesCommand.Execute(null); Assert.Equal(1,page.Copies);
        var path=ProjectFileTests.PathFor("ux011-invalid-draft"); Assert.False(await vm.SaveProjectToAsync(path)); Assert.False(System.IO.File.Exists(path));
        await vm.PrintCommand.ExecuteAsync(null); Assert.False(vm.IsBusy); Assert.False(vm.HasPendingPaperJob); Assert.Contains("Esc",vm.Status);
    }

    [Fact]
    public void HpLimitsDescribeFeedDimensionsAndDoNotSwapWithOrientation()
    {
        var hp=new PrintSettings { PrinterName="HP LaserJet Professional P1102",Paper=PaperPreset.Custom,CustomWidthMm=217,CustomHeightMm=334,Landscape=true };
        Assert.Contains("76–216",Assert.Throws<ArgumentException>(()=>hp.Snapshot()).Message);
        var create=new NewSheetViewModel(hp); Assert.Equal(76,create.WidthMinimum); Assert.Equal(216,create.WidthMaximum); Assert.Equal(356,create.HeightMaximum); Assert.False(create.CanCreate);
        create.WidthText="208"; Assert.True(create.CanCreate); Assert.Equal(new SizeD(334,208),create.Result!.OrientedSizeMm);
        hp.PrinterName="Microsoft Print to PDF"; Assert.Equal(new SizeD(334,217),hp.Snapshot().OrientedPaperMm);
    }

    [Fact]
    public void StrokeWidthDisplayRetainsAppliedPrecision()
    {
        using var vm=DrawingStyleTests.Session(); var page=PrintPageItem.CreateBlank(SheetPaper.A4);
        var shape=new ShapeObject(Guid.NewGuid(),new(new(10,20,30,40)),SheetShapeKind.Rectangle,new("#000000",1.23456789));
        page.Document.AddObject(shape); vm.AddPage(page); vm.SetObjectSelection([shape.Id]);
        Assert.True(NumericInputRules.TryParse(vm.SelectedDrawingWidthText,out var width)); Assert.Equal(1.23456789,width);
        var undo=vm.UndoCount; vm.SelectedDrawingWidthText=vm.SelectedDrawingWidthText; Assert.Equal(undo,vm.UndoCount);
    }
}
