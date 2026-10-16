using PrintMaster.Models;
using PrintMaster.Services;
using System.Collections.Immutable;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Xunit;
namespace PrintMaster.Tests;
public sealed class MarkdownReportedRegressionTests
{
    private static MarkdownDocument Document(string source)=>new(Guid.NewGuid(),source,MarkdownStyle.Default,[],"Reported regression");
    private static async Task<(MarkdownLayoutResult Result,Bitmap Bitmap)> Render(string source)
    {
        using var service=new MarkdownLayoutService();var result=await service.LayoutAsync(Document(source),SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,default);
        var bitmap=await Task.Run(()=>MarkdownDrawingRenderer.Render(result.Pages[0],120,default,1000));return(result,bitmap);
    }
    private static byte[] Pixels(Bitmap bitmap)
    {
        var data=bitmap.LockBits(new(0,0,bitmap.Width,bitmap.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        try{var bytes=new byte[data.Stride*data.Height];Marshal.Copy(data.Scan0,bytes,0,bytes.Length);return bytes;}finally{bitmap.UnlockBits(data);}
    }
    private static Rectangle Ink(Bitmap bitmap,int left=0,int?right=null)
    {
        var pixels=Pixels(bitmap);var x0=bitmap.Width;var y0=bitmap.Height;var x1=-1;var y1=-1;
        for(var y=0;y<bitmap.Height;y++)for(var x=left;x<(right??bitmap.Width);x++)if(pixels[(y*bitmap.Width+x)*4]<80)
        {x0=Math.Min(x0,x);y0=Math.Min(y0,y);x1=Math.Max(x1,x);y1=Math.Max(y1,y);}
        Assert.True(x1>=x0,"No visible ink in expected region");return Rectangle.FromLTRB(x0,y0,x1+1,y1+1);
    }
    private static int LongestDarkRun(Bitmap bitmap)
    {
        var pixels=Pixels(bitmap);var longest=0;
        for(var y=0;y<bitmap.Height;y++){var run=0;for(var x=0;x<bitmap.Width;x++){run=pixels[(y*bitmap.Width+x)*4]<80 ? run+1 : 0;longest=Math.Max(longest,run);}}return longest;
    }
    [Theory][InlineData(",")][InlineData(".")][InlineData("!")][InlineData(":")]
    public async Task StrikethroughBeforePunctuationRemovesTildesAndDrawsDecoration(string punctuation)
    {
        var struck=await Render("~~зачёркнутый текст~~"+punctuation);using var a=struck.Bitmap;
        var plain=await Render("зачёркнутый текст"+punctuation);using var b=plain.Bitmap;
        Assert.DoesNotContain("~",struck.Result.Pages[0].DiagnosticText);Assert.Contains("зачёркнутый текст",struck.Result.Pages[0].DiagnosticText);
        // A subpixel preview line is antialiased. Verify the actual default text
        // threshold used for printing, rather than mistaking gray edge pixels for no line.
        MonochromeRasterizer.Apply(a,MonochromeMode.Text,160,default);MonochromeRasterizer.Apply(b,MonochromeMode.Text,160,default);
        Assert.True(LongestDarkRun(a)>LongestDarkRun(b)+20,$"Strikethrough must print: struck={LongestDarkRun(a)}, plain={LongestDarkRun(b)}");
    }
    [Fact]public async Task TableColumnsHonorLeftCenterAndRightAlignment()
    {
        var rendered=await Render("| A | B | C |\n| :--- | :---: | ---: |\n| Z | Z | Z |");using var bitmap=rendered.Bitmap;
        var start=10d/210*bitmap.Width;var column=190d/210*bitmap.Width/3;
        var left=Ink(bitmap,(int)start,(int)(start+column));var center=Ink(bitmap,(int)(start+column),(int)(start+2*column));var right=Ink(bitmap,(int)(start+2*column),(int)(start+3*column));
        Assert.InRange((left.Left-start)/column,0,.15);Assert.InRange((center.Left-start-column)/column,.35,.65);Assert.InRange((right.Left-start-2*column)/column,.75,1);
    }
    [Fact]public async Task TaskListsPrintSquareCheckmarksWithoutLiteralBracketsOrRoundBullets()
    {
        var uncheckedNote=await Render("- [ ] ENTRY");using var a=uncheckedNote.Bitmap;
        var checkedNote=await Render("- [x] ENTRY");using var b=checkedNote.Bitmap;
        Assert.DoesNotContain("[",uncheckedNote.Result.Pages[0].DiagnosticText);Assert.DoesNotContain("[x]",checkedNote.Result.Pages[0].DiagnosticText);
        Assert.DoesNotContain("•",checkedNote.Result.Pages[0].DiagnosticText);Assert.Contains("ENTRY",checkedNote.Result.Pages[0].DiagnosticText);
        var pa=Pixels(a);var pb=Pixels(b);Assert.True(pa.Where((v,i)=>i%4==0 && v<80).Count()<pb.Where((v,i)=>i%4==0 && v<80).Count(),"Checked square must have an extra printed tick");
    }
    [Theory][InlineData("a\tb","a   b")][InlineData("abcd\tb","abcd    b")][InlineData("\t\tx","        x")][InlineData("я\tQ\r\n\tZ","я   Q\n    Z")]
    public void CodeTabsAdvanceToFourColumnStopsAndResetPerLine(string source,string expected)=>Assert.Equal(expected,MarkdownLayoutService.ExpandCodeTabs(source));
    [Fact]public async Task TabbedCodeAndEquivalentSpacesHaveIdenticalPrintedPositions()
    {
        var tabs=await Render("```text\na\tQ\n\tQ\n\t\tQ\n```");using var a=tabs.Bitmap;
        var spaces=await Render("```text\na   Q\n    Q\n        Q\n```");using var b=spaces.Bitmap;
        Assert.Equal(a.Size,b.Size);Assert.Equal(Pixels(a),Pixels(b));
    }
    [Theory][InlineData("$$\\frac{a}{b}$$")][InlineData("$$\n\\frac{a}{b}\n$$")]
    public async Task DisplayFormulaIsCenteredWithinPrintableTextWidth(string source)
    {
        var rendered=await Render(source);using var bitmap=rendered.Bitmap;var ink=Ink(bitmap);
        Assert.InRange(Math.Abs(ink.Left+ink.Width/2d-bitmap.Width/2d),0,3);
    }
    [Fact]public async Task ShortExplanationAndCodeStayOnSamePageAtBoundary()
    {
        using var service=new MarkdownLayoutService();
        for(var count=1;count<=18;count++)
        {
            var source=string.Join("\n\n",Enumerable.Repeat("Body filler text.",count))+"\n\nEXPLANATION_BEFORE_CODE:\n\n```text\nCODE_ONE\nCODE_TWO\nCODE_THREE\nCODE_FOUR\n```";
            var result=await service.LayoutAsync(Document(source),new(new(90,110),false,SheetMargins.SafeDefault),ImmutableArray<SourceAsset>.Empty,default);
            Assert.Single(result.Pages.Where(p=>p.DiagnosticText.Contains("EXPLANATION_BEFORE_CODE",StringComparison.Ordinal)));
            var index=Array.FindIndex(result.Pages.ToArray(),p=>p.DiagnosticText.Contains("EXPLANATION_BEFORE_CODE",StringComparison.Ordinal));
            Assert.Contains("CODE_ONE",result.Pages[index].DiagnosticText);Assert.Contains("CODE_FOUR",result.Pages[index].DiagnosticText);
        }
    }
    [Fact]public async Task HeadingKeepsTheFirstFollowingTextLinesOnSamePage()
    {
        using var service=new MarkdownLayoutService();
        for(var count=1;count<=16;count++)
        {
            var source=string.Join("\n\n",Enumerable.Repeat("Filler text.",count))+"\n\n## BOUNDARY_HEADING\n\nFOLLOWING_PARAGRAPH text starts here.";
            var result=await service.LayoutAsync(Document(source),new(new(90,110),false,SheetMargins.SafeDefault),ImmutableArray<SourceAsset>.Empty,default);
            var page=Assert.Single(result.Pages.Where(p=>p.DiagnosticText.Contains("BOUNDARY_HEADING",StringComparison.Ordinal)));Assert.Contains("FOLLOWING_PARAGRAPH",page.DiagnosticText);
        }
    }
}
