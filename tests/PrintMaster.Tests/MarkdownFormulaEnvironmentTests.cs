using PrintMaster.Models;
using PrintMaster.Services;
using System.Collections.Immutable;
using System.Windows.Media;
using Xunit;

namespace PrintMaster.Tests;

public sealed class MarkdownFormulaEnvironmentTests
{
    [Theory]
    [InlineData(@"\begin{matrix}a&b\\c&d\end{matrix}",@"\matrix{a&b\\c&d}")]
    [InlineData(@"\begin{pmatrix}a&b\\c&d\end{pmatrix}",@"\left(\matrix{a&b\\c&d}\right)")]
    [InlineData(@"\begin{bmatrix}a&b\\c&d\end{bmatrix}",@"\left[\matrix{a&b\\c&d}\right]")]
    [InlineData(@"\begin{Bmatrix}a&b\\c&d\end{Bmatrix}",@"\left\{\matrix{a&b\\c&d}\right\}")]
    [InlineData(@"\begin{Vmatrix}a&b\\c&d\end{Vmatrix}",@"\left\Vert\matrix{a&b\\c&d}\right\Vert")]
    [InlineData(@"\begin{vmatrix}a&b\\c&d\end{vmatrix}",@"\left|\matrix{a&b\\c&d}\right|")]
    [InlineData(@"|x|=\begin{cases}x,&x\ge0\\-x,&x<0\end{cases}",@"|x|=\cases{x,&x\ge0\\-x,&x<0}")]
    [InlineData(@"\begin{vmatrix}1&\begin{cases}x&x>0\\0&x\le0\end{cases}\\0&2\end{vmatrix}",@"\left|\matrix{1&\cases{x&x>0\\0&x\le0}\\0&2}\right|")]
    [InlineData(@"\begin{pmatrix}a&\begin{vmatrix}1&2\\3&4\end{vmatrix}\end{pmatrix}",@"\left(\matrix{a&\left|\matrix{1&2\\3&4}\right|}\right)")]
    [InlineData(@"\begin {cases} {x^2}&{x\ge0}\\{-x}&{x<0}\end {cases}",@"\cases{ {x^2}&{x\ge0}\\{-x}&{x<0}}")]
    [InlineData(@"\begin{cases}x&\text{literal \begin{cases} \end{cases}}\\y&\text{a & b}\end{cases}",@"\cases{x&\text{literal \begin{cases} \end{cases}}\\y&\text{a & b}}")]
    public void StandardEnvironmentsAdaptToExistingNativeAtoms(string source,string expected)
        =>Assert.Equal(expected,MarkdownFormula.Normalize(source));

    [Theory]
    [InlineData(@"\text{\begin{cases} not math \end{cases}}")]
    [InlineData(@"\\begin{cases}")]
    [InlineData(@"\beginning{cases}")]
    [InlineData(@"\cases{x,&\text{если }x>0\\0,&\text{иначе}}")]
    public void EnvironmentCompatibilityPreservesLiteralTextEscapesAndExistingCommands(string source)
        =>Assert.Equal(source,MarkdownFormula.Normalize(source));

    [Theory]
    [InlineData(@"\begin{cases}{x&x>0\end{cases}}")]
    [InlineData(@"\frac{1}{2")]
    [InlineData(@"x}")]
    [InlineData(@"\text{unclosed")]
    public void MalformedGroupingCannotBeReinterpretedByEnvironmentWrapping(string source)
        =>Assert.Throws<FormatException>(()=>MarkdownFormula.Normalize(source));

