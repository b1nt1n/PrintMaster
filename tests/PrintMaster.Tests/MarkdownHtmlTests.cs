using PrintMaster.Models;
using PrintMaster.Services;
using System.Collections.Immutable;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Xunit;

namespace PrintMaster.Tests;

public sealed class MarkdownHtmlTests
{
    private static MarkdownDocument Document(string source)=>new(Guid.NewGuid(),source,MarkdownStyle.Default,[],"Safe print HTML");
    private static string Visible(MarkdownLayoutResult result)=>string.Concat(result.Pages.Select(p=>p.DiagnosticText)).Replace("\u200b","");
    private static async Task<MarkdownLayoutResult> Layout(string source)
    {using var service=new MarkdownLayoutService();return await service.LayoutAsync(Document(source),SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,default);}
    private static async Task<Bitmap> Raster(MarkdownLayoutResult result)=>await Task.Run(()=>MarkdownDrawingRenderer.Render(Assert.Single(result.Pages),120,default,1200));
    private static byte[] Pixels(Bitmap bitmap)
    {
        var data=bitmap.LockBits(new(0,0,bitmap.Width,bitmap.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        try{var bytes=new byte[data.Stride*data.Height];Marshal.Copy(data.Scan0,bytes,0,bytes.Length);return bytes;}finally{bitmap.UnlockBits(data);}
    }

    [Fact]
    public async Task SubscriptAndSuperscriptPrintSmallerGlyphsOnOppositeSidesOfBaseline()
    {
        using var service=new MarkdownLayoutService();var document=Document("H <sub>H</sub> H <sup>H</sup>") with {Style=MarkdownStyle.Default with {Body=MarkdownStyle.Default.Body with {FontSizePt=24}}};
        var result=await service.LayoutAsync(document,SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,default);
        Assert.DoesNotContain(result.Diagnostics,d=>d.Code=="html-literal");Assert.DoesNotContain("<",Visible(result));
        using var bitmap=await Raster(result);var pixels=Pixels(bitmap);var groups=new List<Rectangle>();var start=-1;var top=bitmap.Height;var bottom=-1;
        for(var x=0;x<bitmap.Width;x++)
        {
            var dark=false;for(var y=0;y<bitmap.Height;y++)if(pixels[(y*bitmap.Width+x)*4]<90){dark=true;top=Math.Min(top,y);bottom=Math.Max(bottom,y);}
            if(dark && start<0)start=x;
            if(!dark && start>=0){groups.Add(Rectangle.FromLTRB(start,top,x,bottom+1));start=-1;top=bitmap.Height;bottom=-1;}
        }
        Assert.Equal(4,groups.Count);var normal=groups[0];var sub=groups[1];var normalAgain=groups[2];var sup=groups[3];
        Assert.Equal(normal.Height,normalAgain.Height);Assert.True(sub.Height<normal.Height);Assert.True(sup.Height<normal.Height);
        Assert.True(sub.Bottom>normal.Bottom,$"Subscript {sub} must extend below {normal}");Assert.True(sup.Top<normal.Top,$"Superscript {sup} must extend above {normal}");
    }

    [Fact]
    public async Task KeyboardTextUsesSamePrintableMonospaceAsInlineCode()
    {
        var key=await Layout("<kbd>Ctrl</kbd> + <kbd>Enter</kbd>");var code=await Layout("`Ctrl` + `Enter`");
        Assert.Equal("Ctrl + Enter",Visible(key));Assert.Empty(key.Diagnostics);
        using var a=await Raster(key);using var b=await Raster(code);Assert.Equal(Pixels(a),Pixels(b));
    }

    [Theory]
    [InlineData("<details>\n\nBODY\n\n</details>")]
    [InlineData("<DETAILS>\n\n<details>\n\nBODY\n\n</details>\n\n</DETAILS>")]
    public async Task DetailsAreExpandedAndScaffoldingDoesNotAddBlankParagraphs(string source)
    {
        var expanded=await Layout(source);var plain=await Layout("BODY");Assert.Equal("BODY",Visible(expanded));Assert.Empty(expanded.Diagnostics);
        using var a=await Raster(expanded);using var b=await Raster(plain);Assert.Equal(Pixels(a),Pixels(b));
    }

    [Fact]
    public async Task DetailsKeepSummaryAndParseNormalMarkdownMathAndFootnotesInside()
    {
        const string source="<details>\n<summary>SUMMARY_TITLE</summary>\n\n**BOLD_BODY** and *italic*[^n].\n\n- [x] TASK_BODY\n\n$$\\frac{a}{b}$$\n\n[^n]: FOOTNOTE_BODY\n\n</details>";
        var result=await Layout(source);var visible=Visible(result);
        foreach(var word in new[]{"SUMMARY_TITLE","BOLD_BODY","TASK_BODY","FOOTNOTE_BODY"})Assert.Contains(word,visible);
        Assert.DoesNotContain("<",visible);Assert.DoesNotContain("[^",visible);Assert.DoesNotContain("\\frac",visible);Assert.Empty(result.Diagnostics);
        Assert.Equal(1,visible.Split("FOOTNOTE_BODY",StringSplitOptions.None).Length-1);
        using var bitmap=await Raster(result);Assert.True(Pixels(bitmap).Where((v,i)=>i%4==0&&v<90).Count()>500);
    }

    [Fact]
    public async Task DetailsImageUsesNormalOwnedAssetPipelineAndOriginalFile()
    {
        var directory=Path.Combine(Path.GetTempPath(),"PrintMaster-HtmlImage-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);var file=Path.Combine(directory,"sample.png");
        try
        {
            using(var image=new Bitmap(120,80)){using var graphics=Graphics.FromImage(image);graphics.Clear(Color.Red);image.Save(file,ImageFormat.Png);}
            var asset=new SourceAsset(Guid.NewGuid(),SourceKind.Image,file,file,0,1,new(120,80),new(30,20));
            const string source="<details>\n<summary>Illustration</summary>\n\n![sample](sample.png)\n\n</details>";
            Assert.Collection(MarkdownLayoutService.FindImageUrls(source),url=>Assert.Equal("sample.png",url));
            var document=Document(source) with {Images=[new("sample.png",asset.Id)]};using var service=new MarkdownLayoutService();
            var result=await service.LayoutAsync(document,SheetPaper.A4,[asset],default);Assert.Empty(result.Diagnostics);
            Assert.Equal(Path.GetFullPath(file),Assert.Single(Assert.Single(result.Pages).ImageFiles).Value);
            using var bitmap=await Raster(result);var pixels=Pixels(bitmap);var red=0;for(var i=0;i<pixels.Length;i+=4)if(pixels[i]<30&&pixels[i+1]<30&&pixels[i+2]>240)red++;
            Assert.True(red>100);
            using var exclusive=File.Open(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None);Assert.True(exclusive.Length>0);
        }
        finally{if(File.Exists(file))File.Delete(file);if(Directory.Exists(directory))Directory.Delete(directory);}
    }

    [Theory]
    [InlineData("`<sub>2</sub>`","<sub>2</sub>")]
    [InlineData("```html\n<details><summary>CODE</summary><sub>2</sub></details>\n```","<details><summary>CODE</summary><sub>2</sub></details>")]
    [InlineData("    <sup>CODE</sup>","<sup>CODE</sup>")]
    [InlineData("\\<sub>2\\</sub>","<sub>2</sub>")]
    [InlineData("&lt;sup&gt;2&lt;/sup&gt;","<sup>2</sup>")]
    public async Task EscapedEncodedAndCodeHtmlStayLiteralWithoutWarnings(string source,string expected)
    {
        var result=await Layout(source);Assert.Equal(expected,Visible(result));Assert.DoesNotContain(result.Diagnostics,d=>d.Code=="html-literal");
    }

    [Theory]
    [InlineData("<script>alert('x')</script>")]
    [InlineData("<img src='http://127.0.0.1:65530/private'>")]
    [InlineData("<sub onclick='alert(1)'>2</sub>")]
    [InlineData("<details style='display:none'>BODY</details>")]
    [InlineData("<b>BOLD</b>")]
    [InlineData("<sub>UNMATCHED")]
    [InlineData("<sup>MISMATCH</sub>")]
    [InlineData("<summary>OUTSIDE_DETAILS</summary>")]
    public async Task UnsupportedAttributedOrUnpairedHtmlIsVisibleAndDiagnosed(string source)
    {
        var result=await Layout("INTRO\n\n"+source);Assert.Contains(source,Visible(result));
        Assert.Contains(result.Diagnostics,d=>d.Code=="html-literal" && d.Line==3 && d.Message.Contains("обычным текстом",StringComparison.Ordinal));
    }

    [Fact]
    public async Task InlineHtmlCannotSilentlyConsumeTextAcrossParagraphBoundary()
    {
        var result=await Layout("<sub>FIRST\n\nSECOND</sub>");var visible=Visible(result);
        Assert.Contains("<sub>FIRST",visible);Assert.Contains("SECOND</sub>",visible);Assert.Equal(2,result.Diagnostics.Count(d=>d.Code=="html-literal"));
    }

    [Fact]
    public async Task FormulaErrorKeepsOriginalSourceLineAfterDetailsMarkup()
    {
        const string source="<details>\n<summary>Summary</summary>\n\nText before formula.\n\n$$\\unknownPrintMasterCommand{x}$$\n\n</details>";
        using var service=new MarkdownLayoutService();var error=await Assert.ThrowsAsync<MarkdownLayoutException>(()=>service.LayoutAsync(Document(source),SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,default));
        Assert.Equal(6,error.Line);Assert.Contains("формул",error.Message,StringComparison.OrdinalIgnoreCase);
    }
}
