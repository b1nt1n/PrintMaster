using PrintMaster.Models;
using PrintMaster.Services;
using System.Collections.Immutable;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Xunit;

namespace PrintMaster.Tests;

public sealed class SheetModelTests
{
    private static SheetDocument ImageDocument(string? path=null)=>SheetDocument.FromSource(new(Guid.NewGuid(),SourceKind.Image,path,path,0,1,new(1000,750),new(200,150)));
    private static TextObject Text()=>new(Guid.NewGuid(),new(new(20,20,80,20)),"PrintMaster",new(FontSizePt:18));

    [Fact] public void BlankSheetHasPaperAndNoRequiredSourceFile()
    {
        var paper=new SheetPaper(new(165,205),true,new(5,6,7,8)); var page=PrintPageItem.CreateBlank(paper);
        Assert.Equal(SourceKind.Blank,page.Kind); Assert.Empty(page.OriginalPath); Assert.Empty(page.WorkingPath);
        Assert.Empty(page.Document.Snapshot().Assets); Assert.Empty(page.Document.Objects);
        Assert.Equal(new SizeD(205,165),page.Document.Paper.OrientedSizeMm);
        Assert.Equal(new RectD(5,6,193,151),page.Document.Paper.SafeAreaMm);
    }
    [Fact] public void SourceCropAndPaperPlacementAreIndependent()
    {
        var doc=ImageDocument(); var page=new PrintPageItem(doc); var id=doc.PrimaryContent!.Id;
        page.Edits.SetCrop(new(100,100,800,500)); doc.SetPlacement(id,new(new(10,30,100,70)));
        Assert.Equal(new RectD(100,100,800,500),page.Edits.Crop);
        Assert.Equal(new RectD(10,30,100,70),doc.PrimaryContent!.Placement.BoundsMm);
        Assert.Equal(new SizeD(1000,750),doc.PrimaryAsset!.LogicalSize); Assert.Equal(new SizeD(200,150),doc.PrimaryAsset.PhysicalSizeMm);
    }
    [Fact] public void PrimaryObjectRotationIsPartOfTheRenderedScene()
    {
        var doc=ImageDocument(); var page=new PrintPageItem(doc); doc.RotateObject(doc.PrimaryContent!.Id,30);
        Assert.True(page.Snapshot().IsComposed);
        Assert.Equal(30,page.Snapshot().Sheet!.PrimaryContent!.Placement.RotationDegrees);
        doc.Undo(); Assert.False(page.Snapshot().IsComposed);
    }
    [Fact] public void UnifiedHistoryRestoresCropRotationPlacementStyleAndOrder()
    {
        var doc=ImageDocument(); var page=new PrintPageItem(doc); var text=Text(); var original=doc.Snapshot();
        page.Edits.SetCrop(new(10,20,800,600)); page.RotationQuarterTurns=1; doc.AddObject(text);
        doc.MoveObject(text.Id,15,5); doc.ResizeObject(text.Id,new(50,30));
        var current=(TextObject)doc.Objects.Single(o=>o.Id==text.Id); doc.ReplaceObject(current with { Style=current.Style with { Bold=true,Color="#C62828" } });
        doc.SetObjectOrder(text.Id,0);
        Assert.Equal(text.Id,doc.Objects[0].Id); Assert.Equal(1,page.RotationQuarterTurns);
        for(var i=0;i<7;i++) doc.Undo();
        Assert.Equal(original,doc.Snapshot()); Assert.Equal(0,page.RotationQuarterTurns); Assert.False(page.Edits.CanUndo);
        for(var i=0;i<7;i++) page.Edits.Redo();
        Assert.Equal(text.Id,doc.Objects[0].Id); Assert.Equal(1,page.RotationQuarterTurns);
        var result=(TextObject)doc.Objects[0]; Assert.True(result.Style.Bold); Assert.Equal(new RectD(35,25,50,30),result.Placement.BoundsMm);
    }
    [Fact] public void OldSnapshotAndMutableAnnotationInputCannotBeChangedByFutureEdits()
    {
        var doc=ImageDocument(); var page=new PrintPageItem(doc); var points=new List<PointD> { new(10,10),new(50,50) };
        var annotation=new Annotation(Guid.NewGuid(),AnnotationKind.Ink,new(10,10),new(50,50),"#000000",4,"",36,points);
        page.Edits.Add(annotation); var before=page.Snapshot(); points[0]=new(999,999);
        page.RotationQuarterTurns=2; page.Edits.SetCrop(new(100,100,500,400));
        Assert.Equal(new PointD(10,10),before.Sheet!.PrimaryContent!.Edits.Annotations[0].Points[0]);
        Assert.Equal(0,before.Sheet.PrimaryContent.Edits.QuarterTurns); Assert.Equal(new RectD(0,0,1000,750),before.Edits.Crop);
    }
    [Fact] public void RedoIsClearedWhenANewBranchIsEdited()
    {
        var doc=ImageDocument(); doc.SetContentRotation(1); doc.Undo(); Assert.True(doc.CanRedo);
        doc.AddObject(Text()); Assert.False(doc.CanRedo);
    }
    [Fact] public void ObjectTypesHaveIndependentStylesAndCoordinates()
    {
        var doc=SheetDocument.CreateBlank(SheetPaper.A4); var text=Text();
        var shape=new ShapeObject(Guid.NewGuid(),new(new(10,60,100,30)),SheetShapeKind.Rectangle,new("#175E9C",0.7,0.5,"#C62828"));
        var ink=new InkObject(Guid.NewGuid(),new(new(20,110,50,20)),[new(0,0),new(1,1)],new("#111827",1));
        doc.AddObject(text); doc.AddObject(shape); doc.AddObject(ink); doc.MoveObject(ink.Id,10,10); doc.ResizeObject(ink.Id,new(100,40));
        var moved=(InkObject)doc.Objects[2]; Assert.Equal(ink.Points,moved.Points); Assert.Equal(new RectD(30,120,100,40),moved.Placement.BoundsMm);
        Assert.Equal(shape,doc.Objects[1]); Assert.Equal(text,doc.Objects[0]);
    }
    [Fact] public void RendererUsesMillimetersAndZOrderAtDifferentDpi()
    {
        var doc=SheetDocument.CreateBlank(SheetPaper.A4);
        var red=new ShapeObject(Guid.NewGuid(),new(new(20,20,50,30)),SheetShapeKind.Rectangle,new("#FF0000",0.2,1,"#FF0000"));
        var blue=new ShapeObject(Guid.NewGuid(),new(new(30,30,20,10)),SheetShapeKind.Rectangle,new("#0000FF",0.2,1,"#0000FF"));
        doc.AddObject(red); doc.AddObject(blue); doc.AddObject(Text() with { Placement=new(new(20,70,100,25)) });
        var rasterizer=new PageRasterizer(new());
        foreach(var dpi in new[] { 120,300 })
        {
            using var bitmap=rasterizer.RenderSheet(doc.Snapshot(),dpi,false,default);
            Assert.Equal((int)Math.Ceiling(210/25.4*dpi),bitmap.Width);
            Assert.Equal((int)Math.Ceiling(297/25.4*dpi),bitmap.Height);
            Color At(double x,double y)=>bitmap.GetPixel((int)(x/210*bitmap.Width),(int)(y/297*bitmap.Height));
            Assert.True(At(25,25).R>240 && At(25,25).B<20); Assert.True(At(35,35).B>240 && At(35,35).R<20);
            Assert.Equal(Color.White.ToArgb(),At(80,40).ToArgb());
            bitmap.Save(Path.Combine(Fixtures.Root,$"pm001-sheet-{dpi}dpi.png"),ImageFormat.Png);
        }
        doc.SetObjectOrder(red.Id,1); using var reordered=rasterizer.RenderSheet(doc.Snapshot(),120,false,default);
        Assert.True(reordered.GetPixel((int)(35/25.4*120),(int)(35/25.4*120)).R>240);
    }
    [Fact] public void FixedImagePlacementIsRenderedAtItsDeclaredPaperPosition()
    {
        var path=Fixtures.CreateImage("pm001-source.png"); var doc=ImageDocument(path); var root=doc.PrimaryContent!;
        doc.SetPlacement(root.Id,new(new(40,70,80,60))); using var bitmap=new PageRasterizer(new()).RenderSheet(doc.Snapshot(),120,false,default);
        var outside=bitmap.GetPixel((int)(30/25.4*120),(int)(80/25.4*120)); Assert.Equal(Color.White.ToArgb(),outside.ToArgb());
        var inside=bitmap.GetPixel((int)(85/25.4*120),(int)(100/25.4*120)); Assert.True(inside.R<240);
    }
    [Fact] public async Task SheetExportContainsEditableModelObjectsAtPhysicalSize()
    {
        var doc=SheetDocument.CreateBlank(new(new(148,210),false,SheetMargins.SafeDefault)); doc.AddObject(Text());
        var path=Path.Combine(Fixtures.Root,"pm001-sheet-export-"+Guid.NewGuid().ToString("N")+".png");
        await new ImageExportService(new(new())).ExportSheetAsync(doc.Snapshot(),path,300,default);
        using var image=Image.FromFile(path); Assert.Equal((int)Math.Ceiling(148/25.4*300),image.Width); Assert.Equal((int)Math.Ceiling(210/25.4*300),image.Height);
        Assert.InRange(image.HorizontalResolution,299,301); Assert.True(doc.Objects.Single() is TextObject);
    }
    [Fact] public async Task ImageCopyExportKeepsSourceSemanticsWithAdditionalPaperObjects()
    {
        var path=Fixtures.CreateImage("pm001-copy-source.png"); var source=new SourceAsset(Guid.NewGuid(),SourceKind.Image,path,path,0,1,new(1000,750),new(800/150.0*25.4,600/150.0*25.4));
        var page=PrintPageItem.FromSource(source); page.Document.AddObject(Text() with { Text="Paper-only",Style=new(Color:"#FF0000") });
        var output=await new ImageExportService(new(new())).SaveCopyAsync(page.Snapshot(),300,default);
        using var image=Image.FromFile(output); Assert.Equal(1600,image.Width); Assert.Equal(1200,image.Height);
        Assert.Equal(path,page.OriginalPath); Assert.Equal(2,page.Document.Objects.Count);
    }
    [Fact] public void InvalidCropAndPlacementDoNotEnterHistory()
    {
        var doc=ImageDocument(); var page=new PrintPageItem(doc);
        Assert.Throws<ArgumentException>(()=>page.Edits.SetCrop(new(0,0,double.NaN,1)));
        Assert.Throws<ArgumentException>(()=>doc.AddObject(Text() with { Placement=new(new(double.PositiveInfinity,0,1,1)) }));
        Assert.False(doc.CanUndo); Assert.Single(doc.Objects);
    }
    [Fact] public void SourceReferencesAndDuplicateObjectIdsAreValidated()
    {
        var doc=SheetDocument.CreateBlank(SheetPaper.A4); var text=Text(); doc.AddObject(text);
        Assert.Throws<ArgumentException>(()=>doc.AddObject(text));
        Assert.Throws<ArgumentException>(()=>doc.AddObject(new ContentObject(Guid.NewGuid(),new(new(1,1,10,10)),Guid.NewGuid(),ContentEdits.Empty(new(100,100)))));
    }
    [Fact] public void PaperOrientationAndMarginsAreUndoable()
    {
        var doc=SheetDocument.CreateBlank(SheetPaper.A4); var changed=new SheetPaper(new(165,205),true,new(7,8,9,10));
        doc.SetPaper(changed); Assert.Equal(new SizeD(205,165),doc.Paper.OrientedSizeMm); doc.Undo(); Assert.Equal(SheetPaper.A4,doc.Paper); doc.Redo(); Assert.Equal(changed,doc.Paper);
    }
}