    public static IEnumerable<object[]> RenderedEnvironments()
    {
        yield return [@"\begin{matrix}a&b\\c&d\end{matrix}",@"\matrix{a&b\\c&d}"];
        yield return [@"\begin{pmatrix}a&b\\c&d\end{pmatrix}",@"\left(\matrix{a&b\\c&d}\right)"];
        yield return [@"\begin{bmatrix}a&b\\c&d\end{bmatrix}",@"\left[\matrix{a&b\\c&d}\right]"];
        yield return [@"\begin{Bmatrix}a&b\\c&d\end{Bmatrix}",@"\left\{\matrix{a&b\\c&d}\right\}"];
        yield return [@"\begin{Vmatrix}a&b\\c&d\end{Vmatrix}",@"\left\Vert\matrix{a&b\\c&d}\right\Vert"];
        yield return [@"\begin{pmatrix}1&\begin{bmatrix}2&3\\4&5\end{bmatrix}\\0&6\end{pmatrix}",@"\left(\matrix{1&\left[\matrix{2&3\\4&5}\right]\\0&6}\right)"];
        yield return [@"\begin{vmatrix}a&b\\c&d\end{vmatrix}=ad-bc",@"\left|\matrix{a&b\\c&d}\right|=ad-bc"];
        yield return [@"|x|=\begin{cases}x,&\text{если }x\ge0\\-x,&\text{если }x<0\end{cases}",@"|x|=\cases{x,&\text{если }x\ge0\\-x,&\text{если }x<0}"];
        yield return [@"\begin{vmatrix}1&\begin{vmatrix}2&3\\4&5\end{vmatrix}\\0&6\end{vmatrix}",@"\left|\matrix{1&\left|\matrix{2&3\\4&5}\right|\\0&6}\right|"];
        yield return [@"\begin{cases}\frac{1}{x}&\text{если }x\ne0\\\begin{vmatrix}1&0\\0&1\end{vmatrix}&\text{иначе}\end{cases}",@"\cases{\frac{1}{x}&\text{если }x\ne0\\\left|\matrix{1&0\\0&1}\right|&\text{иначе}}"];
    }

    [Theory]
    [MemberData(nameof(RenderedEnvironments))]
    public async Task StandardEnvironmentRenderMatchesNativeFormulaAndRetainsEditableSource(string source,string native)
    {
        using var service=new MarkdownLayoutService();
        var markdown="Перед формулой\n\n$$\n"+source+"\n$$\n\nПосле формулы";
        var document=Document(markdown);
        var actual=await service.LayoutAsync(document,SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,default);
        var expected=await service.LayoutAsync(Document(markdown.Replace(source,native,StringComparison.Ordinal)),SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,default);
        var actualPage=Assert.Single(actual.Pages);var expectedPage=Assert.Single(expected.Pages);
        Assert.Equal(markdown,document.Source);Assert.Empty(actual.Diagnostics);
        Assert.True(actualPage.Drawing.IsFrozen);Assert.Equal(expectedPage.Drawing.Bounds,actualPage.Drawing.Bounds);
        Assert.Contains("Перед формулой",actualPage.DiagnosticText);Assert.Contains("После формулы",actualPage.DiagnosticText);
        // A plain 2×2 matrix has fewer geometries than the nested cases below; compare the
        // complete native drawing instead of imposing a minimum tied to formula complexity.
        Assert.Equal(GeometryCount(expectedPage.Drawing),GeometryCount(actualPage.Drawing));
        // Pixel comparison checks the delimiter, cell alignment and all nested contents, not just successful parsing.
        using var actualBitmap=await Task.Run(()=>MarkdownDrawingRenderer.Render(actualPage,120,default,800));
        using var expectedBitmap=await Task.Run(()=>MarkdownDrawingRenderer.Render(expectedPage,120,default,800));
        Assert.Equal(expectedBitmap.Size,actualBitmap.Size);
        var different=0;
        for(var y=0;y<actualBitmap.Height;y++)for(var x=0;x<actualBitmap.Width;x++)
            if(actualBitmap.GetPixel(x,y)!=expectedBitmap.GetPixel(x,y))different++;
        Assert.Equal(0,different);
    }

    [Theory]
    [InlineData(@"\begin{cases}x&x>0\end{vmatrix}")]
    [InlineData(@"\begin{vmatrix}1&2\\3&4")]
    [InlineData(@"\begin{vmatrix}\begin{cases}x&x>0\end{vmatrix}\end{cases}")]
    [InlineData(@"\end{cases}")]
    [InlineData(@"\begin{cases}\unknownPrintMasterSymbol{x}&x>0\end{cases}")]
    public async Task MalformedEnvironmentsAndUnknownCommandsFailWithLineAndPreserveSource(string formula)
    {
        using var service=new MarkdownLayoutService();var source="# Заголовок\n\n$$\n"+formula+"\n$$";var document=Document(source);
        var error=await Assert.ThrowsAsync<MarkdownLayoutException>(()=>service.LayoutAsync(document,SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,default));
        Assert.Equal(3,error.Line);Assert.Contains("формул",error.Message);Assert.Equal(source,document.Source);
    }

    private static MarkdownDocument Document(string source)=>new(Guid.NewGuid(),source,MarkdownStyle.Default,[],"Formula environments");
    private static int GeometryCount(Drawing drawing)=>drawing switch
    {
        GeometryDrawing=>1,
        DrawingGroup group=>group.Children.Sum(GeometryCount),
        _=>0
    };
}
