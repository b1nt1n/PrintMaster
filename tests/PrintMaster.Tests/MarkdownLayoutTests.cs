using PrintMaster.Models;
using PrintMaster.Services;
using System.Collections.Immutable;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using Xunit;
using GdiColor=System.Drawing.Color;

namespace PrintMaster.Tests;

public sealed class MarkdownLayoutTests
{
    private static MarkdownDocument Document(string source,MarkdownStyle? style=null,ImmutableArray<MarkdownImageReference>? images=null)
        =>new(Guid.NewGuid(),source,style ?? MarkdownStyle.Default,images ?? [],"Engine test");
    private static string Visible(MarkdownLayoutResult result)=>string.Concat(result.Pages.Select(p=>p.DiagnosticText)).Replace("\u200b","");
    private static int Count(string text,string marker)=>text.Split(marker,StringSplitOptions.None).Length-1;
    private static IEnumerable<Drawing> Drawings(Drawing drawing)
    {
        yield return drawing;if(drawing is DrawingGroup group)foreach(var child in group.Children)foreach(var nested in Drawings(child))yield return nested;
    }
    public static IEnumerable<object[]> UserFormulas()
    {
        yield return [@"\int x^n \, dx = \frac{x^{n+1}}{n+1} + C, \quad n \neq -1"];
        yield return [@"\frac{d}{dx} \left( f(x) \cdot g(x) \right) = f'(x)g(x) + f(x)g'(x)"];
        yield return [@"(a+b)^n=\sum_{k=0}^{n}\binom{n}{k}a^{n-k}b^k,\quad\text{где }\binom{n}{k}=\frac{n!}{k!(n-k)!}"];
        yield return [@"x_{1,2}=\frac{-b\pm\sqrt{b^2-4ac}}{2a}"];
        yield return [@"\left(\frac{x^2-4}{x-2}\right)^2-n!=(x+2)^2-n!,\quad x\ne2"];
    }
    [Theory]
    [MemberData(nameof(UserFormulas))]
    public async Task UserFormulasRemainEditableSourceAndBecomeFrozenVectors(string formula)
    {
        using var service=new MarkdownLayoutService();var source="# Формула\n\n$$\n"+formula+"\n$$";var document=Document(source);
        var result=await service.LayoutAsync(document,SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,default);
        var page=Assert.Single(result.Pages);Assert.Equal(source,document.Source);Assert.True(page.Drawing.IsFrozen);
        Assert.DoesNotContain(Drawings(page.Drawing),d=>d is GlyphRunDrawing);
        Assert.True(Drawings(page.Drawing).OfType<GeometryDrawing>().Count()>3);
        using var raster=await Task.Run(()=>MarkdownDrawingRenderer.Render(page,120,default,800));
        Assert.True(DarkPixels(raster,new(0,0,raster.Width,raster.Height))>100);
    }
    [Fact]
    public async Task FormulaErrorsAreLineAwareAndKeepDraftUntouched()
    {
        using var service=new MarkdownLayoutService();var source="# Заголовок\n\n$$\n\\unknownPrintMasterCommand{x}\n$$";var doc=Document(source);
        var error=await Assert.ThrowsAsync<MarkdownLayoutException>(()=>service.LayoutAsync(doc,SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,default));
        Assert.Equal(3,error.Line);Assert.Contains("unknownPrintMasterCommand",error.Message);Assert.Equal(source,doc.Source);
    }
    [Fact]
    public async Task OversizedFormulaFailsBeforeCreatingClippedPages()
    {
        using var service=new MarkdownLayoutService();var source="$$\n"+string.Join("+",Enumerable.Repeat(@"\frac{x^2+1}{x+1}",100))+"\n$$";var doc=Document(source);
        var error=await Assert.ThrowsAsync<MarkdownLayoutException>(()=>service.LayoutAsync(doc,SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,default));
        Assert.Contains("не помещается",error.Message);Assert.Equal(1,error.Line);Assert.Equal(source,doc.Source);
    }
    [Theory]
    [InlineData(@"\quad",@"\;\;\;\,")]
    [InlineData(@"\qquad",@"\;\;\;\,\;\;\;\,")]
    [InlineData(@"\text{\quad}",@"\text{\quad}")]
    [InlineData(@"\\quad",@"\\quad")]
    [InlineData(@"\quadratic",@"\quadratic")]
    public void CompatibilitySpacingIsLexicalAndPreservesEscapesAndText(string original,string expected)
        =>Assert.Equal(expected,MarkdownFormula.Normalize(original));
    [Fact]
    public void ImageDiscoveryUsesAstAndResolvesReferencesWithoutReadingCodeOrHtml()
    {
        var source="![one](one.png)\n\n![again](one.png)\n\n![two][asset]\n\n[asset]: <my image.jpg>\n\n`![code](fake.png)`\n\n```text\n![code](fake2.png)\n```\n\n<img src='fake3.png'>";
        Assert.Equal(new[]{"one.png","my image.jpg"},MarkdownLayoutService.FindImageUrls(source));
    }
    [Fact]
    public async Task LongTableCodeAndUrlPrintEveryCharacterAndMarkerAcrossPages()
    {
        using var service=new MarkdownLayoutService();var source=new StringBuilder("# Данные\n\n| Row | Body | Value |\n| --- | --- | --- |\n");
        for(var i=1;i<=35;i++)source.Append($"| ROW-{i:000} | Русский текст строки {i}; длинное содержимое для переноса. | {i*2} |\n");
        var code=new string('X',700)+"LONG-CODE-END";var url="https://example.org/"+new string('a',700)+"/LONG-URL-END";
        source.Append("\n```text\n").Append(code).Append("\n```\n\n[").Append(url).Append("](").Append(url).Append(")\n\nTAIL\n");
        var result=await service.LayoutAsync(Document(source.ToString()),new(new(148,210),false,SheetMargins.SafeDefault),ImmutableArray<SourceAsset>.Empty,default);
        Assert.True(result.Pages.Length>2);var text=Visible(result);Assert.Contains(code,text);Assert.Contains(url,text);Assert.Contains("TAIL",text);
        for(var i=1;i<=35;i++)Assert.Equal(1,Count(text,$"ROW-{i:000}"));
        Assert.Equal(700,text.Count(c=>c=='X'));
        Assert.Equal(source.ToString().Count(c=>c=='Р'),text.Count(c=>c=='Р'));
    }
    [Fact]
    public async Task SoftBreakBecomesSpaceHardBreakRemainsSeparateAndGraphemesSurvive()
    {
        using var service=new MarkdownLayoutService();var source="SOFT\nbreak\n\nHARD  \nbreak\n\n```text\n"+string.Concat(Enumerable.Repeat("e\u0301👩🏽‍💻",80))+"\n```";
        var document=Document(source);var result=await service.LayoutAsync(document,SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,default);
        var text=Visible(result);Assert.Contains("SOFT break",text);Assert.DoesNotContain("HARD break",text);
        Assert.Equal(80,Count(text,"e\u0301"));Assert.Equal(80,Count(text,"👩"));Assert.Equal(80,Count(text,"🏽"));Assert.Equal(80,Count(text,"💻"));
        // WPF glyph metadata does not reliably retain zero-width shaping controls; geometry uses GlyphIndices.
        Assert.Equal(source,document.Source);Assert.Equal(80,document.Source.Count(c=>c=='\u200d'));
    }
    [Fact]
    public async Task PaperAndEffectiveMarginsDriveLayoutAndDpiDoesNotRepaginate()
    {
        using var service=new MarkdownLayoutService();var text=string.Join("\n\n",Enumerable.Range(1,60).Select(i=>$"Строка {i}: длинный текст для печати на разных листах."));
        var doc=Document(text,new(new(FontSizePt:13),new(8,9,11,12)));
        var paper=new SheetPaper(new(210,297),false,new(15,5,5,20));
        var a4=await service.LayoutAsync(doc,paper,ImmutableArray<SourceAsset>.Empty,default);
        var a5=await service.LayoutAsync(doc,new(new(148,210),false,SheetMargins.SafeDefault),ImmutableArray<SourceAsset>.Empty,default);
        Assert.Equal(new RectD(15,9,184,268),a4.Pages[0].SafeAreaMm);Assert.True(a5.Pages.Length>a4.Pages.Length);
        var page=a5.Pages[0];using var low=await Task.Run(()=>MarkdownDrawingRenderer.Render(page,120,default,800));
        using var high=await Task.Run(()=>MarkdownDrawingRenderer.Render(page,600,default,null));
        Assert.Equal((3497,4961),(high.Width,high.Height));Assert.True(high.Width>low.Width*3);
        Assert.Same(a5,await service.LayoutAsync(doc,new(new(148,210),false,SheetMargins.SafeDefault),ImmutableArray<SourceAsset>.Empty,default));
    }
    [Theory]
    [InlineData(NotebookVariant.GridSingle,false)]
    [InlineData(NotebookVariant.GridSingle,true)]
    [InlineData(NotebookVariant.GridSpread,false)]
    [InlineData(NotebookVariant.GridSpread,true)]
    [InlineData(NotebookVariant.RuledSingle,false)]
    [InlineData(NotebookVariant.RuledSingle,true)]
    [InlineData(NotebookVariant.RuledSpread,false)]
    [InlineData(NotebookVariant.RuledSpread,true)]
    public async Task NotebookVariantsAndBothOrientationsUseNativeRegionsWithoutPrintingGuides(NotebookVariant variant,bool landscape)
    {
        using var service=new MarkdownLayoutService();var profile=NotebookProfiles.Default(variant) with {LeftMarginMm=27,RightMarginMm=14,FoldMm=165};var paper=profile.ToPaper(landscape:landscape);
        var doc=Document(string.Join("\n\n",Enumerable.Range(1,40).Select(i=>$"NB-{i:000} text")));
        var result=await service.LayoutAsync(doc,paper,ImmutableArray<SourceAsset>.Empty,default);var text=Visible(result);
        for(var i=1;i<=40;i++)Assert.Equal(1,Count(text,$"NB-{i:000}"));
        Assert.All(result.Pages,p=>Assert.Equal(paper.OrientedSizeMm,p.PaperMm));
        using var raster=await Task.Run(()=>MarkdownDrawingRenderer.Render(result.Pages[0],120,default,1600));
        if(profile.IsSpread)
        {
            Assert.Single(result.Pages);
            var mid=NotebookGeometry.ToPaper(paper,new(profile.FoldMm,profile.HeightMm/2));
            var sx=raster.Width/paper.OrientedSizeMm.Width;var sy=raster.Height/paper.OrientedSizeMm.Height;
            var gap=NotebookGeometry.IsRotated(paper) ? new Rectangle(0,(int)((mid.Y-4)*sy),raster.Width,(int)(8*sy)) : new Rectangle((int)((mid.X-4)*sx),0,(int)(8*sx),raster.Height);
            Assert.Equal(0,DarkPixels(raster,gap));
        }
        // A guide grid would put ink across this empty bottom margin on every paper orientation.
        Assert.Equal(0,DarkPixels(raster,new(0,raster.Height-2,raster.Width,2)));
    }
    [Fact]
    public async Task FrozenPlansRenderOnMtaAndCacheEvictsOldDocuments()
    {
        using var service=new MarkdownLayoutService(cacheEntries:2);
        var first=Document("First document");var second=Document("Second document");var third=Document("Third document");
        var a=await service.LayoutAsync(first,SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,default);
        Assert.Same(a,await service.LayoutAsync(first,SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,default));
        _=await service.LayoutAsync(second,SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,default);_=await service.LayoutAsync(third,SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,default);
        Assert.Equal(2,service.CachedEntryCount);var again=await service.LayoutAsync(first,SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,default);Assert.NotSame(a,again);
        var pixels=await Task.Run(()=>{Assert.Equal(ApartmentState.MTA,Thread.CurrentThread.GetApartmentState());using var bitmap=MarkdownDrawingRenderer.Render(a.Pages[0],120,default,500);return DarkPixels(bitmap,new(0,0,bitmap.Width,bitmap.Height));});
        Assert.True(pixels>30);
    }
    [Fact]
    public async Task UiSyncReadsOnlyReadyPlansHonorsCancelAndLruEviction()
    {
        using var service=new MarkdownLayoutService(cacheEntries:2);var doc=Document("Cached UI background");
        var ready=await service.LayoutAsync(doc,SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,default);
        service.UiThreadAccess=()=>true;
        Assert.Same(ready,service.Layout(doc,SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,default));
        Assert.Throws<InvalidOperationException>(()=>service.Layout(Document("Unprepared"),SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,default));
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(()=>service.Layout(doc,SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,cancel.Token));
        _=await service.LayoutAsync(Document("Other 1"),SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,default);
        _=await service.LayoutAsync(Document("Other 2"),SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,default);
        Assert.Equal(2,service.CachedEntryCount);
        Assert.Throws<InvalidOperationException>(()=>service.Layout(doc,SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,default));
    }
    [Theory]
    [InlineData(1000)]
    [InlineData(333)]
    public async Task PreviewAlwaysRespectsPixelCapDespiteFloatingPointCeiling(int cap)
    {
        using var service=new MarkdownLayoutService();var layout=await service.LayoutAsync(Document("Preview"),SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,default);
        using var preview=await Task.Run(()=>MarkdownDrawingRenderer.Render(layout.Pages[0],120,default,cap));
        Assert.InRange(preview.Width,1,cap);Assert.InRange(preview.Height,1,cap);Assert.Equal(cap,Math.Max(preview.Width,preview.Height));
    }
    [Fact]
    public async Task MissingFontIsExplicitFallbackAndHtmlIsLiteral()
    {
        using var service=new MarkdownLayoutService();var result=await service.LayoutAsync(Document("<b>literal</b> and normal",new(new(FontFamily:"__PrintMaster_missing__"),new(10,10,10,10))),SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,default);
        Assert.Contains(result.Diagnostics,d=>d.Code=="font-fallback" && d.Message.Contains("Segoe UI",StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics,d=>d.Code=="html-literal");Assert.Contains("<b>literal</b>",Visible(result));
    }
    [Fact]
    public async Task ImagesResolveToOriginalFilesForHighResolutionAndAreUnlockedAfterRender()
    {
        var root=Path.Combine(Path.GetTempPath(),"PrintMaster-Markdown-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var file=Path.Combine(root,"photo.png");
        try
        {
            using(var original=new Bitmap(600,300)){using var g=Graphics.FromImage(original);g.Clear(GdiColor.Red);g.FillRectangle(BrushesForGdi.Blue,300,0,300,300);original.Save(file,System.Drawing.Imaging.ImageFormat.Png);}
            var asset=new SourceAsset(Guid.NewGuid(),SourceKind.Image,file,file,0,1,new(1000,500),new(100,50));
            using var service=new MarkdownLayoutService();var doc=Document("![photo](photo.png)",images:[new("photo.png",asset.Id)]);
            var layout=await service.LayoutAsync(doc,SheetPaper.A4,[asset],default);var page=Assert.Single(layout.Pages);
            var mapped=Assert.Single(page.ImageFiles);Assert.Equal(Path.GetFullPath(file),mapped.Value);Assert.True(mapped.Key.IsFrozen);
            using var rendered=await Task.Run(()=>MarkdownDrawingRenderer.Render(page,300,default,null));
            var pxPerMm=300/25.4;Assert.Equal(GdiColor.Red.ToArgb(),rendered.GetPixel((int)(30*pxPerMm),(int)(30*pxPerMm)).ToArgb());
            Assert.Equal(GdiColor.Blue.ToArgb(),rendered.GetPixel((int)(80*pxPerMm),(int)(30*pxPerMm)).ToArgb());
            using var exclusive=File.Open(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None);Assert.True(exclusive.Length>0);
        }
        finally {if(File.Exists(file))File.Delete(file);if(Directory.Exists(root))Directory.Delete(root);}
    }
    [Fact]
    public async Task MissingImageAndCanceledRequestsNeverProduceFakeSuccessPages()
    {
        using var service=new MarkdownLayoutService();var doc=Document("![absent](missing.png)");
        var error=await Assert.ThrowsAsync<MarkdownLayoutException>(()=>service.LayoutAsync(doc,SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,default));Assert.Equal(1,error.Line);Assert.Contains("missing.png",error.Message);
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>service.LayoutAsync(Document("Text"),SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,cancel.Token));
    }
    [Fact]
    public async Task InvalidMarginsAndOversizedTextFailRatherThanClip()
    {
        using var service=new MarkdownLayoutService();
        await Assert.ThrowsAsync<MarkdownLayoutException>(()=>service.LayoutAsync(Document("Text",new(new(),new(150,10,150,10))),SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,default));
        await Assert.ThrowsAsync<MarkdownLayoutException>(()=>service.LayoutAsync(Document("Text",new(new(FontSizePt:1000),new(10,10,10,10))),SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,default));
    }
    private static int DarkPixels(Bitmap bitmap,Rectangle area)
    {
        var clipped=Rectangle.Intersect(new(0,0,bitmap.Width,bitmap.Height),area);var count=0;
        for(var y=clipped.Top;y<clipped.Bottom;y++)for(var x=clipped.Left;x<clipped.Right;x++){var c=bitmap.GetPixel(x,y);if(c.R<160 || c.G<160 || c.B<160)count++;}
        return count;
    }
    private static class BrushesForGdi {public static System.Drawing.Brush Blue=>System.Drawing.Brushes.Blue;}
}
