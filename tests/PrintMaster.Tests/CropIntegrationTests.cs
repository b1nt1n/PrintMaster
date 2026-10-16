using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace PrintMaster.Tests;

public sealed class CropIntegrationTests
{
    private static PrintSessionViewModel Session()=>new(new ClipboardTests.TestDialogs(),
        Path.Combine(Fixtures.Root,"pm008-recovery-"+Guid.NewGuid().ToString("N")),new FakeClipboard());
    private static PrintPageItem Photo(int turns=0)
    {
        var path=Path.Combine(Fixtures.Root,"pm008-source-"+Guid.NewGuid().ToString("N")+".png");
        using(var bitmap=new Bitmap(800,600,PixelFormat.Format32bppArgb))
        {
            bitmap.SetResolution(150,150);
            using var graphics=Graphics.FromImage(bitmap);
            graphics.FillRectangle(Brushes.Red,0,0,400,600);
            graphics.FillRectangle(Brushes.Lime,400,0,400,300);
            graphics.FillRectangle(Brushes.Blue,400,300,400,300);
            bitmap.Save(path,ImageFormat.Png);
        }
        var page=PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Image,path,path,0,1,new(800,600),new(160,120)));
        page.RotationQuarterTurns=turns;
        return page;
    }
    private static byte[] Hash(string path)=>SHA256.HashData(File.ReadAllBytes(path));
    private static string Annotations(ContentObject content)=>JsonSerializer.Serialize(content.Edits.Annotations);
    private static void SetNumeric(CropEditorViewModel editor,string field,string value)
    {
        switch(field)
        {
            case "x": editor.XText=value; break;
            case "y": editor.YText=value; break;
            case "width": editor.WidthText=value; break;
            default: editor.HeightText=value; break;
        }
    }

    [Fact]
    public void DraftResizeRatioAndResetNeverMutateTheParentDocumentOrOriginalBytes()
    {
        using var session=Session(); var page=Photo(1);
        page.Edits.SetCrop(new(80,60,400,300));
        page.Edits.Add(Annotation.Create(AnnotationKind.Arrow,new(30,30),new(130,90),"#175E9C",4));
        session.AddPage(page); var parent=page.Document.Snapshot(); var undo=session.UndoCount; var hash=Hash(page.OriginalPath);
        using(var editor=new CropEditorViewModel(parent.PrimaryAsset!,parent.PrimaryContent!.Edits))
        {
            editor.WidthText="200"; editor.HeightText="150"; editor.XText="10"; editor.YText="20";
            editor.Ratio=1; Assert.Equal(1,editor.OrientedCrop.Width/editor.OrientedCrop.Height,8);
            Assert.Same(parent,page.Document.Snapshot()); Assert.Equal(undo,session.UndoCount);
            editor.ResetCommand.Execute(null);
            Assert.Equal(new RectD(0,0,800,600),editor.ResultCrop); Assert.Equal(0,editor.Ratio);
            Assert.Equal(Annotations(parent.PrimaryContent!),Annotations(editor.DraftPage.Document.PrimaryContent!));
        }
        Assert.Same(parent,page.Document.Snapshot()); Assert.Equal(undo,session.UndoCount); Assert.Equal(hash,Hash(page.OriginalPath));
    }

    [Theory]
    [InlineData(0,80,60,400,300,10,20,200,150)]
    [InlineData(1,240,80,300,400,20,390,150,200)]
    [InlineData(2,320,240,400,300,590,430,200,150)]
    [InlineData(3,60,320,300,400,630,10,150,200)]
    public void NumericFieldsUseVisibleImageCoordinatesWhilePersistenceUsesTheOriginalSource(int turns,
        double initialX,double initialY,double initialWidth,double initialHeight,
        double sourceX,double sourceY,double sourceWidth,double sourceHeight)
    {
        var page=Photo(turns); page.Edits.SetCrop(new(80,60,400,300)); var parent=page.Document.Snapshot();
        using var editor=new CropEditorViewModel(parent.PrimaryAsset!,parent.PrimaryContent!.Edits);
        Assert.Equal(new RectD(initialX,initialY,initialWidth,initialHeight),editor.OrientedCrop);
        Assert.Equal(turns%2==0 ? new RectD(0,0,800,600) : new(0,0,600,800),editor.ImageBounds);
        editor.WidthText="200"; editor.HeightText="150"; editor.XText="10"; editor.YText="20";
        Assert.Equal(new RectD(10,20,200,150),editor.OrientedCrop);
        Assert.Equal(new RectD(sourceX,sourceY,sourceWidth,sourceHeight),editor.ResultCrop);
        Assert.Equal("10",editor.XText); Assert.Equal("20",editor.YText); Assert.Equal("200",editor.WidthText); Assert.Equal("150",editor.HeightText);
        Assert.Same(parent,page.Document.Snapshot());
    }

    [Theory]
    [InlineData("x","-1")][InlineData("y","NaN")][InlineData("width","0")][InlineData("height","-20")]
    [InlineData("width","Infinity")][InlineData("width","not a number")][InlineData("width","1000")]
    [InlineData("x","750")][InlineData("y","590")]
    public void InvalidOrOutOfBoundsNumericEditsLeaveThePreviousCropAndRevisionUnchanged(string field,string input)
    {
        var page=Photo(); page.Edits.SetCrop(new(80,60,400,300)); var parent=page.Document.Snapshot();
        using var editor=new CropEditorViewModel(parent.PrimaryAsset!,parent.PrimaryContent!.Edits);
        var before=editor.ResultCrop; var revision=editor.DraftPage.Document.Revision;
        SetNumeric(editor,field,input);
        Assert.Equal(before,editor.ResultCrop); Assert.Equal(revision,editor.DraftPage.Document.Revision);
    }

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void LockedRatioNumericUpdatesKeepTheVisibleAnchorAndRecomputeTheOtherDimension(int turns)
    {
        var page=Photo(turns); page.Edits.SetCrop(new(80,60,400,300)); var parent=page.Document.Snapshot();
        using var editor=new CropEditorViewModel(parent.PrimaryAsset!,parent.PrimaryContent!.Edits);
        var before=editor.OrientedCrop; editor.Ratio=2;
        Assert.Equal(before.X+before.Width/2,editor.OrientedCrop.X+editor.OrientedCrop.Width/2,8);
        Assert.Equal(before.Y+before.Height/2,editor.OrientedCrop.Y+editor.OrientedCrop.Height/2,8);
        var anchor=editor.OrientedCrop; editor.WidthText="200";
        Assert.Equal(new RectD(anchor.X,anchor.Y,200,100),editor.OrientedCrop);
        editor.HeightText="75"; Assert.Equal(new RectD(anchor.X,anchor.Y,150,75),editor.OrientedCrop);
        editor.WidthText="150,5"; Assert.Equal(150.5,editor.OrientedCrop.Width,8); Assert.Equal(75.25,editor.OrientedCrop.Height,8);
        var unchanged=editor.ResultCrop; editor.WidthText=(editor.WidthMaximum+10).ToString("G",CultureInfo.InvariantCulture);
        Assert.Equal(unchanged,editor.ResultCrop); Assert.Same(parent,page.Document.Snapshot());
    }

    [Theory]
    [InlineData(0,100,50,500,300,20,30,100,150)]
    [InlineData(1,400,100,300,500,30,630,150,100)]
    [InlineData(2,400,400,500,300,880,570,100,150)]
    [InlineData(3,50,400,300,500,820,20,150,100)]
    public void NativePixelFieldsCorrectNormalizedLogicalDimensionsForEveryRotation(int turns,
        double orientedX,double orientedY,double orientedWidth,double orientedHeight,
        double sourceX,double sourceY,double sourceWidth,double sourceHeight)
    {
        var source=Photo().Document.PrimaryAsset! with { LogicalSize=new(1000,750) };
        using var editor=new CropEditorViewModel(source,new(new(100,50,500,300),turns,[]));
        Assert.Equal(new SizeD(1000,750),editor.PixelSize);
        Assert.Equal(orientedX,double.Parse(editor.XText,CultureInfo.InvariantCulture));
        Assert.Equal(orientedY,double.Parse(editor.YText,CultureInfo.InvariantCulture));
        Assert.Equal(orientedWidth,double.Parse(editor.WidthText,CultureInfo.InvariantCulture));
        Assert.Equal(orientedHeight,double.Parse(editor.HeightText,CultureInfo.InvariantCulture));
        var original=editor.ResultCrop; var revision=editor.DraftPage.Document.Revision;
        editor.SetPixelSize(new(800,600));
        Assert.Equal(new SizeD(800,600),editor.PixelSize); Assert.Equal(original,editor.ResultCrop); Assert.Equal(revision,editor.DraftPage.Document.Revision);
        Assert.Equal(orientedX*.8,double.Parse(editor.XText,CultureInfo.InvariantCulture),8);
        Assert.Equal(orientedY*.8,double.Parse(editor.YText,CultureInfo.InvariantCulture),8);
        Assert.Equal(orientedWidth*.8,double.Parse(editor.WidthText,CultureInfo.InvariantCulture),8);
        Assert.Equal(orientedHeight*.8,double.Parse(editor.HeightText,CultureInfo.InvariantCulture),8);
        editor.WidthText="80"; editor.HeightText="120"; editor.XText="16"; editor.YText="24";
        Assert.Equal(new RectD(sourceX,sourceY,sourceWidth,sourceHeight),editor.ResultCrop);
        Assert.Equal("16",editor.XText); Assert.Equal("24",editor.YText); Assert.Equal("80",editor.WidthText); Assert.Equal("120",editor.HeightText);
        editor.Ratio=2; editor.HeightText="20";
        Assert.Equal(2,editor.LogicalRatio,8); Assert.Equal(40,double.Parse(editor.WidthText,CultureInfo.InvariantCulture),8);
        Assert.Equal(20,double.Parse(editor.HeightText,CultureInfo.InvariantCulture),8);
    }

    [Fact]
    public void AOnePixelWideImageStillSupportsAWideCropRatioWithoutAnEmptyFrame()
    {
        var source=Photo().Document.PrimaryAsset! with { LogicalSize=new(1,100) };
        using var editor=new CropEditorViewModel(source,ContentEdits.Empty(source.LogicalSize));
        editor.SetPixelSize(new(1,100)); editor.Ratio=16d/9;
        Assert.Equal(16d/9,editor.Ratio,8); Assert.Equal(1,editor.OrientedCrop.Width,8);
        Assert.Equal(9d/16,editor.OrientedCrop.Height,8); Assert.Equal(0,editor.OrientedCrop.X,8);
        Assert.Equal((100d-9d/16)/2,editor.OrientedCrop.Y,8); Assert.False(editor.ResultCrop.IsEmpty);
    }

    [Fact]
    public void AHighPixelImageAllowsOnePixelNumericDimensionsSmallerThanOneLogicalUnit()
    {
        var source=Photo().Document.PrimaryAsset! with { LogicalSize=new(1000,750) };
        using var editor=new CropEditorViewModel(source,ContentEdits.Empty(source.LogicalSize));
        editor.SetPixelSize(new(8000,6000)); editor.WidthText="1"; editor.HeightText="1";
        Assert.Equal(new RectD(0,0,.125,.125),editor.ResultCrop);
        Assert.Equal("1",editor.WidthText); Assert.Equal("1",editor.HeightText);
        editor.Ratio=1; Assert.Equal(1,editor.Ratio); Assert.Equal(new RectD(0,0,.125,.125),editor.ResultCrop);
    }

    [Fact]
    public void LocalUndoRestoresTheExactPreviousFrameAndClearsAnIncompatibleRatioLock()
    {
        var source=Photo().Document.PrimaryAsset! with { LogicalSize=new(1000,750) };
        var original=new RectD(100,50,500,300);
        using var editor=new CropEditorViewModel(source,new(original,0,[])); editor.SetPixelSize(new(800,600));
        editor.Ratio=2; var locked=editor.ResultCrop;
        Assert.Equal(new RectD(100,75,500,250),locked); Assert.Equal(2,editor.Ratio);
        editor.DraftPage.Edits.Undo(); Assert.Equal(original,editor.ResultCrop); Assert.Equal(0,editor.Ratio);
        Assert.Equal("400",editor.WidthText); Assert.Equal("240",editor.HeightText);
        editor.DraftPage.Edits.Redo(); Assert.Equal(locked,editor.ResultCrop); Assert.Equal(0,editor.Ratio);
    }

    [Theory]
    [InlineData(0,PlacementMode.Automatic)][InlineData(1,PlacementMode.Automatic)]
    [InlineData(2,PlacementMode.Automatic)][InlineData(3,PlacementMode.Automatic)]
    [InlineData(0,PlacementMode.Fixed)][InlineData(1,PlacementMode.Fixed)]
    [InlineData(2,PlacementMode.Fixed)][InlineData(3,PlacementMode.Fixed)]
    public void ApplyingRepeatedCropAndResetPreservesSourceAnnotationsAndProducesOneUndoPerApply(int turns,PlacementMode mode)
    {
        using var session=Session(); var page=Photo(turns);
        page.Edits.Add(Annotation.Create(AnnotationKind.Arrow,new(20,20),new(90,70),"#175E9C",4));
        page.Edits.Add(Annotation.Create(AnnotationKind.Rectangle,new(700,500),new(760,550),"#777777",3));
        var originalHeight=turns%2==0 ? 60d : 80d*4/3;
        if(mode==PlacementMode.Fixed)
        {
            page.Document.PlacePrimaryImage(SheetPaper.A4,new(20,30,80,originalHeight));
            var content=page.Document.PrimaryContent!;
            page.Document.SetPlacement(content.Id,content.Placement with { RotationDegrees=15 });
        }
        session.AddPage(page); session.SelectedPage=page;
        var original=page.Document.Snapshot(); var originalContent=original.PrimaryContent!; var count=session.UndoCount; var sourceHash=Hash(page.OriginalPath);
        var firstCrop=new RectD(100,80,400,400); session.ApplyImageCrop(page,originalContent.Id,firstCrop);
        var first=page.Document.Snapshot(); var cropped=first.PrimaryContent!;
        Assert.Equal(firstCrop,cropped.Edits.Crop); Assert.Equal(turns,cropped.Edits.QuarterTurns); Assert.Equal(count+1,session.UndoCount);
        Assert.Equal(Annotations(originalContent),Annotations(cropped)); Assert.Equal(original.PrimaryAsset,first.PrimaryAsset);
        if(mode==PlacementMode.Fixed)
        {
            Assert.Equal(20,cropped.Placement.BoundsMm.X); Assert.Equal(30,cropped.Placement.BoundsMm.Y);
            Assert.Equal(80,cropped.Placement.BoundsMm.Width); Assert.Equal(80,cropped.Placement.BoundsMm.Height,8);
            Assert.Equal(15,cropped.Placement.RotationDegrees);
        }
        else Assert.Equal(originalContent.Placement,cropped.Placement);
        session.ApplyImageCrop(page,originalContent.Id,firstCrop); Assert.Equal(count+1,session.UndoCount);

        // A second crop expands outside the first instead of losing the previously hidden material.
        var expandedCrop=new RectD(50,40,600,500); session.ApplyImageCrop(page,originalContent.Id,expandedCrop);
        var expanded=page.Document.Snapshot(); Assert.Equal(expandedCrop,expanded.PrimaryContent!.Edits.Crop); Assert.Equal(count+2,session.UndoCount);
        session.ApplyImageCrop(page,originalContent.Id,new(0,0,800,600)); var reset=page.Document.Snapshot();
        Assert.Equal(new RectD(0,0,800,600),reset.PrimaryContent!.Edits.Crop); Assert.Equal(count+3,session.UndoCount);
        Assert.Equal(Annotations(originalContent),Annotations(reset.PrimaryContent!)); Assert.Equal(original.PrimaryAsset,reset.PrimaryAsset);
        Assert.Equal(originalContent.Placement.BoundsMm.Width,reset.PrimaryContent!.Placement.BoundsMm.Width,8);
        Assert.Equal(originalContent.Placement.BoundsMm.Height,reset.PrimaryContent.Placement.BoundsMm.Height,8);
        Assert.Equal(originalContent.Placement.RotationDegrees,reset.PrimaryContent.Placement.RotationDegrees);
        session.UndoCommand.Execute(null); Assert.Equal(expanded,page.Document.Snapshot());
        session.UndoCommand.Execute(null); Assert.Equal(first,page.Document.Snapshot());
        session.UndoCommand.Execute(null); Assert.Equal(original,page.Document.Snapshot());
        session.RedoCommand.Execute(null); session.RedoCommand.Execute(null); session.RedoCommand.Execute(null);
        Assert.Equal(reset,page.Document.Snapshot()); Assert.Equal(sourceHash,Hash(page.OriginalPath));
    }

    [Fact]
    public void CropCommandRequestsTheModalWithoutChangingToolAndRejectsReadOnlyTargets()
    {
        using var session=Session(); var page=Photo(); session.AddPage(page); session.Tool=EditorTool.Brush;
        var before=page.Document.Snapshot(); var count=session.UndoCount; var requests=0;
        session.CropRequested+=(_,_)=>requests++;
        session.SetToolCommand.Execute(EditorTool.Crop);
        Assert.Equal(1,requests); Assert.Equal(EditorTool.Brush,session.Tool); Assert.Same(before,page.Document.Snapshot()); Assert.Equal(count,session.UndoCount);
        session.IsBusy=true; session.SetToolCommand.Execute(EditorTool.Crop); session.IsBusy=false; Assert.Equal(1,requests);
        session.ShowPrintPreview=true; session.SetToolCommand.Execute(EditorTool.Crop); session.ShowPrintPreview=false; Assert.Equal(1,requests);
        page.Document.ReplaceObject(page.Document.PrimaryContent! with { Locked=true },"Lock fixture");
        session.SetToolCommand.Execute(EditorTool.Crop); Assert.Equal(1,requests);
        var blank=PrintPageItem.CreateBlank(SheetPaper.A4); session.AddPage(blank); session.SelectedPage=blank;
        session.SetToolCommand.Execute(EditorTool.Crop); Assert.Equal(1,requests);
        var path=Fixtures.CreatePdf("pm008-read-only-"+Guid.NewGuid().ToString("N")+".pdf",1);
        var document=PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Pdf,path,path,0,1,new(595,842),new(210,297)));
        session.AddPage(document); session.SelectedPage=document;
        session.SetToolCommand.Execute(EditorTool.Crop); Assert.Equal(1,requests);
    }

    [Fact]
    public async Task PortableProjectRoundTripRetainsExpandedCropRotationAndHiddenSourcePixels()
    {
        using var session=Session(); var page=Photo(3);
        page.Edits.Add(Annotation.Create(AnnotationKind.Rectangle,new(20,20),new(90,70),"#175E9C",4)); session.AddPage(page);
        var id=page.Document.PrimaryContent!.Id; var sourceHash=Hash(page.OriginalPath);
        session.ApplyImageCrop(page,id,new(100,80,400,400)); session.ApplyImageCrop(page,id,new(50,40,650,500));
        var before=page.Document.Snapshot(); var project=ProjectFileTests.PathFor("pm008-roundtrip"); var files=new ProjectFileService();
        await files.SaveAsync(ProjectFileTests.Session(page),project,true,default);
        using var workspace=new SessionWorkspace(); var loaded=await files.LoadAsync(project,workspace,default); Assert.Empty(loaded.Missing);
        var restored=Assert.Single(loaded.Session.Pages).Sheet;
        Assert.Equal(new RectD(50,40,650,500),restored.PrimaryContent!.Edits.Crop); Assert.Equal(3,restored.PrimaryContent.Edits.QuarterTurns);
        Assert.Equal(Annotations(before.PrimaryContent!),Annotations(restored.PrimaryContent)); Assert.Equal(id,restored.PrimaryContent.Id);
        Assert.Equal(sourceHash,Hash(restored.PrimaryAsset!.WorkingPath!)); Assert.Equal(sourceHash,Hash(page.OriginalPath));
        using var editor=new CropEditorViewModel(restored.PrimaryAsset!,restored.PrimaryContent.Edits); editor.ResetCommand.Execute(null);
        Assert.Equal(new RectD(0,0,800,600),editor.ResultCrop); Assert.Equal(new SizeD(600,800),editor.DraftPage.Snapshot().ContentSize);
        using var full=new PageRasterizer(new()).Render(editor.DraftPage.Snapshot(),600,800,false,default);
        AssertColor(Color.Red,full.GetPixel(300,650)); AssertColor(Color.Lime,full.GetPixel(150,150)); AssertColor(Color.Blue,full.GetPixel(450,150));
    }

    [Theory]
    [InlineData(300,0)][InlineData(300,1)][InlineData(300,2)][InlineData(300,3)]
    [InlineData(600,0)][InlineData(600,1)][InlineData(600,2)][InlineData(600,3)]
    public async Task PngAndPdfExportOnlyTheAppliedCropWithoutEditorGuidesForEveryRotation(int dpi,int turns)
    {
        using var session=Session(); var page=Photo(turns); session.AddPage(page); var beforeHash=Hash(page.OriginalPath);
        var original=page.Document.Snapshot();
        using(var editor=new CropEditorViewModel(original.PrimaryAsset!,original.PrimaryContent!.Edits))
        {
            editor.DraftPage.Edits.SetCrop(new(400,0,400,600));
            Assert.Same(original,page.Document.Snapshot());
            session.ApplyImageCrop(page,original.PrimaryContent.Id,editor.ResultCrop);
        }
        var applied=page.Document.Snapshot(); var paper=new SheetPaper(new(70,90),false,SheetMargins.SafeDefault);
        var item=SheetExportRenderer.Prepare(page.Snapshot(),paper);
        var options=new ExportOptions(ExportFormat.Png,ExportAppearance.Color,dpi,ScaleMode.Fit,MonochromeMode.Auto,160);
        var folder=Path.Combine(Fixtures.Root,"pm008-export-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var png=Path.Combine(folder,"crop.png"); var pdf=Path.Combine(folder,"crop.pdf"); var service=new PageExportService(new(new(new())));
        await service.ExportAsync(ExportFilePlan.Build([item],options,png),false,null,default);
        await service.ExportAsync(ExportFilePlan.Build([item],options with { Format=ExportFormat.Pdf },pdf),false,null,default);
        using var image=new Bitmap(png); var reader=new PdfiumRenderer();
        using var renderedPdf=reader.Render(pdf,0,image.Width,image.Height,0,false,default);
        Assert.Equal((int)Math.Ceiling(70d/25.4*dpi),image.Width); Assert.Equal((int)Math.Ceiling(90d/25.4*dpi),image.Height);
        Assert.InRange(image.HorizontalResolution,dpi-.1,dpi+.1);
        var size=Assert.Single(reader.ReadPages(pdf)); Assert.Equal(70,size.Width/72*25.4,2); Assert.Equal(90,size.Height/72*25.4,2);
        var first=turns is 0 or 3 ? Color.Lime : Color.Blue; var second=turns is 0 or 3 ? Color.Blue : Color.Lime;
        // Include thirds intersections: an accidentally exported crop grid cannot hide between samples.
        var points=turns%2==0 ? new[] {(35d,25d),(35d,65d),(26.111111d,31.666667d),(43.888889d,58.333333d)}
            : new[] {(20d,45d),(50d,45d),(25d,38.333333d),(45d,51.666667d)};
        foreach(var bitmap in new[] {image,renderedPdf})
        {
            for(var i=0;i<points.Length;i++)
            {
                var x=(int)(points[i].Item1/70*bitmap.Width); var y=(int)(points[i].Item2/90*bitmap.Height);
                for(var dy=-4;dy<=4;dy++) for(var dx=-4;dx<=4;dx++)
                    AssertColor(i%2==0 ? first : second,bitmap.GetPixel(x+dx,y+dy));
            }
            AssertColor(Color.White,bitmap.GetPixel(1,bitmap.Height/2)); AssertColor(Color.White,bitmap.GetPixel(bitmap.Width-2,bitmap.Height/2));
        }
        Assert.Same(applied,page.Document.Snapshot()); Assert.Equal(beforeHash,Hash(page.OriginalPath));
    }
    private static void AssertColor(Color expected,Color actual)
    {
        Assert.InRange((int)actual.R,Math.Max(0,expected.R-8),Math.Min(255,expected.R+8));
        Assert.InRange((int)actual.G,Math.Max(0,expected.G-8),Math.Min(255,expected.G+8));
        Assert.InRange((int)actual.B,Math.Max(0,expected.B-8),Math.Min(255,expected.B+8));
    }
}
