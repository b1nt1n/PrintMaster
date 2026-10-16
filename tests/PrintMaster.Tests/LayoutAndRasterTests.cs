using PrintMaster.Models;
using PrintMaster.Services;
using System.Drawing;
using System.Drawing.Imaging;
using Xunit;

[assembly:CollectionBehavior(DisableTestParallelization=true)]

namespace PrintMaster.Tests;

public class LayoutAndRasterTests
{
    [Theory]
    [InlineData(165,650)] [InlineData(205,807)] [InlineData(210,827)] [InlineData(297,1169)]
    public void PaperUsesHundredthsOfInch(double mm,int units)=>Assert.Equal(units,PageLayout.MmToHundredths(mm));
    [Fact] public void FitUsesAsymmetricHardwareMargins()
    {
        var safe=PageLayout.SafeArea(new(210,297),new(7,4,198,287),5);
        Assert.Equal(new RectD(7,5,198,286),safe);
        var placement=PageLayout.Place(new(100,200),safe,ScaleMode.Fit);
        Assert.Equal(143,placement.Width,4); Assert.Equal(286,placement.Height,4); Assert.True(safe.Contains(new(placement.X,placement.Y)));
    }
    [Fact] public void FillReportsCropAndKeepsCenter()
    {
        var target=new RectD(5,5,200,287); var fill=PageLayout.Place(new(4,3),target,ScaleMode.Fill);
        Assert.True(PageLayout.Crops(new(4,3),target,ScaleMode.Fill)); Assert.False(PageLayout.Crops(new(4,3),target,ScaleMode.Fit));
        Assert.Equal(target.X+target.Width/2,fill.X+fill.Width/2,4); Assert.Equal(target.Y+target.Height/2,fill.Y+fill.Height/2,4);
    }
    [Fact] public void CopiesRetainQueueOrder()
    {
        var a=Fixtures.ImagePage("a") with { Copies=2 }; var b=Fixtures.ImagePage("b") with { Copies=3 };
        Assert.Equal(new[] { a.Id,a.Id,b.Id,b.Id,b.Id },PageLayout.ExpandCopies(new[] { a,b }).Select(x=>x.Id));
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void CropRotationTransformRoundTrips(int rotation)
    {
        var crop=new RectD(100,70,400,300); var t=ImageGeometry.CreateTransform(crop,rotation,new(19,25,400,300)); var p=new PointD(170,180);
        var back=t.Inverse(t.Transform(p)); Assert.Equal(p.X,back.X,8); Assert.Equal(p.Y,back.Y,8);
        var corners=new[] { t.Transform(new(crop.X,crop.Y)),t.Transform(new(crop.Right,crop.Bottom)) };
        Assert.All(corners,c=>{ Assert.InRange(c.X,19-1e-8,419+1e-8); Assert.InRange(c.Y,25-1e-8,325+1e-8); });
    }
    [Fact] public void RatioCropStaysWithinBounds()
    {
        var crop=ImageGeometry.AspectCrop(new(900,700),new(0,0),16d/9,new(0,0,1000,750));
        Assert.Equal(16d/9,crop.Width/crop.Height,8); Assert.True(crop.X>=0 && crop.Y>=0 && crop.Right<=1000 && crop.Bottom<=750);
    }
    [Fact] public void TransparentPixelsPrintAsWhite()
    {
        using var bitmap=new Bitmap(2,1,PixelFormat.Format32bppArgb); bitmap.SetPixel(0,0,Color.FromArgb(0,0,0,0)); bitmap.SetPixel(1,0,Color.Black);
        MonochromeRasterizer.Apply(bitmap,MonochromeMode.Text,160,default);
        Assert.Equal(255,bitmap.GetPixel(0,0).R); Assert.Equal(0,bitmap.GetPixel(1,0).R);
    }
    [Fact] public void DitheringPreservesMidtoneDensityAndProducesBinaryPixels()
    {
        using var bitmap=new Bitmap(200,120,PixelFormat.Format32bppArgb); using(var g=Graphics.FromImage(bitmap)) g.Clear(Color.FromArgb(128,128,128));
        MonochromeRasterizer.Apply(bitmap,MonochromeMode.Photo,160,default); var white=0;
        for(var y=0;y<bitmap.Height;y++) for(var x=0;x<bitmap.Width;x++)
        { var pixel=bitmap.GetPixel(x,y); Assert.True(pixel.R is 0 or 255); Assert.Equal(pixel.R,pixel.G); Assert.Equal(pixel.G,pixel.B); if(pixel.R==255) white++; }
        Assert.InRange(white/24000.0,0.48,0.52);
    }
    [Fact] public void CropRotationAndAnnotationsAreBakedIntoRaster()
    {
        var path=Fixtures.CreateImage(); var page=Fixtures.ImagePage(path) with { RotationQuarterTurns=1,
            Edits=new(new(100,100,800,500),new[] { Annotation.Create(AnnotationKind.Rectangle,new(200,200),new(400,400),"#000000",10) }) };
        using var bitmap=new PageRasterizer(new()).Render(page,500,800,true,default);
        Assert.Equal(500,bitmap.Width); Assert.Equal(800,bitmap.Height);
        bitmap.Save(System.IO.Path.Combine(Fixtures.Root,"edited-crop-rotate.png"),ImageFormat.Png);
        // Original (200,200) maps to (400,100) after clockwise crop rotation.
        Assert.True(bitmap.GetPixel(400,100).R<20);
    }
    [Fact] public void FillRasterAllocatesOnlyViewport()
    {
        var page=Fixtures.ImagePage(Fixtures.CreateImage()); var destination=PageLayout.Place(page.ContentSize,new(0,0,300,450),ScaleMode.Fill);
        using var bitmap=new PageRasterizer(new()).RenderViewport(page,300,450,destination,true,default);
        Assert.Equal(300,bitmap.Width); Assert.Equal(450,bitmap.Height); Assert.NotEqual(Color.White.ToArgb(),bitmap.GetPixel(150,220).ToArgb());
    }
    [Fact] public void UndoRestoresCropAndVectorGeometry()
    {
        var state=new ImageEditState(1000,750); state.SetCrop(new(10,20,800,600)); state.Add(Annotation.Create(AnnotationKind.Arrow,new(1,1),new(50,70),"#000000",3));
        state.Undo(); Assert.Empty(state.Annotations); state.Undo(); Assert.Equal(new RectD(0,0,1000,750),state.Crop);
        state.Redo(); Assert.Equal(new RectD(10,20,800,600),state.Crop);
    }
    [Fact] public void PdfiumRendersActualMultiPagePdf()
    {
        var path=Fixtures.CreatePdf(); var pdf=new PdfiumRenderer(); Assert.Equal(2,pdf.ReadPages(path).Count);
        using var bitmap=pdf.Render(path,1,595,842,0,false,default); bitmap.Save(System.IO.Path.Combine(Fixtures.Root,"pdf-preview.png"),ImageFormat.Png);
        var black=0; for(var y=50;y<200;y++) for(var x=20;x<550;x++) if(bitmap.GetPixel(x,y).R<50) black++;
        Assert.True(black>200,"The PDF text must actually be rendered.");
    }
    [Fact] public void CancellationStopsRasterization()
    {
        using var cancellation=new CancellationTokenSource(); cancellation.Cancel(); using var bitmap=new Bitmap(40,40,PixelFormat.Format32bppArgb);
        Assert.Throws<OperationCanceledException>(()=>MonochromeRasterizer.Apply(bitmap,MonochromeMode.Photo,160,cancellation.Token));
    }
}
