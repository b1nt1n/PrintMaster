using PrintMaster.Models;
using PrintMaster.Services;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace PrintMaster.Tests;

public sealed class DrawingAppearanceTests
{
    private static ShapeObject Shape(SheetShapeKind kind=SheetShapeKind.Rectangle,string? fill=null,double opacity=1)=>
        new(Guid.NewGuid(),new(new(20,20,40,30),17),kind,new("#204080",0.8,opacity,fill));
    private static Annotation Drawing(AnnotationKind kind=AnnotationKind.Rectangle,string? fill=null,double? opacity=null)=>
        Annotation.Create(kind,new(100,100),new(500,400),"#204080",12,points:[new(100,100),new(500,400)]) with { FillColor=fill,Opacity=opacity };
    private static PrintPageItem Photo()
    {
        var path=Path.Combine(Fixtures.Root,"pm010-white-"+Guid.NewGuid().ToString("N")+".png");
        using(var bitmap=new Bitmap(800,600)) { using(var g=Graphics.FromImage(bitmap)) g.Clear(Color.White); bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png); }
        return PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Image,path,path,0,1,new(800,600),new(80,60)));
    }
    private static StrokeStyle Style(SheetObject item)=>DrawingStyleTests.StyleOf(item);
    private static void SameAnnotation(Annotation expected,Annotation actual)
    {
        // SheetDocument freezes point collections at the model boundary; compare their
        // values separately while retaining strict record equality for every other field.
        Assert.Equal(expected with { Points=actual.Points },actual);
        Assert.Equal(expected.Points.ToArray(),actual.Points.ToArray());
    }
    private static Color Composite(string rgb,double opacity)
    {
        var alpha=(int)Math.Round(255*opacity); var color=ColorTranslator.FromHtml(rgb);
        return Color.FromArgb(255,(int)Math.Round((color.R*alpha+255*(255-alpha))/255d),(int)Math.Round((color.G*alpha+255*(255-alpha))/255d),(int)Math.Round((color.B*alpha+255*(255-alpha))/255d));
    }
    private static void Near(Color expected,Color actual)
    {
        Assert.Equal(255,actual.A); Assert.InRange(actual.R,Math.Max(0,expected.R-2),Math.Min(255,expected.R+2));
        Assert.InRange(actual.G,Math.Max(0,expected.G-2),Math.Min(255,expected.G+2)); Assert.InRange(actual.B,Math.Max(0,expected.B-2),Math.Min(255,expected.B+2));
    }

    [Fact]
    public void FillAndOpacityUseOneUndoEachPreserveGeometryAndKeepCreationDefaults()
    {
        using var vm=DrawingStyleTests.Session(); var page=PrintPageItem.CreateBlank(SheetPaper.A4); var original=Shape();
        page.Document.AddObject(original); vm.AddPage(page); vm.SetObjectSelection([original.Id]); vm.InkColor="#AABBCC"; vm.StrokeThickness=6;
        var before=vm.UndoCount; Assert.False(vm.SelectedDrawingHasFill); Assert.Equal("100",vm.SelectedDrawingOpacityText);
        vm.SelectedDrawingFillColor="c04020"; var filled=(ShapeObject)page.Document.Objects.Single();
        Assert.Equal("#C04020",filled.Style.FillColor); Assert.Equal(original.Placement,filled.Placement); Assert.Equal(original.Style.Color,filled.Style.Color); Assert.Equal(original.Style.WidthMm,filled.Style.WidthMm);
        vm.SelectedDrawingOpacityText="37,5"; var styled=(ShapeObject)page.Document.Objects.Single();
        Assert.Equal(0.375,styled.Style.Opacity); Assert.Equal(filled.Style.FillColor,styled.Style.FillColor); Assert.Equal(before+2,vm.UndoCount);
        Assert.Equal("#AABBCC",vm.InkColor); Assert.Equal(6,vm.StrokeThickness);
        vm.UndoCommand.Execute(null); Assert.Equal(filled,page.Document.Objects.Single()); Assert.Equal("100",vm.SelectedDrawingOpacityText);
        vm.UndoCommand.Execute(null); Assert.Equal(original,page.Document.Objects.Single()); Assert.False(vm.SelectedDrawingHasFill);
        vm.RedoCommand.Execute(null); vm.RedoCommand.Execute(null); Assert.Equal(styled,page.Document.Objects.Single());
        vm.SelectedDrawingHasFill=false; Assert.Null(Style(page.Document.Objects.Single()).FillColor); Assert.Equal(0.375,Style(page.Document.Objects.Single()).Opacity); Assert.Equal(before+3,vm.UndoCount);
        vm.UndoCommand.Execute(null); Assert.Equal(styled,page.Document.Objects.Single());
    }

    [Fact]
    public void GroupFillTouchesOnlyClosedFiguresAndOpacityTouchesTheWholeDrawingSelection()
    {
        using var vm=DrawingStyleTests.Session(); var page=PrintPageItem.CreateBlank(SheetPaper.A4);
        var rectangle=Shape(fill:"Red",opacity:0.4); var ellipse=Shape(SheetShapeKind.Ellipse,opacity:0.7);
        var arrow=Shape(SheetShapeKind.Arrow,"#BADBAD",0.2); var ink=new InkObject(Guid.NewGuid(),new(new(70,20,20,30)),[new(0,0),new(1,1)],new("#102030",2,0.3,"#ABCDE0"));
        page.Document.AddObjects([rectangle,ellipse,arrow,ink]); vm.AddPage(page); vm.SetObjectSelection([rectangle.Id,ellipse.Id,arrow.Id,ink.Id]);
        Assert.Null(vm.SelectedDrawingHasFill); Assert.True(vm.HasMixedDrawingFillColor); Assert.True(vm.HasMixedDrawingOpacity); Assert.Empty(vm.SelectedDrawingOpacityText);
        var before=vm.UndoCount; vm.SelectedDrawingOpacityText=""; vm.SelectedDrawingHasFill=null; Assert.Equal(before,vm.UndoCount);
        vm.SelectedDrawingFillColor="#00aa55";
        Assert.All(page.Document.Objects.Take(2),o=>Assert.Equal("#00AA55",Style(o).FillColor)); Assert.Equal(arrow,page.Document.Objects[2]); Assert.Equal(ink,page.Document.Objects[3]); Assert.Equal(before+1,vm.UndoCount);
        Assert.True(vm.SelectedDrawingHasFill); Assert.False(vm.HasMixedDrawingFillColor);
        vm.SelectedDrawingOpacityText="50"; Assert.All(page.Document.Objects,o=>Assert.Equal(0.5,Style(o).Opacity)); Assert.False(vm.HasMixedDrawingOpacity); Assert.Equal(before+2,vm.UndoCount);
        Assert.Equal("#BADBAD",Style(page.Document.Objects[2]).FillColor); Assert.Equal("#ABCDE0",Style(page.Document.Objects[3]).FillColor);
        vm.UndoCommand.Execute(null); Assert.True(vm.HasMixedDrawingOpacity); vm.UndoCommand.Execute(null);
        Assert.Equal(new SheetObject[] {rectangle,ellipse,arrow,ink},page.Document.Objects); Assert.Null(vm.SelectedDrawingHasFill);
        Assert.Equal(new[] {rectangle.Id,ellipse.Id,arrow.Id,ink.Id},vm.SelectedObjectIds);
    }

    [Theory]
    [InlineData("-0.01")] [InlineData("100.01")] [InlineData("NaN")] [InlineData("Infinity")] [InlineData("bad")]
    public void InvalidOpacityNeverChangesTheSceneOrUndoStack(string value)
    {
        using var vm=DrawingStyleTests.Session(); var page=PrintPageItem.CreateBlank(SheetPaper.A4); var original=Shape();
        page.Document.AddObject(original); vm.AddPage(page); vm.SetObjectSelection([original.Id]); var revision=page.Document.Revision; var before=vm.UndoCount;
        vm.SelectedDrawingOpacityText=value;
        Assert.True(vm.HasDrawingStyleError); Assert.Equal(original,page.Document.Objects.Single()); Assert.Equal(revision,page.Document.Revision); Assert.Equal(before,vm.UndoCount);
        vm.SelectedDrawingOpacityText="0"; Assert.False(vm.HasDrawingStyleError); Assert.Equal(0,Style(page.Document.Objects.Single()).Opacity); Assert.Equal(before+1,vm.UndoCount);
    }

    [Fact]
    public void EquivalentStylesAndUntouchedMarkerPercentPreserveRedoAndNullLegacyOpacity()
    {
        using var vm=DrawingStyleTests.Session(); var page=PrintPageItem.CreateBlank(SheetPaper.A4); var original=Shape(fill:"Red",opacity:0.375);
        page.Document.AddObject(original); vm.AddPage(page); vm.SetObjectSelection([original.Id]); vm.SelectedDrawingOpacityText="60"; vm.UndoCommand.Execute(null);
        var before=vm.UndoCount; var redo=vm.RedoCount; var revision=page.Document.Revision;
        vm.SelectedDrawingFillColor="#ff0000"; vm.SelectedDrawingHasFill=true; vm.SelectedDrawingOpacityText="37.5000"; vm.SelectedDrawingFillColor="bad";
        Assert.Equal(original,page.Document.Objects.Single()); Assert.Equal(before,vm.UndoCount); Assert.Equal(redo,vm.RedoCount); Assert.Equal(revision,page.Document.Revision);
        var photo=Photo(); var marker=Drawing(AnnotationKind.Marker); photo.Edits.Add(marker); vm.AddPage(photo); vm.SelectedPage=photo; vm.SelectedSourceAnnotation=marker;
        before=vm.UndoCount; revision=photo.Document.Revision; vm.SelectedDrawingOpacityText=vm.SelectedDrawingOpacityText;
        Assert.Equal(before,vm.UndoCount); Assert.Equal(revision,photo.Document.Revision); Assert.Null(photo.Edits.Annotations.Single().Opacity); Assert.Equal(85d/255,marker.EffectiveOpacity);
    }

    [Theory] [InlineData(AnnotationKind.Rectangle)] [InlineData(AnnotationKind.Ellipse)]
    public void SourceFigureAppearanceKeepsCropRotationGeometryAndCanUndo(AnnotationKind kind)
    {
        using var vm=DrawingStyleTests.Session(); var page=Photo(); var original=Drawing(kind);
        page.Edits.Add(original); page.Edits.SetCrop(new(50,50,700,500)); page.RotationQuarterTurns=3; vm.AddPage(page); vm.SelectedSourceAnnotation=original; var before=vm.UndoCount;
        vm.SelectedDrawingFillColor="#A03060"; vm.SelectedDrawingOpacityText="25"; var updated=page.Edits.Annotations.Single();
        Assert.Equal("#A03060",updated.FillColor); Assert.Equal(0.25,updated.Opacity); Assert.Equal(original.Start,updated.Start); Assert.Equal(original.End,updated.End);
        Assert.Equal(original.Points.ToArray(),updated.Points.ToArray()); Assert.Equal(original.Color,updated.Color); Assert.Equal(original.Thickness,updated.Thickness);
        Assert.Equal(new RectD(50,50,700,500),page.Edits.Crop); Assert.Equal(3,page.RotationQuarterTurns); Assert.Equal(before+2,vm.UndoCount);
        vm.UndoCommand.Execute(null); Assert.Null(page.Edits.Annotations.Single().Opacity); vm.UndoCommand.Execute(null); SameAnnotation(original,page.Edits.Annotations.Single());
        vm.RedoCommand.Execute(null); vm.RedoCommand.Execute(null); SameAnnotation(updated,page.Edits.Annotations.Single());
    }

    [Theory] [InlineData(AnnotationKind.Arrow)] [InlineData(AnnotationKind.Ink)] [InlineData(AnnotationKind.Marker)]
    public void OpenDrawingHasOpacityButCannotAcquireFill(AnnotationKind kind)
    {
        using var vm=DrawingStyleTests.Session(); var page=Photo(); var original=Drawing(kind,fill:"#BBBBBB"); page.Edits.Add(original); vm.AddPage(page); vm.SelectedSourceAnnotation=original;
        Assert.True(vm.ShowSelectedDrawingProperties); Assert.False(vm.ShowSelectedDrawingFillProperties); Assert.False(vm.CanChangeDrawingFill);
        var before=vm.UndoCount; vm.SelectedDrawingFillColor="#CC0000"; vm.SelectedDrawingHasFill=false; Assert.Equal(before,vm.UndoCount); SameAnnotation(original,page.Edits.Annotations.Single());
        vm.SelectedDrawingOpacityText="75"; Assert.Equal(0.75,page.Edits.Annotations.Single().Opacity); Assert.Equal("#BBBBBB",page.Edits.Annotations.Single().FillColor);
        vm.UndoCommand.Execute(null); SameAnnotation(original,page.Edits.Annotations.Single());
    }

    [Fact]
    public void BusyPrintViewTextAndLockedSelectionCannotChangeDrawingAppearance()
    {
        using var vm=DrawingStyleTests.Session(); var page=PrintPageItem.CreateBlank(SheetPaper.A4); var shape=Shape(); page.Document.AddObject(shape); vm.AddPage(page); vm.SetObjectSelection([shape.Id]);
        var before=vm.UndoCount; vm.IsBusy=true; vm.SelectedDrawingFillColor="#FF0000"; vm.SelectedDrawingOpacityText="25"; vm.IsBusy=false;
        vm.ShowPrintPreview=true; vm.SelectedDrawingHasFill=true; vm.SelectedDrawingOpacityText="25"; vm.ShowPrintPreview=false;
        Assert.Equal(shape,page.Document.Objects.Single()); Assert.Equal(before,vm.UndoCount);
        page.Document.ReplaceObject(shape with { Locked=true }); vm.SelectedDrawingFillColor="#FF0000"; vm.SelectedDrawingOpacityText="25";
        Assert.False(vm.CanChangeDrawingStyle); Assert.Equal(shape.Style,((ShapeObject)page.Document.Objects.Single()).Style);
        var photo=Photo(); var text=Annotation.Create(AnnotationKind.Text,new(100,100),new(300,200),"#204080",4,"Text",24); photo.Edits.Add(text); vm.AddPage(photo); vm.SelectedPage=photo; vm.SelectedSourceAnnotation=text;
        before=vm.UndoCount; vm.SelectedDrawingOpacityText="0"; vm.SelectedDrawingFillColor="#FF0000"; SameAnnotation(text,photo.Edits.Annotations.Single()); Assert.Equal(before,vm.UndoCount);
    }

    [Theory] [InlineData(-0.01)] [InlineData(1.01)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void ModelRejectsInvalidAnnotationOpacityBeforeMutation(double value)
    {
        var page=Photo(); var before=page.Document.Snapshot(); var revision=page.Document.Revision;
        Assert.Throws<ArgumentException>(()=>page.Edits.Add(Drawing(opacity:value)));
        Assert.Equal(before,page.Document.Snapshot()); Assert.Equal(revision,page.Document.Revision);
    }

    [Fact]
    public void ModelRejectsMalformedFillButSupportsTransparentAnnotation()
    {
        var page=Photo(); Assert.Throws<ArgumentException>(()=>page.Edits.Add(Drawing(fill:"not-a-color")));
        var invisible=Drawing(fill:"#FF0000",opacity:0); page.Edits.Add(invisible); SameAnnotation(invisible,page.Edits.Annotations.Single());
    }

    [Fact]
    public void OldAnnotationJsonKeepsExactMarkerAlphaAndOmitsComputedOpacity()
    {
        var old=Drawing(AnnotationKind.Marker) with { Color="#000000",Thickness=20,Start=new(100,100),End=new(500,100),Points=new[] {new PointD(100,100),new PointD(500,100)} };
        var node=JsonSerializer.SerializeToNode(old)!.AsObject(); Assert.False(node.ContainsKey(nameof(Annotation.EffectiveOpacity))); node.Remove(nameof(Annotation.Opacity)); node.Remove(nameof(Annotation.FillColor));
        var restored=JsonSerializer.Deserialize<Annotation>(node.ToJsonString())!; Assert.Null(restored.Opacity); Assert.Null(restored.FillColor); Assert.Equal(85d/255,restored.EffectiveOpacity);
        using var bitmap=new Bitmap(800,600); using(var g=Graphics.FromImage(bitmap)) { g.Clear(Color.White); PageRasterizer.DrawAnnotation(g,restored); }
        Near(Color.FromArgb(170,170,170),bitmap.GetPixel(300,100));
    }

    [Fact]
    public void ClipboardCloneConversionAndJsonPreserveAppearance()
    {
        var original=Drawing(fill:"#A03060",opacity:0.37); var clone=ClipboardAnnotationService.Clone(original,new(800,600),new(400,300));
        Assert.NotEqual(original.Id,clone.Id); Assert.Equal(original.FillColor,clone.FillColor); Assert.Equal(original.Opacity,clone.Opacity); Assert.Equal(original.Thickness/2,clone.Thickness);
        var package=ClipboardObjectService.Deserialize(ClipboardObjectService.Serialize(new(1,null,[],clone,new(400,300)))); Assert.Equal(clone.Opacity,package.Annotation!.Opacity); Assert.Equal(clone.FillColor,package.Annotation.FillColor);
        var sheet=(ShapeObject)ClipboardAnnotationService.ToSheet(original,new(800,600),SheetPaper.A4.SafeAreaMm);
        Assert.Equal(0.37,sheet.Style.Opacity); Assert.Equal("#A03060",sheet.Style.FillColor); Assert.Equal(original.Color,sheet.Style.Color);
        var marker=(InkObject)ClipboardAnnotationService.ToSheet(Drawing(AnnotationKind.Marker),new(800,600),SheetPaper.A4.SafeAreaMm); Assert.Equal(85d/255,marker.Style.Opacity);
    }

    [Fact]
    public async Task PortableProjectPreservesNewAppearanceAndOldVersionFiveWithoutFields()
    {
        var photo=Photo(); var annotation=Drawing(fill:"#A03060",opacity:0.37); photo.Edits.Add(annotation);
        var sheet=PrintPageItem.CreateBlank(SheetPaper.A4); var shape=Shape(fill:"#0088FF",opacity:0.6); sheet.Document.AddObject(shape);
        var service=new ProjectFileService(); var path=ProjectFileTests.PathFor("pm010-style"); await service.SaveAsync(ProjectFileTests.Session(photo,sheet),path,true,default);
        using(var zip=ZipFile.OpenRead(path))
        using(var reader=new StreamReader(zip.GetEntry("manifest.json")!.Open())) Assert.Equal(ProjectFileService.CurrentVersion,JsonNode.Parse(reader.ReadToEnd())!["version"]!.GetValue<int>());
        using(var workspace=new SessionWorkspace())
        {
            var result=await service.LoadAsync(path,workspace,default); Assert.Empty(result.Missing);
            var stored=result.Session.Pages[0].Sheet.PrimaryContent!.Edits.Annotations.Single(); Assert.Equal(annotation.FillColor,stored.FillColor); Assert.Equal(annotation.Opacity,stored.Opacity);
            Assert.Equal(shape.Style,result.Session.Pages[1].Sheet.Objects.OfType<ShapeObject>().Single().Style);
        }
        var legacy=Photo(); legacy.Edits.Add(Drawing(AnnotationKind.Marker)); var oldPath=ProjectFileTests.PathFor("pm010-old-five"); await service.SaveAsync(ProjectFileTests.Session(legacy),oldPath,true,default);
        using(var zip=ZipFile.Open(oldPath,ZipArchiveMode.Update))
        {
            var entry=zip.GetEntry("manifest.json")!; JsonObject node; using(var reader=new StreamReader(entry.Open())) node=JsonNode.Parse(reader.ReadToEnd())!.AsObject(); entry.Delete(); node["version"]=5;
            var stored=node["session"]!["pages"]![0]!["sheet"]!["objects"]![0]!["edits"]!["annotations"]![0]!.AsObject(); stored.Remove("opacity"); stored.Remove("fillColor");
            using var writer=new StreamWriter(zip.CreateEntry("manifest.json").Open()); writer.Write(node.ToJsonString());
        }
        using var oldWorkspace=new SessionWorkspace(); var loaded=await service.LoadAsync(oldPath,oldWorkspace,default); var oldAnnotation=loaded.Session.Pages[0].Sheet.PrimaryContent!.Edits.Annotations.Single();
        Assert.Null(oldAnnotation.Opacity); Assert.Null(oldAnnotation.FillColor); Assert.Equal(85d/255,oldAnnotation.EffectiveOpacity);
    }

    [Theory] [InlineData(SheetShapeKind.Rectangle,300)] [InlineData(SheetShapeKind.Rectangle,600)] [InlineData(SheetShapeKind.Ellipse,300)] [InlineData(SheetShapeKind.Ellipse,600)]
    public async Task SheetFillOpacityReachColorExportAndMonochromeRaster(SheetShapeKind kind,int dpi)
    {
        using var vm=DrawingStyleTests.Session(); var page=PrintPageItem.CreateBlank(new(new(80,80),false,SheetMargins.SafeDefault)); var shape=Shape(kind) with { Placement=new(new(20,20,40,30)) };
        page.Document.AddObject(shape); vm.AddPage(page); vm.SetObjectSelection([shape.Id]); var rasterizer=new PageRasterizer(new());
        using(var before=rasterizer.RenderSheet(page.Document.Snapshot(),dpi,false,default)) Assert.Equal(Color.White.ToArgb(),before.GetPixel(before.Width/2,(int)(35d/80*before.Height)).ToArgb());
        vm.SelectedDrawingFillColor="#000000"; vm.SelectedDrawingOpacityText="50";
        using(var colored=rasterizer.RenderSheet(page.Document.Snapshot(),dpi,false,default)) Near(Composite("#000000",0.5),colored.GetPixel(colored.Width/2,(int)(35d/80*colored.Height)));
        var path=Path.Combine(Fixtures.Root,"pm010-sheet-"+Guid.NewGuid().ToString("N")+".png"); await new ImageExportService(rasterizer).ExportSheetAsync(page.Document.Snapshot(),path,dpi,default);
        using(var exported=new Bitmap(path)) { Near(Composite("#000000",0.5),exported.GetPixel(exported.Width/2,(int)(35d/80*exported.Height))); Assert.InRange(exported.HorizontalResolution,dpi-0.01f,dpi+0.01f); }
        var pixels=(int)Math.Ceiling(80/25.4*dpi); using var print=rasterizer.RenderProcessed(page.Snapshot(),pixels,pixels,new(0,0,pixels,pixels),MonochromeMode.Text,160,dpi,default,new(0,0,80,80));
        Assert.Equal(Color.Black.ToArgb(),print.GetPixel(pixels/2,(int)(35d/80*pixels)).ToArgb()); Assert.Equal(Color.White.ToArgb(),print.GetPixel(10,10).ToArgb());
        Assert.Equal("#000000",((ShapeObject)page.Document.Objects.Single()).Style.FillColor); Assert.Equal(0.5,((ShapeObject)page.Document.Objects.Single()).Style.Opacity);
    }

    [Theory] [InlineData(AnnotationKind.Rectangle,300)] [InlineData(AnnotationKind.Rectangle,600)] [InlineData(AnnotationKind.Ellipse,300)] [InlineData(AnnotationKind.Ellipse,600)]
    public async Task PhotoFillOpacityReachEditedCopyWithoutChangingSource(AnnotationKind kind,int dpi)
    {
        var photo=Photo(); var bytes=await File.ReadAllBytesAsync(photo.OriginalPath); var annotation=Drawing(kind,fill:"#204080",opacity:0.5); photo.Edits.Add(annotation);
        var rasterizer=new PageRasterizer(new()); using(var preview=rasterizer.Render(photo.Snapshot(),800,600,false,default))
        {
            Near(Composite("#204080",0.5),preview.GetPixel(300,250));
            // Outside the fill but inside a rectangle's outline: alpha applies to the stroke as well.
            if(kind==AnnotationKind.Rectangle) Near(Composite("#204080",0.5),preview.GetPixel(300,96));
            Assert.Equal(Color.White.ToArgb(),preview.GetPixel(50,50).ToArgb());
        }
        var path=Path.Combine(Fixtures.Root,"pm010-photo-"+Guid.NewGuid().ToString("N")+".png"); await new ImageExportService(rasterizer).SaveCopyAsync(photo.Snapshot(),dpi,default,path);
        using(var exported=new Bitmap(path))
        {
            Near(Composite("#204080",0.5),exported.GetPixel((int)(300d/800*exported.Width),(int)(250d/600*exported.Height)));
            if(kind==AnnotationKind.Rectangle) Near(Composite("#204080",0.5),exported.GetPixel((int)(300d/800*exported.Width),(int)(96d/600*exported.Height)));
            Assert.InRange(exported.HorizontalResolution,dpi-0.01f,dpi+0.01f);
        }
        Assert.Equal(bytes,await File.ReadAllBytesAsync(photo.OriginalPath)); SameAnnotation(annotation,photo.Edits.Annotations.Single());
    }

    [Fact]
    public void EmbeddedPhotoAnnotationUsesTheSameOpacityAndFillOnTheComposedSheet()
    {
        var photo=Photo(); photo.Edits.Add(Drawing(fill:"#204080",opacity:0.5)); photo.Document.SetPaper(new(new(80,80),false,SheetMargins.SafeDefault));
        photo.Document.SetPlacement(photo.Document.PrimaryContent!.Id,new(new(20,20,40,30))); var rasterizer=new PageRasterizer(new());
        using var raster=rasterizer.RenderSheet(photo.Document.Snapshot(),254,false,default); Near(Composite("#204080",0.5),raster.GetPixel(350,325));
    }

    [Fact]
    public async Task CancelledAppearanceExportPreservesBothTheDestinationAndTheSource()
    {
        var photo=Photo(); photo.Edits.Add(Drawing(fill:"#204080",opacity:0.5)); var original=await File.ReadAllBytesAsync(photo.OriginalPath);
        var path=Path.Combine(Fixtures.Root,"pm010-cancel-"+Guid.NewGuid().ToString("N")+".png"); var previous=new byte[] {7,8,9}; await File.WriteAllBytesAsync(path,previous);
        using var cancellation=new CancellationTokenSource(); cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new ImageExportService(new(new())).SaveCopyAsync(photo.Snapshot(),600,cancellation.Token,path));
        Assert.Equal(previous,await File.ReadAllBytesAsync(path)); Assert.Equal(original,await File.ReadAllBytesAsync(photo.OriginalPath)); Assert.Equal(0.5,photo.Edits.Annotations.Single().Opacity);
    }
}
