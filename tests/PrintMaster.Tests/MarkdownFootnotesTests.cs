using PrintMaster.Models;
using PrintMaster.Services;
using System.Collections.Immutable;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Media;
using Xunit;

namespace PrintMaster.Tests;

public sealed class MarkdownFootnotesTests
{
    private const double DipPerMm=96d/25.4;
    private static MarkdownDocument Document(string source)=>new(Guid.NewGuid(),source,MarkdownStyle.Default,[],"Footnotes");
    private static int Count(string text,string word)=>text.Split(word,StringSplitOptions.None).Length-1;
    private static string Visible(MarkdownLayoutResult result)=>string.Concat(result.Pages.Select(p=>p.DiagnosticText)).Replace("\u200b","");

    [Fact]
    public async Task NumericAndNamedFootnotesArePrintedAtPhysicalBottomAndUnusedDefinitionIsOmitted()
    {
        using var service=new MarkdownLayoutService();
        var source="BODY_START[^1] and OTHER_REFERENCE[^about]. Again[^1].\n\n[^1]: FIRST_FOOTER unique definition.\n[^about]: NAMED_FOOTER **bold** and *italic*.\n[^unused]: UNUSED_DEFINITION";
        var result=await service.LayoutAsync(Document(source),SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,default);
        var page=Assert.Single(result.Pages);var text=Visible(result);
        Assert.Equal(1,Count(text,"FIRST_FOOTER"));Assert.Equal(1,Count(text,"NAMED_FOOTER"));Assert.DoesNotContain("UNUSED_DEFINITION",text);
        Assert.DoesNotContain("[^",text);Assert.Contains("1. FIRST_FOOTER",text);Assert.Contains("2. NAMED_FOOTER",text);
        var footer=Assert.IsType<DrawingGroup>(page.Drawing.Children[1]);
        Assert.True(footer.Transform.Value.OffsetY>250*DipPerMm,$"Footer top: {footer.Transform.Value.OffsetY/DipPerMm}");
        using var raster=await Task.Run(()=>MarkdownDrawingRenderer.Render(page,120,default,1000));
        Assert.True(DarkPixels(raster,new(0,(int)(raster.Height*.85),raster.Width,(int)(raster.Height*.12)))>50);
        Assert.Equal(0,DarkPixels(raster,new(0,(int)(raster.Height*.3),raster.Width,(int)(raster.Height*.3))));
    }

    [Fact]
    public async Task ReferencesNearPageBoundariesMoveWithFooterWithoutLostOrDuplicatedBodyText()
    {
        using var service=new MarkdownLayoutService();var source=new StringBuilder();
        for(var i=0;i<110;i++) source.Append($"PARAGRAPH_{i:000}: Every body marker must survive pagination.").Append(i%7==0 ? $"[^n{i}]" : "").Append("\n\n");
        for(var i=0;i<110;i+=7)source.Append($"[^n{i}]: FOOTNOTE_{i:000}: a note belonging to the referenced paragraph.\n");
        var result=await service.LayoutAsync(Document(source.ToString()),new(new(105,148),false,SheetMargins.SafeDefault),ImmutableArray<SourceAsset>.Empty,default);
        Assert.True(result.Pages.Length>5);var text=Visible(result);
        for(var i=0;i<110;i++)Assert.Equal(1,Count(text,$"PARAGRAPH_{i:000}"));
        for(var i=0;i<110;i+=7)
        {
            var page=Assert.Single(result.Pages.Where(p=>p.DiagnosticText.Contains($"PARAGRAPH_{i:000}",StringComparison.Ordinal)));
            Assert.Contains($"FOOTNOTE_{i:000}",page.DiagnosticText);Assert.Equal(1,Count(text,$"FOOTNOTE_{i:000}"));
        }
    }

    [Fact]
    public async Task RepeatedReferenceOnDifferentPagesRepeatsItsDefinitionOnEachReferencedPageOnly()
    {
        using var service=new MarkdownLayoutService();
        var source="FIRST_REFERENCE[^same]\n\n"+string.Join("\n\n",Enumerable.Range(0,70).Select(i=>$"BODY_ROW_{i:000}: Long content fills several small pages."))+"\n\nLAST_REFERENCE[^same]\n\n[^same]: REPEATED_FOOTER";
        var result=await service.LayoutAsync(Document(source),new(new(105,148),false,SheetMargins.SafeDefault),ImmutableArray<SourceAsset>.Empty,default);
        Assert.True(result.Pages.Length>3);
        var referenced=result.Pages.Where(p=>p.DiagnosticText.Contains("FIRST_REFERENCE",StringComparison.Ordinal)||p.DiagnosticText.Contains("LAST_REFERENCE",StringComparison.Ordinal)).ToArray();
        Assert.Equal(2,referenced.Length);Assert.All(referenced,p=>Assert.Equal(1,Count(p.DiagnosticText,"REPEATED_FOOTER")));
        Assert.All(result.Pages.Except(referenced),p=>Assert.DoesNotContain("REPEATED_FOOTER",p.DiagnosticText));
    }

