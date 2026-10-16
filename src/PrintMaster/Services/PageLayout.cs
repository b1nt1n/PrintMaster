using PrintMaster.Models;
using System.Drawing.Printing;

namespace PrintMaster.Services;

public static class PageLayout
{
    public static int MmToHundredths(double mm) => checked((int)Math.Round(mm/25.4*100,MidpointRounding.AwayFromZero));
    public static double HundredthsToMm(double value) => value * 25.4/100;
    public static PaperSize CreatePaper(PrintSettingsSnapshot settings,PrinterSettings printer)
    {
        var kind = settings.Paper switch { PaperPreset.A4 => PaperKind.A4,PaperPreset.A5 => PaperKind.A5,_=>PaperKind.Custom };
        if (kind != PaperKind.Custom)
        {
            var stock = printer.PaperSizes.Cast<PaperSize>().FirstOrDefault(x=>x.Kind==kind);
            if (stock != null) return stock;
        }
        return new PaperSize(settings.Paper == PaperPreset.Notebook ? $"PrintMaster {settings.PaperSizeMm.Width:g} x {settings.PaperSizeMm.Height:g} mm" : "PrintMaster custom",
            MmToHundredths(settings.PaperSizeMm.Width),MmToHundredths(settings.PaperSizeMm.Height));
    }
    public static RectD SafeArea(SizeD paper,RectD hardware,double safeMargin)
    {
        var physical = new RectD(0,0,paper.Width,paper.Height);
        var inset = new RectD(safeMargin,safeMargin,paper.Width-2*safeMargin,paper.Height-2*safeMargin);
        var safe = physical.Intersect(inset).Intersect(hardware);
        if (safe.IsEmpty) throw new InvalidOperationException("Печатная область пуста. Проверьте бумагу и поля драйвера.");
        return safe;
    }
    public static RectD PrintSafeArea(SizeD paper,RectD hardware,double safeMargin)
    {
        // PageBounds/MarginBounds are integer hundredths of an inch. Use the same
        // quantization for preview, alignment and preflight as PrintPage will use.
        var bounds=new SizeD(HundredthsToMm(MmToHundredths(paper.Width)),HundredthsToMm(MmToHundredths(paper.Height)));
        var margin=HundredthsToMm(MmToHundredths(safeMargin));
        return SafeArea(bounds,hardware,Math.Max(safeMargin,margin));
    }
    public static RectD Place(SizeD content,RectD target,ScaleMode scaling)
    {
        if (content.Width<=0 || content.Height<=0 || target.IsEmpty) throw new ArgumentException("Недопустимые размеры страницы.");
        if(scaling==ScaleMode.Stretch) return target;
        var factor = scaling==ScaleMode.Fit ? Math.Min(target.Width/content.Width,target.Height/content.Height)
            : Math.Max(target.Width/content.Width,target.Height/content.Height);
        var w=content.Width*factor; var h=content.Height*factor;
        return new(target.X+(target.Width-w)/2,target.Y+(target.Height-h)/2,w,h);
    }
    public static bool Crops(SizeD content,RectD target,ScaleMode scaling)
    {
        if (scaling is ScaleMode.Fit or ScaleMode.Stretch) return false;
        var placement = Place(content,target,scaling);
        return placement.Width > target.Width+0.01 || placement.Height > target.Height+0.01;
    }
    public static IEnumerable<PageSnapshot> ExpandCopies(IEnumerable<PageSnapshot> pages)
    {
        foreach (var page in pages)
        {
            if (page.Copies is < 1 or > 999) throw new ArgumentException("Количество копий: от 1 до 999.");
            for (var i=0;i<page.Copies;i++) yield return page;
        }
    }
}
