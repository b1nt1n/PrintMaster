using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Xunit;

namespace PrintMaster.Tests;

public sealed class NotebookTests
{
    [Theory]
    [InlineData(NotebookVariant.GridSingle,167,208,167,208)]
    [InlineData(NotebookVariant.RuledSingle,167,208,167,208)]
    [InlineData(NotebookVariant.GridSpread,334,208,208,334)]
    [InlineData(NotebookVariant.RuledSpread,334,208,208,334)]
    public void PresetsKeepEditorAndFeedDimensionsDistinct(NotebookVariant variant,double w,double h,double feedW,double feedH)
    {
        var profile=NotebookProfiles.Default(variant); var paper=profile.ToPaper();
        Assert.Equal(new SizeD(w,h),paper.OrientedSizeMm); Assert.Equal(new SizeD(feedW,feedH),paper.SizeMm);
        var model=new NewSheetViewModel(new() { Paper=PaperPreset.Notebook,PrinterName="HP LaserJet Professional P1102" });
        model.NotebookVariant=variant;
        Assert.True(model.CanCreate,model.Error); Assert.Equal(paper.OrientedSizeMm,model.Result!.OrientedSizeMm);
    }
    [Fact]
    public void LegacyNotebookWithoutProfileKeepsOldDimensions()
    {
        var settings=new PrintSettings { Paper=PaperPreset.Notebook,PrinterName="HP LaserJet Professional P1102" };
        Assert.Equal(new SizeD(165,205),settings.Snapshot().PaperSizeMm);
        var paper=new SheetPaper(new(165,205),false,SheetMargins.SafeDefault);
        PrintReviewViewModel.SetPaper(settings,paper); Assert.Null(settings.Notebook); Assert.Equal(paper.SizeMm,settings.PaperSizeMm);
    }
    [Fact]
    public void IndependentFractionalProfilesKeepDifferentMarginsAndFold()
    {
        var a=NotebookProfiles.Default(NotebookVariant.GridSpread) with { WidthMm=332.6,HeightMm=207.4,FoldMm=166.1,LeftMarginMm=19.5,RightMarginMm=20.6 };
        var b=a with { Id=Guid.NewGuid(),LeftMarginMm=20.5,GridOriginYmm=1.1 };
        var regions=NotebookGeometry.TextRegions(a.ToPaper(),a.ToPaper().SafeAreaMm);
        Assert.Equal(19.5,regions[0].NativeBounds.X); Assert.Equal(166.1-5,regions[0].NativeBounds.Right,6);
        Assert.Equal(332.6-20.6,regions[1].NativeBounds.Right,6);
        Assert.Equal(20.5,NotebookGeometry.TextRegions(b.ToPaper(),b.ToPaper().SafeAreaMm)[0].NativeBounds.X);
        Assert.Equal(19.5,a.LeftMarginMm); Assert.NotEqual(a.Id,b.Id);
    }
    [Theory]
    [InlineData(false,0)] [InlineData(true,90)]
    public void RotationMapsRulingAndTextIntoOrientedPaper(bool rotated,int degrees)
    {
        var p=NotebookProfiles.Default(NotebookVariant.RuledSpread); var paper=p.ToPaper(landscape:!rotated);
        var region=NotebookGeometry.TextRegions(paper,paper.SafeAreaMm)[0];
        Assert.Equal(degrees,region.Placement.RotationDegrees);
        var bounds=SheetEditing.VisualBounds(new TextObject(Guid.NewGuid(),region.Placement,"A",new()));
        Assert.True(bounds.Intersect(paper.SafeAreaMm).Width>=bounds.Width-0.01);
        Assert.True(bounds.Intersect(paper.SafeAreaMm).Height>=bounds.Height-0.01);
        Assert.Contains(NotebookGeometry.Guides(paper),g=>g.IsFold);
    }
    [Fact]
    public void InvalidProfilesCannotCreateEmptyOrNonFiniteTextRegions()
    {
        var p=NotebookProfiles.Default(NotebookVariant.GridSpread);
        Assert.Throws<ArgumentException>(()=>(p with { LeftMarginMm=166 }).Validate());
        Assert.Throws<ArgumentException>(()=>(p with { GridHeightMm=double.NaN }).Validate());
        var editor=new NotebookProfileEditorViewModel(p,true); editor.LeftMarginText="19,5";
        Assert.Equal(19.5,editor.Result!.LeftMarginMm); Assert.NotEqual(p.Id,editor.Result.Id);
        editor.HeightText="bad"; Assert.False(editor.CanApply); Assert.NotEmpty(editor.Error);
    }
    [Fact]
    public void TwoRegionsRetainEveryCharacterAndReportRemainder()
    {
        var paper=NotebookProfiles.Default(NotebookVariant.RuledSpread).ToPaper();
        var source=string.Concat(Enumerable.Repeat("Строка 🙂 e\u0301\r\n",900)); var story=new NotebookTextStory(Guid.NewGuid(),source,new(FontSizePt:14));
        var result=NotebookTextLayout.Layout(paper,story,paper.SafeAreaMm);
        Assert.Equal(2,result.Objects.Length); Assert.NotEmpty(result.Objects[0].Text); Assert.NotEmpty(result.Objects[1].Text); Assert.NotEmpty(result.Remainder);
        Assert.Equal(source,string.Concat(result.Objects.Select(o=>o.Text))+result.Remainder);
        Assert.Equal(8,result.Objects[0].Style.FixedLineHeightMm); Assert.Equal(0,result.Objects[0].Flow!.PartIndex); Assert.Equal(1,result.Objects[1].Flow!.PartIndex);
        Assert.True(result.Objects[0].Placement.BoundsMm.Right<paper.Notebook!.FoldMm);
        Assert.True(result.Objects[1].Placement.BoundsMm.X>paper.Notebook.FoldMm);
    }
    [Fact]
    public void UpdatingPaperAndStoryIsUndoableWithoutMutatingAnotherSheet()
    {
        var p=NotebookProfiles.Default(NotebookVariant.GridSpread); var paper=p.ToPaper(); var a=SheetDocument.CreateBlank(paper); var b=SheetDocument.CreateBlank(paper);
        var original=a.Snapshot(); var story=new NotebookTextStory(Guid.NewGuid(),"Текст",new()); var layout=NotebookTextLayout.Layout(paper,story,paper.SafeAreaMm);
        a.SetNotebookText(story,layout.Objects); a.Undo(); Assert.Equal(original,a.Snapshot()); a.Redo(); Assert.Equal(story,a.NotebookText);
        a.SetPaper((p with { Id=Guid.NewGuid(),LeftMarginMm=19.1 }).ToPaper()); Assert.Equal(20,b.Paper.Notebook!.LeftMarginMm);
    }
    [Theory]
    [InlineData(NotebookVariant.GridSingle)] [InlineData(NotebookVariant.GridSpread)]
    [InlineData(NotebookVariant.RuledSingle)] [InlineData(NotebookVariant.RuledSpread)]
    public void RulingNeverEntersContentRaster(NotebookVariant variant)
    {
        var profile=NotebookProfiles.Default(variant); var page=SheetDocument.CreateBlank(profile.ToPaper());
        page.AddObject(new ShapeObject(Guid.NewGuid(),new(new(22,22,40,20)),SheetShapeKind.Rectangle,new("#000000",0.5)));
        var rasterizer=new PageRasterizer(new()); using var before=rasterizer.RenderSheet(page.Snapshot(),96,true,default,1600);
        var settings=new PrintSettings { PrinterName="HP LaserJet Professional P1102",Paper=PaperPreset.Notebook,Notebook=profile,Landscape=profile.IsSpread }.Snapshot();
        var printBefore=rasterizer.RenderPrintPreview(new PrintPageItem(page).Snapshot(),settings,page.Paper.SafeAreaMm,default);
        page.SetPaper((profile with { ShowGuides=false,GridWidthMm=7,LeftMarginMm=19.2 }).ToPaper());
        using var after=rasterizer.RenderSheet(page.Snapshot(),96,true,default,1600);
        Assert.Equal(before.Size,after.Size); Assert.Equal(Pixels(before),Pixels(after));
        var printAfter=rasterizer.RenderPrintPreview(new PrintPageItem(page).Snapshot(),settings with { Notebook=page.Paper.Notebook },page.Paper.SafeAreaMm,default);
        Assert.Equal(PreviewPixels(printBefore),PreviewPixels(printAfter));
    }
    private static byte[] PreviewPixels(System.Windows.Media.Imaging.BitmapSource bitmap)
    { var stride=(bitmap.PixelWidth*bitmap.Format.BitsPerPixel+7)/8; var bytes=new byte[stride*bitmap.PixelHeight]; bitmap.CopyPixels(bytes,stride,0); return bytes; }
    [Fact]
    public void SessionTextHonorsPrintDocumentMarginRoundingOnSingleNotebook()
    {
        using var vm=Session(); var page=PrintPageItem.CreateBlank(NotebookProfiles.Default(NotebookVariant.GridSingle).ToPaper());
        vm.AddPage(page); vm.SetNotebookStory(page,"Три строки\nДля проверки\nПолей драйвера",new());
        var safe=PageLayout.PrintSafeArea(page.Document.Paper.OrientedSizeMm,page.Document.Paper.SafeAreaMm,5);
        Assert.Equal(5.08,safe.X,6);
        Assert.False(SheetEditing.HasClipping(page.Snapshot(),safe));
        Assert.InRange(page.Document.Objects.OfType<TextObject>().Single().Placement.BoundsMm.X,safe.X-1e-9,safe.Right+1e-9);
    }
    [Fact]
    public void FinalNewlineDoesNotCreateFalseClippingOnLastNotebookRegion()
    {
        using var vm=Session(); var page=vm.SelectedPage!;
        var source=string.Concat(Enumerable.Range(1,30).Select(i=>$"Строка {i:00}: проверка переноса текста.\n"));
        vm.SetNotebookStory(page,source,new(FontSizePt:14));
        var safe=PageLayout.PrintSafeArea(page.Document.Paper.OrientedSizeMm,page.Document.Paper.SafeAreaMm,5);
        Assert.Empty(NotebookTextLayout.Remainder(page.Document.Snapshot()));
        Assert.False(SheetEditing.HasClipping(page.Snapshot(),safe));
        Assert.Equal(source,string.Concat(page.Document.Objects.OfType<TextObject>().Select(t=>t.Text)));
    }
    private static byte[] Pixels(Bitmap bitmap)
    {
        var data=bitmap.LockBits(new Rectangle(0,0,bitmap.Width,bitmap.Height),ImageLockMode.ReadOnly,bitmap.PixelFormat);
        try { var bytes=new byte[Math.Abs(data.Stride)*data.Height]; Marshal.Copy(data.Scan0,bytes,0,bytes.Length); return bytes; }
        finally { bitmap.UnlockBits(data); }
    }
    [Fact]
    public void EditingCanRemoveOverflowByKeepingOnlyFirstFragment()
    {
        using var vm=Session(); var page=vm.SelectedPage!; var source=string.Concat(Enumerable.Repeat("Текст строки\n",600));
        vm.SetNotebookStory(page,source,new()); var first=page.Document.Objects.OfType<TextObject>().First();
        Assert.NotEmpty(NotebookTextLayout.Remainder(page.Document.Snapshot()));
        vm.BeginTextEditing(new(20,10),first); vm.EditingText=first.Text; vm.FinishTextEditing();
        Assert.Equal(first.Text,page.Document.NotebookText!.Source); Assert.Empty(NotebookTextLayout.Remainder(page.Document.Snapshot()));
    }
    [Fact]
    public void EntirelyOverflowingDraftIsRetainedAfterCommit()
    {
        using var vm=Session(); vm.BeginFlowTextCommand.Execute(null); vm.TextFontSizePt=1000;
        vm.EditingText="Не помещается, но должен сохраниться"; vm.FinishTextEditing();
        Assert.Equal("Не помещается, но должен сохраниться",vm.SelectedPage!.Document.NotebookText!.Source);
        Assert.Equal(vm.SelectedPage.Document.NotebookText.Source,NotebookTextLayout.Remainder(vm.SelectedPage.Document.Snapshot()));
    }
    private static PrintSessionViewModel Session()
    {
        var vm=new PrintSessionViewModel(new QuietDialogs(),Path.Combine(Fixtures.Root,"nb001-"+Guid.NewGuid().ToString("N")));
        vm.AddPage(PrintPageItem.CreateBlank(NotebookProfiles.Default(NotebookVariant.RuledSpread).ToPaper())); return vm;
    }
    private sealed class QuietDialogs : IUserDialogs
    {
        public string[] PickFiles()=>[]; public string? PickPdfOutput()=>null; public string? PickLibreOffice()=>null;
        public bool ConfirmCropping(int count)=>false; public void Error(string message) { }
    }
}
