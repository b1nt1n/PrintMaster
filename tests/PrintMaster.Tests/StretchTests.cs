using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Xunit;

namespace PrintMaster.Tests;

public sealed class StretchTests
{
    internal static PrintPageItem Scene()
    {
        var path=Path.Combine(Fixtures.Root,"pm007-source-"+Guid.NewGuid().ToString("N")+".png");
        using(var bitmap=new Bitmap(400,200))
        {
            using var g=Graphics.FromImage(bitmap); g.Clear(Color.White);
            g.FillRectangle(Brushes.Red,0,0,200,100); g.FillRectangle(Brushes.Lime,200,0,200,100);
            g.FillRectangle(Brushes.Blue,0,100,200,100); g.FillRectangle(Brushes.Gold,200,100,200,100);
            bitmap.Save(path,ImageFormat.Png);
        }
        return PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Image,path,path,0,1,new(400,200),new(100,50)));
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void Placement_AllRotations_MapFullCropToAsymmetricHardwareArea(int rotation)
    {
        var crop=new RectD(80,40,320,140); var safe=PageLayout.SafeArea(new(210,297),new(7,4,196,287),5);
        var destination=PageLayout.Place(new SizeD(crop.Width,crop.Height).Rotated(rotation),safe,ScaleMode.Stretch);
        var transform=ImageGeometry.CreateTransform(crop,rotation,destination);
        var corners=new[] {new PointD(crop.X,crop.Y),new PointD(crop.Right,crop.Y),new PointD(crop.X,crop.Bottom),new PointD(crop.Right,crop.Bottom)};
        var mapped=corners.Select(transform.Transform).ToArray();
        Assert.Equal(safe.X,mapped.Min(p=>p.X),7); Assert.Equal(safe.Right,mapped.Max(p=>p.X),7);
        Assert.Equal(safe.Y,mapped.Min(p=>p.Y),7); Assert.Equal(safe.Bottom,mapped.Max(p=>p.Y),7);
        Assert.False(PageLayout.Crops(new SizeD(crop.Width,crop.Height).Rotated(rotation),safe,ScaleMode.Stretch));
        Assert.True(PageLayout.Crops(new SizeD(crop.Width,crop.Height).Rotated(rotation),safe,ScaleMode.Fill));
        var point=new PointD(175,90); var restored=transform.Inverse(transform.Transform(point));
        Assert.Equal(point.X,restored.X,7); Assert.Equal(point.Y,restored.Y,7);
    }
    [Theory] [InlineData(300,0)] [InlineData(300,1)] [InlineData(300,2)] [InlineData(300,3)]
    [InlineData(600,0)] [InlineData(600,1)] [InlineData(600,2)] [InlineData(600,3)]
    public void Export_AllImageQuadrantsRemainVisibleAndPaperMarginsStayWhite(int dpi,int rotation)
    {
        var page=Scene(); page.RotationQuarterTurns=rotation; var before=File.ReadAllBytes(page.OriginalPath);
        var item=SheetExportRenderer.Prepare(page.Snapshot(),new(new(148,210),false,SheetMargins.SafeDefault));
        var options=new ExportOptions(ExportFormat.Png,ExportAppearance.Color,dpi,ScaleMode.Stretch,MonochromeMode.Auto,160);
        SheetExportRenderer.ValidatePages([item],options); Assert.False(SheetExportRenderer.HasClipping(item,options));
        using var bitmap=new SheetExportRenderer(new(new())).Render(item,options,default);
        var baseColors=new[] {Color.Red,Color.Lime,Color.Blue,Color.Gold};
        var order=rotation switch {0=>new[]{0,1,2,3},1=>new[]{2,0,3,1},2=>new[]{3,2,1,0},_=>new[]{1,3,0,2}};
        var safe=item.SafeAreaMm; var size=item.Paper.OrientedSizeMm;
        var points=new[] {(0.25,0.25),(0.75,0.25),(0.25,0.75),(0.75,0.75)};
        for(var i=0;i<4;i++)
        {
            var actual=bitmap.GetPixel((int)((safe.X+safe.Width*points[i].Item1)/size.Width*bitmap.Width),
                (int)((safe.Y+safe.Height*points[i].Item2)/size.Height*bitmap.Height));
            Assert.Equal(baseColors[order[i]].ToArgb(),actual.ToArgb());
        }
        Assert.Equal(Color.White.ToArgb(),bitmap.GetPixel(1,bitmap.Height/2).ToArgb());
        Assert.Equal(Color.White.ToArgb(),bitmap.GetPixel(bitmap.Width-2,bitmap.Height/2).ToArgb());
        Assert.Equal(dpi,bitmap.HorizontalResolution,1); Assert.Equal(before,File.ReadAllBytes(page.OriginalPath));
        if(rotation==0 && dpi==300) bitmap.Save(Path.Combine(Fixtures.Root,"pm007-stretch-a5-300.png"),ImageFormat.Png);
    }
    [Fact]
    public void PrintPreview_UsesSameStretchAndBinaryPipeline()
    {
        var page=Scene().Snapshot(); var settings=new PrintSettings { PrinterName="Microsoft Print to PDF",Scaling=ScaleMode.Stretch,Monochrome=MonochromeMode.Text,Threshold=128 }.Snapshot();
        var safe=new RectD(7,5,196,287); var renderer=new PageRasterizer(new());
        var preview=BitmapBridge.ToGdi(renderer.RenderPrintPreview(page,settings,safe,default));
        using(preview)
        {
            var destination=PageLayout.Place(page.ContentSize,new(0,0,preview.Width,preview.Height),ScaleMode.Stretch);
            using var direct=renderer.RenderProcessed(page,preview.Width,preview.Height,destination,MonochromeMode.Text,128,120,default,safe);
            for(var y=5;y<preview.Height;y+=53) for(var x=5;x<preview.Width;x+=47)
            { Assert.Equal(direct.GetPixel(x,y),preview.GetPixel(x,y)); Assert.Contains(preview.GetPixel(x,y).R,new byte[]{0,255}); }
        }
    }
    [Fact]
    public async Task Project_StretchRoundTripsAndKeepsOldEnumValues()
    {
        Assert.Equal(0,(int)ScaleMode.Fit); Assert.Equal(1,(int)ScaleMode.Fill); Assert.Equal(2,(int)ScaleMode.Stretch);
        var page=Scene(); var before=File.ReadAllBytes(page.OriginalPath);
        var session=new ProjectSession(ProjectPrintSettings.Capture(new() { Scaling=ScaleMode.Stretch }),
            new(page.Id,50,true,false,"#111827",4,36,0,300),[new(page.Document.Snapshot(),true,1)]);
        var path=ProjectFileTests.PathFor("pm007-stretch"); var files=new ProjectFileService(); await files.SaveAsync(session,path,true,default);
        using var workspace=new SessionWorkspace(); var restored=await files.LoadAsync(path,workspace,default);
        Assert.Equal(ScaleMode.Stretch,restored.Session.Print.Scaling); Assert.Equal(page.Id,Assert.Single(restored.Session.Pages).Sheet.Id);
        Assert.Equal(before,File.ReadAllBytes(page.OriginalPath));
    }
    [Fact]
    public void Review_StretchExplainsDistortionWithoutRequiringCroppingConfirmation()
    {
        var page=Scene(); var settings=new PrintSettings { Scaling=ScaleMode.Stretch,PrinterName="Microsoft Print to PDF" };
        using var review=new PrintReviewViewModel([page.Snapshot()],settings,[settings.PrinterName]);
        Assert.Single(review.ScaleChoices,c=>c.Value==ScaleMode.Stretch); Assert.Contains("пропорции",review.ScalingHint);
        Assert.False(review.AllowCropping); Assert.True(review.TryAccept(false)); Assert.Equal(ScaleMode.Stretch,review.AcceptedSettings!.Scaling);
    }
    [Fact]
    public void ComposedSheet_StretchDoesNotRepositionOrDistortFixedObjects()
    {
        var page=Scene(); page.Document.PlacePrimaryImage(SheetPaper.A4,new(20,30,100,50)); var before=page.Document.Snapshot();
        var item=SheetExportRenderer.Prepare(page.Snapshot(),SheetPaper.A4); var renderer=new SheetExportRenderer(new(new()));
        var fit=new ExportOptions(ExportFormat.Png,ExportAppearance.Color,300,ScaleMode.Fit,MonochromeMode.Auto,160);
        using var a=renderer.Render(item,fit,default,600); using var b=renderer.Render(item,fit with {Scaling=ScaleMode.Stretch},default,600);
        for(var y=1;y<a.Height;y+=19) for(var x=1;x<a.Width;x+=17) Assert.Equal(a.GetPixel(x,y),b.GetPixel(x,y));
        Assert.Same(before,page.Document.Snapshot());
    }
    [Fact]
    public async Task PdfExport_StretchPreservesWholeImageAndPhysicalPageSize()
    {
        var page=Scene(); var renderer=new SheetExportRenderer(new(new()));
        var item=SheetExportRenderer.Prepare(page.Snapshot(),new(new(148,210),false,SheetMargins.SafeDefault));
        var options=new ExportOptions(ExportFormat.Pdf,ExportAppearance.Color,300,ScaleMode.Stretch,MonochromeMode.Auto,160);
        var path=Path.Combine(Fixtures.Root,"pm007-export-"+Guid.NewGuid().ToString("N")+".pdf");
        var plan=ExportFilePlan.Build([item],options,path);
        await new PageExportService(renderer).ExportAsync(plan,false,null,default);
        var pdf=new PdfiumRenderer(); var size=Assert.Single(pdf.ReadPages(path));
        Assert.Equal(148,size.Width/72*25.4,2); Assert.Equal(210,size.Height/72*25.4,2);
        using var bitmap=pdf.Render(path,0,592,840,0,false,default);
        Assert.True(bitmap.GetPixel(148,210).R>240 && bitmap.GetPixel(148,210).G<15);
        Assert.True(bitmap.GetPixel(444,210).G>240 && bitmap.GetPixel(444,210).R<15);
        Assert.True(bitmap.GetPixel(148,630).B>240 && bitmap.GetPixel(148,630).R<15);
        Assert.True(bitmap.GetPixel(444,630).R>240 && bitmap.GetPixel(444,630).G>190);
        Assert.Equal(Color.White.ToArgb(),bitmap.GetPixel(1,420).ToArgb());
    }
}
