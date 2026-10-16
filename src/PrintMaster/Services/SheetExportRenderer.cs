using PrintMaster.Models;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace PrintMaster.Services;

public sealed class SheetExportRenderer(PageRasterizer rasterizer)
{
    public static ExportPage Prepare(PageSnapshot page,SheetPaper automaticPaper,PrinterProfile? profile=null)
    {
        var paper=page.UsesAutomaticContentLayout ? automaticPaper : page.GetSheet().Paper;
        paper.Validate(); var size=paper.OrientedSizeMm;
        var hardware=profile is { IsVerified:true } && Math.Abs(profile.ActualPaperMm.Width-size.Width)<.3 && Math.Abs(profile.ActualPaperMm.Height-size.Height)<.3
            ? profile.PrintableMm : new RectD(0,0,size.Width,size.Height);
        var safe=PageLayout.PrintSafeArea(size,hardware,5).Intersect(paper.SafeAreaMm);
        if(safe.IsEmpty) throw new ArgumentException("Поля занимают весь лист.");
        return new(page,paper,safe);
    }
    public static void ValidatePages(IReadOnlyList<ExportPage> pages,ExportOptions options)
    {
        options.Validate(); if(pages.Count==0) throw new ArgumentException("Нет страниц для экспорта.");
        foreach(var item in pages)
        {
            if(item.Page.GetSheet().Objects.OfType<MarkdownObject>().Any(markdown=>markdown.Document?.LayoutEngineVersion!=MarkdownDocument.CurrentLayoutEngineVersion))
                throw new InvalidOperationException(MarkdownDocument.LayoutUpgradeMessage);
            item.Paper.Validate(); var size=item.Paper.OrientedSizeMm;
            BitmapLimits.Validate(checked((int)Math.Ceiling(size.Width/25.4*options.Dpi)),checked((int)Math.Ceiling(size.Height/25.4*options.Dpi)));
            if(item.Page.GetSheet().Assets.Any(a=>string.IsNullOrEmpty(a.WorkingPath) || !System.IO.File.Exists(a.WorkingPath)))
                throw new InvalidOperationException("Отсутствуют исходники выбранных страниц. Переподключите материалы проекта.");
            if(!options.AllowOverflow && HasOverflow(item.Page)) throw new InvalidOperationException("Текст не помещается. Подтвердите экспорт видимой части; полный текст останется в проекте.");
            if(!options.AllowClipping && HasClipping(item,options)) throw new InvalidOperationException("Содержимое будет обрезано. Подтвердите экспорт видимой части или исправьте макет.");
        }
    }
    public static bool HasOverflow(PageSnapshot page)=>page.GetSheet() is {} sheet &&
        (NotebookTextLayout.Remainder(sheet).Length>0 || sheet.Objects.OfType<TextObject>().Any(t=>t.Visible &&
            (TextLayoutService.Layout(t).WidthOverflow || TextLayoutService.Layout(t).HeightMm>t.Placement.BoundsMm.Height+.05)));
    public static bool HasClipping(ExportPage item,ExportOptions options)
    {
        var size=item.Paper.OrientedSizeMm;
        var area=options.Appearance==ExportAppearance.Monochrome || item.Page.UsesAutomaticContentLayout ? item.SafeAreaMm : new RectD(0,0,size.Width,size.Height);
        return item.Page.UsesAutomaticContentLayout && PageLayout.Crops(item.Page.ContentSize,area,options.Scaling)
            || SheetEditing.HasClipping(item.Page,area);
    }
    public Bitmap Render(ExportPage item,ExportOptions options,CancellationToken token,int? previewCap=null)
    {
        var size=item.Paper.OrientedSizeMm; var scale=options.Dpi/25.4;
        if(previewCap is {} cap) scale=Math.Min(scale,cap/Math.Max(size.Width,size.Height));
        var width=Math.Max(1,checked((int)Math.Ceiling(size.Width*scale))); var height=Math.Max(1,checked((int)Math.Ceiling(size.Height*scale)));
        // Actual pixel-to-mm ratios also cover the sub-pixel rounding at the paper edge.
        var sx=width/size.Width; var sy=height/size.Height; var safe=item.SafeAreaMm;
        var safePx=new RectD(safe.X*sx,safe.Y*sy,safe.Width*sx,safe.Height*sy);
        var destination=PageLayout.Place(item.Page.ContentSize,safePx,options.Scaling);
        var bitmap=rasterizer.RenderViewport(item.Page,width,height,destination,options.Appearance==ExportAppearance.Monochrome,token,new(0,0,size.Width,size.Height));
        try
        {
            bitmap.SetResolution(previewCap==null ? options.Dpi : 120,previewCap==null ? options.Dpi : 120);
            if(item.Page.UsesAutomaticContentLayout || options.Appearance==ExportAppearance.Monochrome)
            {
                using var g=Graphics.FromImage(bitmap); using var outside=new Region(new Rectangle(0,0,width,height));
                outside.Exclude(new Rectangle((int)Math.Ceiling(safePx.X),(int)Math.Ceiling(safePx.Y),
                    Math.Max(0,(int)Math.Floor(safePx.Right)-(int)Math.Ceiling(safePx.X)),Math.Max(0,(int)Math.Floor(safePx.Bottom)-(int)Math.Ceiling(safePx.Y))));
                g.SetClip(outside,CombineMode.Replace); g.FillRectangle(Brushes.White,0,0,width,height);
            }
            if(options.Appearance==ExportAppearance.Monochrome)
                MonochromeRasterizer.Apply(bitmap,MonochromeRasterizer.Resolve(options.Monochrome,item.Page.HasImages ? SourceKind.Image : item.Page.Kind),options.Threshold,token);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }
}
