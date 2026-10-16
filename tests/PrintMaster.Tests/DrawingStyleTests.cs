using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.Drawing;
using System.IO;
using Xunit;

namespace PrintMaster.Tests;

public sealed class DrawingStyleTests
{
    internal static PrintSessionViewModel Session()=>new(new ClipboardTests.TestDialogs(),Path.Combine(Fixtures.Root,"ux008-recovery-"+Guid.NewGuid().ToString("N")),new FakeClipboard());
    internal static StrokeStyle StyleOf(SheetObject item)=>item is ShapeObject shape ? shape.Style : ((InkObject)item).Style;
    private static SheetObject Drawing(string kind)=>kind switch
    {
        "Ink" or "Marker"=>new InkObject(Guid.NewGuid(),new(new(25,35,40,20),23),[new(0,0),new(0.4,0.8),new(1,1)],new("#175E9C",0.6,kind=="Marker" ? 0.3 : 1)),
        _=>new ShapeObject(Guid.NewGuid(),new(new(25,35,40,20),23),Enum.Parse<SheetShapeKind>(kind),new("#175E9C",0.6,0.7,"#EEEEEE"))
    };
    internal static PrintPageItem ImagePage()
    {
        var path=Path.Combine(Fixtures.Root,"ux008-white.png");
        if(!File.Exists(path)) { using var image=new Bitmap(800,600); using(var g=Graphics.FromImage(image)) g.Clear(Color.White); image.SetResolution(150,150); image.Save(path); }
        return PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Image,path,path,0,1,new(800,600),new(160,120)));
    }

    [Theory]
    [InlineData("Rectangle")] [InlineData("Ellipse")] [InlineData("Arrow")] [InlineData("Ink")] [InlineData("Marker")]
    public void SelectedDrawingChangesOnlyColorAndWidthWithIndependentUndoSteps(string kind)
    {
        using var vm=Session(); var page=PrintPageItem.CreateBlank(SheetPaper.A4); var item=Drawing(kind);
        page.Document.AddObject(item); vm.AddPage(page); vm.SetObjectSelection([item.Id]);
        vm.InkColor="#102030"; vm.StrokeThickness=4.5; var before=vm.UndoCount;
        Assert.True(vm.ShowSelectedDrawingProperties); Assert.Equal("#175E9C",vm.SelectedDrawingColor); Assert.False(vm.ShowNewDrawingProperties);
        vm.SelectedDrawingColor="2a73c5"; vm.SelectedDrawingWidthText="1,75";
        var updated=page.Document.Objects.Single(); var style=StyleOf(updated);
        Assert.Equal("#2A73C5",style.Color); Assert.Equal(1.75,style.WidthMm); Assert.Equal(item.Placement,updated.Placement);
        Assert.Equal(StyleOf(item).Opacity,style.Opacity); Assert.Equal(StyleOf(item).FillColor,style.FillColor);
        if(item is InkObject original) Assert.Equal(original.Points,((InkObject)updated).Points);
        Assert.Equal("#102030",vm.InkColor); Assert.Equal(4.5,vm.StrokeThickness); Assert.Equal(before+2,vm.UndoCount);
        vm.UndoCommand.Execute(null); Assert.Equal(0.6,StyleOf(page.Document.Objects.Single()).WidthMm); Assert.Equal("#2A73C5",vm.SelectedDrawingColor);
        vm.UndoCommand.Execute(null); Assert.Equal(item,page.Document.Objects.Single()); Assert.Equal("#175E9C",vm.SelectedDrawingColor);
        vm.RedoCommand.Execute(null); vm.RedoCommand.Execute(null); Assert.Equal(updated,page.Document.Objects.Single());
        vm.Tool=EditorTool.Brush; Assert.False(vm.ShowSelectedDrawingProperties); Assert.True(vm.ShowNewDrawingProperties);
        Assert.Equal("#102030",vm.InkColor); Assert.Equal(4.5,vm.StrokeThickness);
    }

    [Theory]
    [InlineData(AnnotationKind.Arrow)] [InlineData(AnnotationKind.Rectangle)] [InlineData(AnnotationKind.Ellipse)]
    [InlineData(AnnotationKind.Ink)] [InlineData(AnnotationKind.Marker)]
    public void SourceAnnotationsKeepGeometryAndUseSourceCoordinatesAfterCropAndRotation(AnnotationKind kind)
    {
        using var vm=Session(); var page=ImagePage(); page.Edits.SetCrop(new(30,40,600,500)); page.RotationQuarterTurns=1;
        var item=Annotation.Create(kind,new(80,90),new(400,280),"#175E9C",4,points:[new(80,90),new(150,120),new(400,280)]);
        page.Edits.Add(item); vm.AddPage(page); vm.SelectedSourceAnnotation=item; var before=vm.UndoCount;
        Assert.True(vm.ShowSelectedDrawingProperties); Assert.Equal("Толщина",vm.DrawingWidthLabel);
        vm.SelectedDrawingWidthText="12.5"; vm.SelectedDrawingColor="#FF2080";
        var current=page.Edits.Annotations.Single();
        Assert.Equal(12.5,current.Thickness); Assert.Equal("#FF2080",current.Color); Assert.Equal(item.Start,current.Start); Assert.Equal(item.End,current.End);
        Assert.Equal(item.Points,current.Points); Assert.Equal(item.Kind,current.Kind); Assert.Equal(item.FontSize,current.FontSize);
        Assert.Equal(new RectD(30,40,600,500),page.Edits.Crop); Assert.Equal(1,page.RotationQuarterTurns); Assert.Equal(before+2,vm.UndoCount);
        // Even without a canvas selection event, the inspector reads the newly stored record.
        Assert.Equal("#FF2080",vm.SelectedDrawingColor); Assert.Equal("12.5",vm.SelectedDrawingWidthText.Replace(',','.'));
        vm.UndoCommand.Execute(null); Assert.Equal("#175E9C",vm.SelectedDrawingColor);
        vm.UndoCommand.Execute(null); Assert.Equal(4,page.Edits.Annotations.Single().Thickness);
        vm.RedoCommand.Execute(null); vm.RedoCommand.Execute(null); Assert.Equal("#FF2080",vm.SelectedDrawingColor);
        var count=vm.UndoCount; vm.SourceImageSelected=true; Assert.False(vm.ShowSelectedDrawingProperties);
        vm.SelectedDrawingColor="#000000"; Assert.Equal(count,vm.UndoCount); Assert.Equal("#FF2080",page.Edits.Annotations.Single().Color);
    }

    [Fact]
    public void MixedDrawingGroupChangesOnePropertyInOneUndoAndKeepsOtherStyles()
    {
        using var vm=Session(); var page=PrintPageItem.CreateBlank(SheetPaper.A4);
        var shape=Drawing("Rectangle"); var ink=Drawing("Marker") with { Placement=new(new(80,30,40,20)) };
        ink=((InkObject)ink) with { Style=new("#C62828",2,0.3) };
        var untouched=Drawing("Arrow"); page.Document.AddObjects([shape,ink,untouched]); vm.AddPage(page); vm.SetObjectSelection([shape.Id,ink.Id]);
        Assert.True(vm.HasMixedDrawingColor); Assert.True(vm.HasMixedDrawingWidth); Assert.Empty(vm.SelectedDrawingWidthText);
        var before=vm.UndoCount; vm.SelectedDrawingWidthText=""; Assert.Equal(before,vm.UndoCount);
        vm.SelectedDrawingColor="#0080FF";
        Assert.Equal(before+1,vm.UndoCount); Assert.False(vm.HasMixedDrawingColor); Assert.True(vm.HasMixedDrawingWidth);
        Assert.Equal(new[] {0.6,2},page.Document.Objects.Take(2).Select(o=>StyleOf(o).WidthMm));
        Assert.Equal(new[] {0.7,0.3},page.Document.Objects.Take(2).Select(o=>StyleOf(o).Opacity));
        vm.SelectedDrawingWidthText="1.2"; Assert.False(vm.HasMixedDrawingWidth); Assert.Equal(before+2,vm.UndoCount);
        Assert.All(page.Document.Objects.Take(2),o=>Assert.Equal(1.2,StyleOf(o).WidthMm)); Assert.Equal(untouched,page.Document.Objects[2]);
        vm.UndoCommand.Execute(null); Assert.True(vm.HasMixedDrawingWidth); Assert.False(vm.HasMixedDrawingColor);
        vm.UndoCommand.Execute(null); Assert.True(vm.HasMixedDrawingColor); Assert.Equal(new[] {shape,ink,untouched},page.Document.Objects);
        Assert.Equal(new[] {shape.Id,ink.Id},vm.SelectedObjectIds);
    }

    [Theory]
    [InlineData("0")] [InlineData("-2")] [InlineData("NaN")] [InlineData("Infinity")] [InlineData("bad")] [InlineData("20.01")]
    public void InvalidWidthDoesNotMutateTheDrawingOrHistory(string width)
    {
        using var vm=Session(); var page=PrintPageItem.CreateBlank(SheetPaper.A4); var item=Drawing("Arrow");
        page.Document.AddObject(item); vm.AddPage(page); vm.SetObjectSelection([item.Id]); var count=vm.UndoCount; var revision=page.Edits.Revision;
        vm.SelectedDrawingWidthText=width;
        Assert.True(vm.HasDrawingStyleError); Assert.Equal(item,page.Document.Objects.Single()); Assert.Equal(count,vm.UndoCount); Assert.Equal(revision,page.Edits.Revision);
        vm.SelectedDrawingWidthText="0.9"; Assert.False(vm.HasDrawingStyleError); Assert.Equal(0.9,StyleOf(page.Document.Objects.Single()).WidthMm);
    }

    [Fact]
    public void UntouchedWidthFieldDoesNotRoundTheStoredPhysicalWidth()
    {
        using var vm=Session(); var page=PrintPageItem.CreateBlank(SheetPaper.A4);
        var shape=(ShapeObject)Drawing("Rectangle"); shape=shape with { Style=shape.Style with { WidthMm=4/SheetEditing.DipPerMm } };
        page.Document.AddObject(shape); vm.AddPage(page); vm.SetObjectSelection([shape.Id]);
        var count=vm.UndoCount; var revision=page.Edits.Revision;
        vm.SelectedDrawingWidthText=vm.SelectedDrawingWidthText;
        Assert.Equal(shape,page.Document.Objects.Single()); Assert.Equal(count,vm.UndoCount); Assert.Equal(revision,page.Edits.Revision);
    }

    [Fact]
    public void UnchangedColorsIncludingNamedColorsDoNotCreateEditsOrClearRedo()
    {
        using var vm=Session(); var page=PrintPageItem.CreateBlank(SheetPaper.A4);
        var shape=(ShapeObject)Drawing("Rectangle"); shape=shape with { Style=shape.Style with { Color="Red" } };
        page.Document.AddObject(shape); vm.AddPage(page); vm.SetObjectSelection([shape.Id]); vm.SelectedDrawingWidthText="2"; vm.UndoCommand.Execute(null);
        var count=vm.UndoCount; var redo=vm.RedoCount; var revision=page.Edits.Revision;
        vm.SelectedDrawingColor="#ff0000"; vm.SelectedDrawingWidthText="0.600"; vm.SelectedDrawingColor="#notRGB";
        Assert.Equal(count,vm.UndoCount); Assert.Equal(redo,vm.RedoCount); Assert.Equal(revision,page.Edits.Revision); Assert.Equal(shape,page.Document.Objects.Single());
        var image=ImagePage(); var annotation=Annotation.Create(AnnotationKind.Ink,new(50,50),new(100,100),"Red",4,points:[new(50,50),new(100,100)]);
        image.Edits.Add(annotation); vm.AddPage(image); vm.SelectedPage=image; vm.SelectedSourceAnnotation=annotation; count=vm.UndoCount; revision=image.Edits.Revision;
        vm.SelectedDrawingColor="#FF0000"; vm.SelectedDrawingWidthText="4.000";
        Assert.Equal(count,vm.UndoCount); Assert.Equal(revision,image.Edits.Revision);
    }

    [Fact]
    public void UnrelatedObjectsMixedSelectionLockedObjectsAndPrintViewCannotBeRestyled()
    {
        using var vm=Session(); var page=PrintPageItem.CreateBlank(SheetPaper.A4); var shape=Drawing("Rectangle");
        var text=SheetEditing.CreateText("Текст",new(),new(80,70),page.Document.Paper.SafeAreaMm);
        page.Document.AddObjects([shape,text]); vm.AddPage(page); vm.SetObjectSelection([shape.Id,text.Id]); var before=page.Document.Snapshot(); var count=vm.UndoCount;
        Assert.False(vm.ShowSelectedDrawingProperties); vm.SelectedDrawingColor="#FFFFFF"; vm.SelectedDrawingWidthText="8";
        Assert.Equal(before,page.Document.Snapshot()); Assert.Equal(count,vm.UndoCount);
        vm.SetObjectSelection([shape.Id]); vm.Tool=EditorTool.Rectangle; vm.SelectedDrawingWidthText="8"; Assert.Equal(before,page.Document.Snapshot());
        vm.Tool=EditorTool.Select; vm.IsBusy=true; Assert.False(vm.CanChangeDrawingStyle); vm.SelectedDrawingColor="#FFFFFF"; vm.IsBusy=false;
        vm.ShowPrintPreview=true; Assert.False(vm.ShowSelectedDrawingProperties); vm.SelectedDrawingWidthText="8";
        Assert.Equal(before,page.Document.Snapshot()); vm.ShowPrintPreview=false;
        page.Document.ReplaceObject(shape with { Locked=true }); Assert.False(vm.ShowSelectedDrawingProperties); vm.SelectedDrawingColor="#FFFFFF";
        Assert.Equal("#175E9C",StyleOf(page.Document.Objects[0]).Color);
        var image=ImagePage(); var annotation=Annotation.Create(AnnotationKind.Text,new(50,50),new(100,100),"#111827",4,"Текст");
        image.Edits.Add(annotation); vm.AddPage(image); vm.SelectedPage=image; vm.SelectedSourceAnnotation=annotation;
        Assert.False(vm.ShowSelectedDrawingProperties); vm.Tool=EditorTool.Brush; Assert.True(vm.ShowSourceAnnotationDefaults);
        vm.Tool=EditorTool.Select; Assert.False(vm.ShowSourceAnnotationDefaults);
    }

    [Fact]
    public async Task SelectedStylesSurvivePortableProjectForSheetAndSourceAnnotations()
    {
        using var vm=Session(); var page=PrintPageItem.CreateBlank(SheetPaper.A4); var item=Drawing("Marker");
        page.Document.AddObject(item); vm.AddPage(page); vm.SetObjectSelection([item.Id]); vm.SelectedDrawingColor="#37A48F"; vm.SelectedDrawingWidthText="2";
        var image=ImagePage(); var annotation=Annotation.Create(AnnotationKind.Ellipse,new(60,50),new(400,260),"#111827",4);
        image.Edits.Add(annotation); vm.AddPage(image); vm.SelectedPage=image; vm.SelectedSourceAnnotation=annotation;
        vm.SelectedDrawingColor="#B94712"; vm.SelectedDrawingWidthText="10";
        var path=Path.Combine(Fixtures.Root,"ux008-"+Guid.NewGuid().ToString("N")+".printmaster");
        await new ProjectFileService().SaveAsync(vm.CaptureProject(),path,true,default);
        using var workspace=new SessionWorkspace(); var restored=await new ProjectFileService().LoadAsync(path,workspace,default);
        var stored=restored.Session.Pages[0].Sheet.Objects.OfType<InkObject>().Single();
        Assert.Equal("#37A48F",stored.Style.Color); Assert.Equal(2,stored.Style.WidthMm); Assert.Equal(0.3,stored.Style.Opacity); Assert.Equal(((InkObject)item).Points.ToArray(),stored.Points.ToArray());
        var storedAnnotation=restored.Session.Pages[1].Sheet.PrimaryContent!.Edits.Annotations.Single();
        Assert.Equal("#B94712",storedAnnotation.Color); Assert.Equal(10,storedAnnotation.Thickness); Assert.Equal(annotation.Start,storedAnnotation.Start);
    }

    [Fact]
    public async Task SourceAnnotationStylesReachTheEditedCopyInSourceCoordinates()
    {
        using var vm=Session(); var page=ImagePage(); var originalBytes=await File.ReadAllBytesAsync(page.OriginalPath);
        var annotation=Annotation.Create(AnnotationKind.Rectangle,new(80,90),new(400,280),"#111827",4);
        page.Edits.Add(annotation); vm.AddPage(page); vm.SelectedSourceAnnotation=annotation;
        var rasterizer=new PageRasterizer(new());
        using var before=rasterizer.Render(page.Snapshot(),800,600,false,default);
        Assert.Equal(Color.White.ToArgb(),before.GetPixel(250,84).ToArgb());
        vm.SelectedDrawingColor="#33AA77"; vm.SelectedDrawingWidthText="16";
        using var after=rasterizer.Render(page.Snapshot(),800,600,false,default);
        Assert.Equal(ColorTranslator.FromHtml("#33AA77").ToArgb(),after.GetPixel(250,84).ToArgb());
        var path=Path.Combine(Fixtures.Root,"ux008-source-export-"+Guid.NewGuid().ToString("N")+".png");
        await new ImageExportService(rasterizer).SaveCopyAsync(page.Snapshot(),300,default,path);
        using var copy=new Bitmap(path);
        Assert.Equal(ColorTranslator.FromHtml("#33AA77").ToArgb(),copy.GetPixel((int)(250d/800*copy.Width),(int)(84d/600*copy.Height)).ToArgb());
        Assert.InRange(copy.HorizontalResolution,299.99f,300.01f); Assert.Equal(originalBytes,await File.ReadAllBytesAsync(page.OriginalPath));
    }

    [Fact]
    public async Task ChangedStrokeReachesPreviewExportAndPrintRasterWithoutARealPrintJob()
    {
        using var vm=Session(); var page=PrintPageItem.CreateBlank(new(new(80,80),false,SheetMargins.SafeDefault));
        var shape=new ShapeObject(Guid.NewGuid(),new(new(20,30,30,20)),SheetShapeKind.Rectangle,new("#000000",0.2));
        page.Document.AddObject(shape); vm.AddPage(page); vm.SetObjectSelection([shape.Id]); var rasterizer=new PageRasterizer(new());
        using var before=rasterizer.RenderSheet(page.Document.Snapshot(),254,false,default);
        Assert.Equal(Color.White.ToArgb(),before.GetPixel(350,288).ToArgb());
        vm.SelectedDrawingColor="#B94712"; vm.SelectedDrawingWidthText="3";
        using var preview=rasterizer.RenderSheet(page.Document.Snapshot(),254,false,default);
        Assert.Equal(ColorTranslator.FromHtml("#B94712").ToArgb(),preview.GetPixel(350,288).ToArgb());
        var path=Path.Combine(Fixtures.Root,"ux008-export-"+Guid.NewGuid().ToString("N")+".png");
        await new ImageExportService(rasterizer).ExportSheetAsync(page.Document.Snapshot(),path,300,default);
        using var export=new Bitmap(path); Assert.Equal(ColorTranslator.FromHtml("#B94712").ToArgb(),export.GetPixel(413,340).ToArgb());
        var settings=new PrintSettings { PrinterName="Raster test",Monochrome=MonochromeMode.Text,Threshold=160 };
        var printPixels=(int)Math.Ceiling(80/25.4*600);
        using var print=rasterizer.RenderProcessed(page.Snapshot(),printPixels,printPixels,new(0,0,printPixels,printPixels),MonochromeMode.Text,160,600,default,new(0,0,80,80));
        Assert.Equal(Color.Black.ToArgb(),print.GetPixel((int)(35d/80*printPixels),(int)(28.8/80*printPixels)).ToArgb());
        Assert.Equal(Color.White.ToArgb(),print.GetPixel((int)(35d/80*printPixels),(int)(25.5/80*printPixels)).ToArgb());
        using var printPreview=BitmapBridge.ToGdi(rasterizer.RenderPrintPreview(page.Snapshot(),settings.Snapshot(),new(0,0,80,80),default));
        var previewPixel=printPreview.GetPixel((int)(35d/80*printPreview.Width),(int)(28.8/80*printPreview.Height));
        Assert.True(previewPixel.R<20,$"Print preview: {printPreview.Width}x{printPreview.Height}, pixel={previewPixel}");
    }
}