    [Fact]
    public async Task FootnoteParagraphsCodeAndFormulaStayInFooterAndSurviveGdiRasterization()
    {
        using var service=new MarkdownLayoutService();
        var source="BODY_WITH_NOTE[^about].\n\n[^about]: FIRST_NOTE_PARAGRAPH **emphasized**.\n\n    SECOND_NOTE_PARAGRAPH $\\frac{a}{b}$\n\n    ```text\n    NOTE_CODE\n        nested\n    ```\n";
        var result=await service.LayoutAsync(Document(source),SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,default);var page=Assert.Single(result.Pages);
        Assert.Contains("FIRST_NOTE_PARAGRAPH",page.DiagnosticText);Assert.Contains("SECOND_NOTE_PARAGRAPH",page.DiagnosticText);Assert.Contains("NOTE_CODE",page.DiagnosticText);
        Assert.DoesNotContain("\\frac",page.DiagnosticText);Assert.True(page.Drawing.IsFrozen);
        using var raster=await Task.Run(()=>MarkdownDrawingRenderer.Render(page,300,default,1600));
        Assert.True(DarkPixels(raster,new(0,(int)(raster.Height*.65),raster.Width,(int)(raster.Height*.3)))>250);
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
    public async Task NotebookFootersRespectNativeColumnsAndBothOrientations(NotebookVariant variant,bool landscape)
    {
        using var service=new MarkdownLayoutService();var profile=NotebookProfiles.Default(variant);var paper=profile.ToPaper(landscape:landscape);
        var source=new StringBuilder();for(var i=0;i<22;i++)source.Append($"NOTEBOOK_ROW_{i:000} text").Append(i is 0 or 19 ? $"[^n{i}]" : "").Append("\n\n");
        source.Append("[^n0]: LEFT_NOTE\n[^n19]: RIGHT_NOTE\n");
        var result=await service.LayoutAsync(Document(source.ToString()),paper,ImmutableArray<SourceAsset>.Empty,default);
        var text=Visible(result);for(var i=0;i<22;i++)Assert.Equal(1,Count(text,$"NOTEBOOK_ROW_{i:000}"));
        Assert.Equal(1,Count(text,"LEFT_NOTE"));Assert.Equal(1,Count(text,"RIGHT_NOTE"));
        Assert.All(result.Pages,p=>{Assert.Equal(paper.OrientedSizeMm,p.PaperMm);Assert.True(p.Drawing.IsFrozen);});
        if(profile.IsSpread)
        {
            foreach(var page in result.Pages)
            {
                using var raster=await Task.Run(()=>MarkdownDrawingRenderer.Render(page,120,default,1600));
                var fold=NotebookGeometry.ToPaper(paper,new(profile.FoldMm,profile.HeightMm/2));
                var sx=raster.Width/paper.OrientedSizeMm.Width;var sy=raster.Height/paper.OrientedSizeMm.Height;
                var gap=NotebookGeometry.IsRotated(paper) ? new Rectangle(0,(int)((fold.Y-2)*sy),raster.Width,(int)(4*sy)) : new Rectangle((int)((fold.X-2)*sx),0,(int)(4*sx),raster.Height);
                Assert.Equal(0,DarkPixels(raster,gap));
            }
        }
    }

    [Fact]
    public async Task TooLongFootnoteIsExplicitFailureInsteadOfSilentlyLosingTail()
    {
        using var service=new MarkdownLayoutService();var source="Reference[^huge].\n\n[^huge]: "+string.Join(" ",Enumerable.Repeat("HUGE_FOOTNOTE_CONTENT",1500));
        var error=await Assert.ThrowsAsync<MarkdownLayoutException>(()=>service.LayoutAsync(Document(source),new(new(105,148),false,SheetMargins.SafeDefault),ImmutableArray<SourceAsset>.Empty,default));
        Assert.Contains("сноск",error.Message.ToLowerInvariant());
    }

    [Fact]
    public async Task FootnoteImageRetainsOwnedOriginalAndPrintsAtFooterInsteadOfBody()
    {
        var directory=Path.Combine(Path.GetTempPath(),"PrintMaster-FootnoteImage-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);var file=Path.Combine(directory,"note.png");
        try
        {
            using(var original=new Bitmap(450,300)){using var graphics=Graphics.FromImage(original);graphics.Clear(System.Drawing.Color.Red);original.Save(file,System.Drawing.Imaging.ImageFormat.Png);}
            var asset=new SourceAsset(Guid.NewGuid(),SourceKind.Image,file,file,0,1,new(450,300),new(30,20));
            var source="BODY[^image]\n\n[^image]: IMAGE_NOTE ![illustration](note.png)";
            Assert.Contains("note.png",MarkdownLayoutService.FindImageUrls(source));
            var document=Document(source) with {Images=[new("note.png",asset.Id)]};using var service=new MarkdownLayoutService();
            var result=await service.LayoutAsync(document,SheetPaper.A4,[asset],default);var page=Assert.Single(result.Pages);
            Assert.Equal(Path.GetFullPath(file),Assert.Single(page.ImageFiles).Value);
            using var raster=await Task.Run(()=>MarkdownDrawingRenderer.Render(page,300,default,1200));
            var red=0;var firstRed=raster.Height;
            for(var y=0;y<raster.Height;y++)for(var x=0;x<raster.Width;x++){var color=raster.GetPixel(x,y);if(color.R>220&&color.G<30&&color.B<30){red++;firstRed=Math.Min(firstRed,y);}}
            Assert.True(red>1000);Assert.True(firstRed>raster.Height*.8,$"Image starts at {firstRed}/{raster.Height}");
            using var exclusive=File.Open(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None);Assert.True(exclusive.Length>0);
        }
        finally {if(File.Exists(file))File.Delete(file);if(Directory.Exists(directory))Directory.Delete(directory);}
    }

    private static int DarkPixels(Bitmap image,Rectangle rect)
    {
        rect=Rectangle.Intersect(rect,new(0,0,image.Width,image.Height));var count=0;
        for(var y=rect.Top;y<rect.Bottom;y++)for(var x=rect.Left;x<rect.Right;x++){var c=image.GetPixel(x,y);if(c.R<180&&c.G<180&&c.B<180)count++;}
        return count;
    }
}
