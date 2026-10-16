using PrintMaster.Models;
using PrintMaster.Services;
using System.Drawing;
using System.Drawing.Imaging;
using Xunit;

namespace PrintMaster.Tests;

public sealed class PrintPreviewTests
{
    [Fact] public void NewSessionDefaultsToAutomaticMode()
        => Assert.Equal("Auto",new PrintSettings().Monochrome.ToString());

    [Fact] public void SharpThresholdLosesBothLightAndDarkToneDifferences()
    {
        using var bitmap=new Bitmap(4,1,PixelFormat.Format32bppArgb);
        var shades=new[] { 190,230,60,140 };
        for(var i=0;i<4;i++) bitmap.SetPixel(i,0,Color.FromArgb(shades[i],shades[i],shades[i]));
        MonochromeRasterizer.Apply(bitmap,MonochromeMode.Text,160,default);
        Assert.Equal(new[] { 255,255,0,0 },Enumerable.Range(0,4).Select(i=>(int)bitmap.GetPixel(i,0).R));
    }

    [Theory] [InlineData(SourceKind.Image,MonochromeMode.Photo)] [InlineData(SourceKind.Pdf,MonochromeMode.Text)] [InlineData(SourceKind.Docx,MonochromeMode.Text)]
    public void AutomaticModeIsResolvedPerPage(SourceKind source,MonochromeMode expected)
        => Assert.Equal(expected,MonochromeRasterizer.Resolve(MonochromeMode.Auto,source));

    [Fact] public void ExplicitModeAlwaysWins()
    {
        Assert.Equal(MonochromeMode.Text,MonochromeRasterizer.Resolve(MonochromeMode.Text,SourceKind.Image));
        Assert.Equal(MonochromeMode.Photo,MonochromeRasterizer.Resolve(MonochromeMode.Photo,SourceKind.Pdf));
    }

    [Fact] public void PrintPreviewUsesTheSameRasterAndMonochromePipeline()
    {
        var path=Fixtures.CreateImage("print-preview-source.png"); var page=Fixtures.ImagePage(path);
        var config=new PrintSettings { PrinterName="Preview",Monochrome=MonochromeMode.Auto }.Snapshot();
        var safe=new RectD(5,5,200,287); var service=new PageRasterizer(new());
        var preview=service.RenderPrintPreview(page,config,safe,default);
        var width=(int)Math.Ceiling(safe.Width/25.4*120); var height=(int)Math.Ceiling(safe.Height/25.4*120);
        using var actual=service.RenderProcessed(page,width,height,PageLayout.Place(page.ContentSize,new(0,0,width,height),config.Scaling),config.Monochrome,config.Threshold,120,default);
        var expected=BitmapBridge.ToWpf(actual); var stride=width*4;
        var a=new byte[stride*height]; var b=new byte[stride*height]; preview.CopyPixels(a,stride,0); expected.CopyPixels(b,stride,0);
        Assert.Equal(a,b); Assert.InRange(preview.PixelWidth,1,1600); Assert.InRange(preview.PixelHeight,1,1600);
    }

    [Fact] public void AutomaticModeRetainsToneDifferencesInImageRaster()
    {
        var path=Fixtures.CreateImage("auto-tones-source.png"); var page=Fixtures.ImagePage(path); var rasterizer=new PageRasterizer(new());
        using var image=rasterizer.RenderProcessed(page,800,600,new(0,0,800,600),MonochromeMode.Auto,160,120,default);
        double Whites(int x)
        {
            var white=0; for(var y=200;y<300;y++) for(var i=x;i<x+40;i++) if(image.GetPixel(i,y).R==255) white++;
            return white/4000.0;
        }
        var dark=Whites(280); var light=Whites(650);
        Assert.InRange(dark,0.25,0.45); Assert.InRange(light,0.70,0.95); Assert.True(light-dark>0.3);
    }
}
