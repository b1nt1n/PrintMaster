using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.Collections.Immutable;
using System.Drawing;
using System.IO;
using Xunit;

namespace PrintMaster.Tests;

public sealed class BlankSheetTests
{
    [Theory]
    [InlineData(PaperPreset.A4,false,210,297)] [InlineData(PaperPreset.A5,true,210,148)]
    [InlineData(PaperPreset.Notebook,false,167,208)] [InlineData(PaperPreset.Notebook,true,167,208)]
    public void DialogCreatesOrientedSheetsWithSafeMargins(PaperPreset preset,bool landscape,double width,double height)
    {
        var model=new NewSheetViewModel(new() { PrinterName="HP LaserJet Professional P1102",Paper=preset,Landscape=landscape });
        Assert.True(model.CanCreate); Assert.NotNull(model.Result); Assert.Equal(new SizeD(width,height),model.Result.OrientedSizeMm);
        Assert.Equal(new RectD(5,5,width-10,height-10),model.Result.SafeAreaMm);
    }
    [Fact]
    public void CustomDialogValidatesNumbersAndHpFeedDimensions()
    {
        var model=new NewSheetViewModel(new() { PrinterName="HP LaserJet Professional P1102",Paper=PaperPreset.Custom });
        model.WidthText="164,5"; model.HeightText="204.5"; Assert.True(model.CanCreate); Assert.Equal(new SizeD(164.5,204.5),model.Result!.SizeMm);
        model.WidthText="bad"; Assert.False(model.CanCreate); Assert.Null(model.Result);
        model.WidthText="NaN"; Assert.False(model.CanCreate); model.WidthText="217"; Assert.False(model.CanCreate);
        model.WidthText="76"; model.HeightText="127"; Assert.True(model.CanCreate);
        model.Landscape=true; Assert.Equal(new SizeD(127,76),model.Result!.OrientedSizeMm);
        var pdf=new NewSheetViewModel(new() { PrinterName="Microsoft Print to PDF",Paper=PaperPreset.Custom });
        pdf.WidthText="300"; pdf.HeightText="400"; Assert.True(pdf.CanCreate);
    }
    [Fact]
    public void ImageInsertionUndoRemovesBothObjectAndMaterialAndRedoRestores()
    {
        var sheet=SheetDocument.CreateBlank(SheetPaper.A4);
        var source=new SourceAsset(Guid.NewGuid(),SourceKind.Image,"image.png","working.png",0,1,new(800,600),new(200,150));
        var photo=new ContentObject(Guid.NewGuid(),new(new(15,25,80,60)),source.Id,ContentEdits.Empty(source.LogicalSize));
        sheet.AddContent(source,photo); Assert.Single(sheet.Objects); Assert.Single(sheet.Snapshot().Assets);
        sheet.Undo(); Assert.Empty(sheet.Objects); Assert.Empty(sheet.Snapshot().Assets); sheet.Redo();
        Assert.Equal(photo,sheet.Objects.Single()); Assert.Equal(source,sheet.Snapshot().Assets.Single()); Assert.Null(sheet.PrimaryContent);
    }
    [Fact]
    public void WrappedTextRetainsFullTextAndHeightIncludesAllLines()
    {
        var style=new TextStyle(FontSizePt:14); var safe=SheetPaper.A4.SafeAreaMm;
        var text=string.Join(" ",Enumerable.Repeat("Длинный текст для печати без потери слов.",12));
        var wide=SheetEditing.CreateText(text,style,new(10,10),safe);
        var narrow=SheetEditing.CreateText(text,style,new(10,10),safe,wide with { Placement=new(new(10,10,60,20)) });
        Assert.Equal(text,narrow.Text); Assert.True(narrow.Placement.BoundsMm.Height>wide.Placement.BoundsMm.Height);
        Assert.False(SheetEditing.Outside(wide,safe));
        var bottom=SheetEditing.CreateText(text,style,new(10,280),safe); Assert.True(SheetEditing.Outside(bottom,safe));
    }
    [Fact]
    public void AlignAccountsForStrokeRotationAndArrowHead()
    {
        var safe=SheetPaper.A4.SafeAreaMm;
        var rectangle=new ShapeObject(Guid.NewGuid(),new(new(20,30,50,20),90),SheetShapeKind.Rectangle,new(WidthMm:1));
        var aligned=rectangle with { Placement=SheetEditing.Align(rectangle,safe,"left") };
        Assert.Equal(safe.X,SheetEditing.VisualBounds(aligned).X,6);
        aligned=aligned with { Placement=SheetEditing.Align(aligned,safe,"bottom") };
        Assert.Equal(safe.Bottom,SheetEditing.VisualBounds(aligned).Bottom,6); Assert.False(SheetEditing.Outside(aligned,safe));
        var arrow=new ShapeObject(Guid.NewGuid(),new(new(30,5,50,0.01)),SheetShapeKind.Arrow,new(WidthMm:0.4));
        Assert.True(SheetEditing.Outside(arrow,safe)); Assert.True(SheetEditing.VisualBounds(arrow).Height>1);
        Assert.False(SheetEditing.Outside(arrow with { Visible=false },safe));
    }
    [Fact]
    public void PrintableAlignmentUsesTheSameUnitsAsMarginBounds()
    {
        var safe=PageLayout.PrintSafeArea(new(210,297),new(4,4,202,289),5);
        Assert.Equal(5.08,safe.X,6); Assert.Equal(5.08,safe.Y,6);
        Assert.Equal(PageLayout.HundredthsToMm(827-20),safe.Right,6);
        Assert.Equal(PageLayout.HundredthsToMm(1169-20),safe.Bottom,6);
    }
    [Fact]
    public void DragPreviewKeepsTransparentPixelsAndMatchesPlacement()
    {
        var sheet=SheetDocument.CreateBlank(SheetPaper.A4);
        sheet.AddObject(new ShapeObject(Guid.NewGuid(),new(new(20,30,40,20)),SheetShapeKind.Rectangle,new("#000000",0.2,1,"#000000")));
        using var raster=new PageRasterizer(new()).RenderSheet(sheet.Snapshot(),96,false,default,1600,true);
        Assert.Equal(0,raster.GetPixel(0,0).A); Assert.Equal(255,raster.GetPixel(120,150).A); Assert.Equal(0,raster.GetPixel(120,150).R);
    }
    [Fact] [Trait("Category","Printer")]
    public async Task BlankLayoutPrintsA5LandscapeCopiesAndRejectsUnsafeObjects()
    {
        var printer=PrinterInterop.ListPrinters().First(n=>n.Equals("Microsoft Print to PDF",StringComparison.OrdinalIgnoreCase));
        var page=PrintPageItem.CreateBlank(new(new(148,210),true,SheetMargins.SafeDefault)); page.Copies=2;
        page.Document.AddObject(new ShapeObject(Guid.NewGuid(),new(new(20,30,40,15)),SheetShapeKind.Rectangle,new("#000000",0.2,1,"#000000")));
        page.Document.AddObject(SheetEditing.CreateText("Новый лист: текст, фигуры и фотографии.",new(FontSizePt:14),new(20,60),page.Document.Paper.SafeAreaMm));
        var path=Path.Combine(Fixtures.Root,"pm003-a5-landscape-2-copies.pdf"); if(File.Exists(path)) File.Delete(path);
        var engine=new PrintEngine(new(new()),new());
        var settings=new PrintSettings { PrinterName=printer,Paper=PaperPreset.A5,Landscape=true,Dpi=600 };
        var profile=new PrinterInterop().Probe(settings.Snapshot());
        var safe=PageLayout.PrintSafeArea(profile.ActualPaperMm,profile.PrintableMm,5);
        var edge=new ShapeObject(Guid.NewGuid(),new(new(20,30,10,5)),SheetShapeKind.Rectangle,new("#000000",0.4));
        edge=edge with { Placement=SheetEditing.Align(edge,safe,"right") }; edge=edge with { Placement=SheetEditing.Align(edge,safe,"bottom") };
        Assert.False(SheetEditing.Outside(edge,safe)); page.Document.AddObject(edge);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await engine.PrintAsync([page.Snapshot()],settings.Snapshot(path),null,timeout.Token);
        for(var i=0;i<100 && !File.Exists(path);i++) await Task.Delay(100);
        var pdf=new PdfiumRenderer(); var pages=pdf.ReadPages(path); Assert.Equal(2,pages.Count);
        Assert.InRange(pages[0].Width/72*25.4,209,211); Assert.InRange(pages[0].Height/72*25.4,147,149);
        using var bitmap=pdf.Render(path,0,840,592,0,false,default);
        Assert.True(bitmap.GetPixel(85,125).R<30); Assert.True(bitmap.GetPixel(70,125).R>240); Assert.True(bitmap.GetPixel(250,125).R>240);
        bitmap.Save(Path.Combine(Fixtures.Root,"pm003-a5-pdf-preview.png"));
        page.Document.MoveObject(page.Document.Objects[0].Id,-20,0);
        var rejected=Path.Combine(Fixtures.Root,"pm003-unsafe-rejected.pdf"); if(File.Exists(rejected)) File.Delete(rejected);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>engine.PrintAsync([page.Snapshot()],settings.Snapshot(rejected),null,default));
        Assert.False(File.Exists(rejected));
    }
    [Fact]
    public void NotebookRasterRetainsItsPhysicalSizeAt600Dpi()
    {
        var sheet=SheetDocument.CreateBlank(new(new(165,205),true,SheetMargins.SafeDefault));
        sheet.AddObject(new ShapeObject(Guid.NewGuid(),new(new(20,30,40,15)),SheetShapeKind.Rectangle,new("#000000",0.2,1,"#000000")));
        using var raster=new PageRasterizer(new()).RenderSheet(sheet.Snapshot(),600,true,default);
        Assert.Equal((int)Math.Ceiling(205*600/25.4),raster.Width); Assert.Equal((int)Math.Ceiling(165*600/25.4),raster.Height);
        Assert.Equal(0,raster.GetPixel((int)(25*600/25.4),(int)(35*600/25.4)).R);
        Assert.Equal(255,raster.GetPixel((int)(18*600/25.4),(int)(35*600/25.4)).R);
    }
}
