using PrintMaster.Models;
using PrintMaster.Services;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using WpfMath.Parsers;
using WpfMath.Rendering;
using XamlMath;
using MarkdownDocument=PrintMaster.Models.MarkdownDocument;

namespace PrintMaster.FontLifecycle;

internal static class Program
{
    private static readonly string[] Formulas=
    [
        @"\int x^n \,dx=\frac{x^{n+1}}{n+1}+C,\quad n\neq-1",
        @"\frac{d}{dx}\left(f(x)\cdot g(x)\right)=f'(x)g(x)+f(x)g'(x)",
        @"(a+b)^n=\sum_{k=0}^{n}\binom{n}{k}a^{n-k}b^k,\quad\text{где }\binom{n}{k}=\frac{n!}{k!(n-k)!}",
        @"x_{1,2}=\frac{-b\pm\sqrt{b^2-4ac}}{2a}",
        @"\left(\frac{x^2-4}{x-2}\right)^2-n!=(x+2)^2-n!,\quad x\ne2"
    ];
    [STAThread]
    public static int Main(string[] args)
    {
        var mode=args.FirstOrDefault() ?? "cold";var path=Path.GetFullPath(args.Length>1 ? args[1] : "report.json");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var baseline=mode=="compare" ? Formulas.Select(StockBounds).ToArray() : null;
            var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};app.Startup+=(_,_)=>app.Shutdown();app.Run();
            // First production font access occurs after shutdown in cold mode, on the service's private STA.
            var production=Formulas.Select(source=>
            {
                var document=new MarkdownDocument(Guid.NewGuid(),"$$\n"+source+"\n$$",MarkdownStyle.Default,[]);
                var result=MarkdownLayoutService.Shared.LayoutAsync(document,SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,default).GetAwaiter().GetResult();
                var page=result.Pages.Single();var bounds=InkBounds(page.Drawing,Matrix.Identity,1);
                using var bitmap=Task.Run(()=>MarkdownDrawingRenderer.Render(page,120,default,600)).GetAwaiter().GetResult();
                return new Metrics(source,bounds.Width,bounds.Height,page.Drawing.IsFrozen,bitmap.Width,bitmap.Height);
            }).ToArray();
            var matches=baseline==null || baseline.Zip(production).All(p=>Math.Abs(p.First.Width-p.Second.Width)<.0001 && Math.Abs(p.First.Height-p.Second.Height)<.0001);
            var report=new {Mode=mode,Success=matches,ApplicationShutdown=true,VisibleWindows=0,PhysicalPrints=0,ColdProductionFonts=mode=="cold",Baseline=baseline,Production=production,MetricsMatch=matches};
            var json=JsonSerializer.Serialize(report,new JsonSerializerOptions {WriteIndented=true});File.WriteAllText(path,json);Console.WriteLine(json);return matches ? 0 : 2;
        }
        catch(Exception ex)
        {
            var json=JsonSerializer.Serialize(new {Mode=mode,Success=false,VisibleWindows=0,PhysicalPrints=0,Error=ex.ToString()},new JsonSerializerOptions {WriteIndented=true});File.WriteAllText(path,json);Console.Error.WriteLine(json);return 1;
        }
    }
    private static Metrics StockBounds(string source)
    {
        var formula=WpfTeXFormulaParser.Instance.Parse(source.Replace(@"\quad",@"\;\;\;\,"));var env=WpfTeXEnvironment.Create(TexStyle.Display,16,"Segoe UI");
        var drawing=new DrawingGroup();using(var dc=drawing.Open())formula.RenderTo(dc,env,16);drawing.Freeze();
        var b=InkBounds(drawing,Matrix.Identity,1);return new(source,b.Width,b.Height,true,0,0);
    }
    private static Rect InkBounds(Drawing drawing,Matrix parent,double opacity)
    {
        if(drawing is DrawingGroup group)
        {
            var transform=group.Transform?.Value ?? Matrix.Identity;transform.Append(parent);var result=Rect.Empty;
            foreach(var child in group.Children)result.Union(InkBounds(child,transform,opacity*group.Opacity));return result;
        }
        var visible=drawing switch
        {
            GeometryDrawing {Brush:SolidColorBrush brush}=>brush.Color.A*brush.Opacity*opacity>0,
            GeometryDrawing {Pen.Brush:SolidColorBrush brush}=>brush.Color.A*brush.Opacity*opacity>0,
            GlyphRunDrawing {ForegroundBrush:SolidColorBrush brush}=>brush.Color.A*brush.Opacity*opacity>0,
            _=>false
        };
        if(!visible)return Rect.Empty;var rectangle=drawing is GlyphRunDrawing glyph ? glyph.GlyphRun.BuildGeometry().Bounds : drawing.Bounds;if(rectangle.IsEmpty)return rectangle;
        var corners=new[]{parent.Transform(rectangle.TopLeft),parent.Transform(rectangle.TopRight),parent.Transform(rectangle.BottomLeft),parent.Transform(rectangle.BottomRight)};
        return new Rect(new Point(corners.Min(p=>p.X),corners.Min(p=>p.Y)),new Point(corners.Max(p=>p.X),corners.Max(p=>p.Y)));
    }
    private sealed record Metrics(string Source,double Width,double Height,bool Frozen,int RasterWidth,int RasterHeight);
}
