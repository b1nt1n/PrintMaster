using PrintMaster.Models;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using WpfMath.Parsers;
using WpfMath.Rendering;
using XamlMath;
using MdTable=Markdig.Extensions.Tables.Table;
using MdRow=Markdig.Extensions.Tables.TableRow;
using MdCell=Markdig.Extensions.Tables.TableCell;
using WpfTable=System.Windows.Documents.Table;
using WpfRow=System.Windows.Documents.TableRow;
using WpfCell=System.Windows.Documents.TableCell;
using WpfBlock=System.Windows.Documents.Block;
using MarkdownDocument=PrintMaster.Models.MarkdownDocument;

namespace PrintMaster.Services;

internal sealed record MarkdownFormula(DrawingGroup Drawing,Size Size)
{
    public static MarkdownFormula Make(string source,TexStyle style,double em,string font,Brush foreground)
    {
        var formula=WpfTeXFormulaParser.Instance.Parse(Normalize(source));var environment=MarkdownFontProvider.CreateEnvironment(style,em,font,foreground);
        var metrics=new MetricRenderer();XamlMath.Rendering.TeXFormulaExtensions.RenderTo(formula,metrics,environment,0,0);var box=metrics.Box!;
        var raw=new DrawingGroup();using(var dc=raw.Open())MarkdownFontProvider.Draw(formula,dc,environment,em);
        var bounds=raw.Bounds;var left=Math.Min(0,bounds.Left);var top=Math.Min(0,bounds.Top);
        var drawing=new DrawingGroup();using(var dc=drawing.Open()) {dc.PushTransform(new TranslateTransform(-left,-top));dc.DrawDrawing(raw);dc.Pop();}
        drawing.Freeze();return new(drawing,new(Math.Max(box.TotalWidth*em,bounds.Right-left)+1,Math.Max(box.TotalHeight*em,bounds.Bottom-top)+1));
    }
    internal static string Normalize(string source)
    {
        const string quad=@"\;\;\;\,";var result=new StringBuilder();
        for(var i=0;i<source.Length;)
        {
            if(source[i]!='\\' || i+1>=source.Length){result.Append(source[i++]);continue;}
            if(source[i+1]=='\\'){result.Append("\\\\");i+=2;continue;}
            var start=i++;while(i<source.Length && char.IsLetter(source[i]))i++;
            if(i==start+1){var count=Math.Min(2,source.Length-start);result.Append(source.AsSpan(start,count));i=start+count;continue;}
            var command=source[(start+1)..i];
            if(command=="text")
            {
                var end=i;while(end<source.Length && char.IsWhiteSpace(source[end]))end++;
                if(end<source.Length && source[end]=='{')
                {
                    var depth=0;do {if(source[end]=='\\' && end+1<source.Length){end+=2;continue;}if(source[end]=='{')depth++;else if(source[end]=='}')depth--;end++;}while(end<source.Length && depth>0);
                    result.Append(source.AsSpan(start,end-start));i=end;continue;
                }
            }
            result.Append(command switch {"quad"=>quad,"qquad"=>quad+quad,_=>source[start..i]});
        }
        return MarkdownFormulaEnvironments.Normalize(result.ToString());
    }
    private sealed class MetricRenderer : XamlMath.Rendering.IElementRenderer
    {
        public XamlMath.Boxes.Box? Box {get;private set;}
        public void RenderElement(XamlMath.Boxes.Box box,double x,double y)=>Box=box;
        public void RenderCharacter(CharInfo info,double x,double y,XamlMath.Rendering.IBrush? foreground) { }
        public void RenderLine(XamlMath.Rendering.Point a,XamlMath.Rendering.Point b,XamlMath.Rendering.IBrush? foreground) { }
        public void RenderRectangle(XamlMath.Rendering.Rectangle rectangle,XamlMath.Rendering.IBrush? foreground) { }
        public void RenderTransformed(XamlMath.Boxes.Box box,IEnumerable<XamlMath.Rendering.Transformations.Transformation> transforms,double x,double y) { }
        public void FinishRendering() { }
    }
}

