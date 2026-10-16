using PrintMaster.Models;

namespace PrintMaster.Services;

public sealed record PreparedPrintSide(PageSnapshot Page,PrintSettingsSnapshot Settings,int? QueueOrdinal,bool IsBlank=false);
public sealed record DuplexSheet(int Number,PreparedPrintSide Front,PreparedPrintSide Back);
public sealed record PrintPacket(IReadOnlyList<PageSnapshot> Pages,PrintSettingsSnapshot Settings)
{
    public int Feeds=>Pages.Sum(p=>p.Copies);
}
public sealed record ManualDuplexPlan(IReadOnlyList<DuplexSheet> Sheets,IReadOnlyList<PrintPacket> FrontPackets,IReadOnlyList<PrintPacket> BackPackets)
{
    public int ContentImpressions=>Sheets.Sum(s=>(s.Front.IsBlank ? 0 : 1)+(s.Back.IsBlank ? 0 : 1));
    public int BlankSides=>Sheets.Count*2-ContentImpressions;
}

public static class ManualDuplexPlanner
{
    // Front/back sheet pairing and reverse second-pass order are adapted from
    // ndak79/myPrinter, backend/Models/PrintModels.cs, Git blob 1d9e834c9e8a739ad835acc259bd8f8be8935952.
    // Copyright (c) 2026 Smart Printer contributors. MIT: Assets/Licenses/myPrinter/LICENSE.
    // This adaptation adds immutable snapshots, per-side copies, actual paper sizes and blank feeds.
    public static ManualDuplexPlan Build(IReadOnlyList<PageSnapshot> pages,PrintSettingsSnapshot settings,bool faceDownStack=true,int backRotation=0)
    {
        ValidateQueue(pages,settings);
        if(backRotation is not (0 or 180)) throw new ArgumentException("Поворот оборота: 0° или 180°.");
        if(pages.Any(p=>p.Copies is <1 or >999) || pages.Sum(p=>(long)p.Copies)>100000)
            throw new ArgumentException("Помощник поддерживает до 100 000 оттисков с 1–999 копиями стороны.");
        var sheets=new List<DuplexSheet>();
        for(var index=0;index<pages.Count;)
        {
            var frontIndex=index++; var front=pages[frontIndex]; var frontSettings=ForPage(front,settings);
            PageSnapshot? back=null; var backIndex=-1; PrintSettingsSnapshot? backSettings=null;
            if(index<pages.Count && SameStock(frontSettings,ForPage(pages[index],settings)))
            { backIndex=index++; back=pages[backIndex]; backSettings=ForPage(back,settings); }
            // Pair queue sides before expanding copies: A×2 / B×1 means A/B and A/blank.
            for(var copy=0;copy<Math.Max(front.Copies,back?.Copies ?? 0);copy++)
            {
                var f=copy<front.Copies ? new PreparedPrintSide(front with { Copies=1 },frontSettings,frontIndex+1) : Blank(frontSettings);
                var b=back!=null && copy<back.Copies ? new PreparedPrintSide(back with { Copies=1 },backSettings!,backIndex+1) : Blank(frontSettings);
                sheets.Add(new(sheets.Count+1,f,b));
            }
        }
        var fronts=Packets(sheets.Select(s=>s.Front));
        var backs=sheets.All(s=>s.Back.IsBlank) ? [] : Packets((faceDownStack ? sheets.AsEnumerable().Reverse() : sheets).Select(s=>s.Back)
            .Select(s=>s with { Settings=s.Settings with { PageRotationDegrees=backRotation } }));
        return new(sheets,fronts,backs);
    }
    public static IReadOnlyList<PrintPacket> Simplex(IReadOnlyList<PageSnapshot> pages,PrintSettingsSnapshot settings)
    {
        ValidateQueue(pages,settings);
        return Packets(pages.Select((p,i)=>new PreparedPrintSide(p,ForPage(p,settings),i+1)));
    }
    public static PrintSettingsSnapshot ForPage(PageSnapshot page,PrintSettingsSnapshot source)
    {
        var effective=page.PrintOptions?.ApplyTo(source) ?? source;
        // An imported image/PDF still follows its print options even when annotations
        // are present. A positioned composition owns its physical sheet geometry.
        if(page.UsesAutomaticContentLayout) return effective;
        var paper=page.GetSheet().Paper;
        return effective with { PaperSizeMm=paper.SizeMm,Landscape=paper.Landscape,Notebook=paper.Notebook,
            Paper=paper.Notebook!=null || paper.SizeMm==new SizeD(165,205) ? PaperPreset.Notebook : paper.SizeMm==new SizeD(210,297) ? PaperPreset.A4 : paper.SizeMm==new SizeD(148,210) ? PaperPreset.A5 : PaperPreset.Custom };
    }
    public static void ValidateQueue(IReadOnlyList<PageSnapshot> pages,PrintSettingsSnapshot settings)
    {
        if(pages.Count==0) throw new ArgumentException("Нет выбранных страниц.");
        if(pages.Any(p=>p.Copies is <1 or >999) || pages.Sum(p=>(long)p.Copies)>100000)
            throw new ArgumentException("Печать поддерживает до 100 000 оттисков с 1–999 копиями страницы.");
        ValidateSettings(settings);
        for(var i=0;i<pages.Count;i++)
        {
            try
            {
                if(pages[i].GetSheet().Objects.OfType<MarkdownObject>().Any(item=>item.Document?.LayoutEngineVersion!=MarkdownDocument.CurrentLayoutEngineVersion))
                    throw new ArgumentException(MarkdownDocument.LayoutUpgradeMessage);
                pages[i].PrintOptions?.Validate();
                ValidateSettings(ForPage(pages[i],settings));
            }
            catch(ArgumentException error) { throw new ArgumentException($"Страница {i+1}: {error.Message}",error); }
        }
    }
    public static void ValidateSettings(PrintSettingsSnapshot settings)
    {
        if(string.IsNullOrWhiteSpace(settings.PrinterName)) throw new ArgumentException("Выберите принтер.");
        if(!Enum.IsDefined(settings.Paper) || !Enum.IsDefined(settings.Scaling) || !Enum.IsDefined(settings.Monochrome))
            throw new ArgumentException("Неизвестный режим печати.");
        var size=settings.PaperSizeMm;
        if(!double.IsFinite(size.Width) || !double.IsFinite(size.Height) || size.Width<25 || size.Height<25 || size.Width>1000 || size.Height>1000)
            throw new ArgumentException("Размер бумаги должен быть от 25 до 1000 мм.");
        if(settings.PrinterName.Contains("P1102",StringComparison.OrdinalIgnoreCase) && (size.Width<76 || size.Width>216 || size.Height<127 || size.Height>356))
            throw new ArgumentException("P1102 поддерживает бумагу шириной 76–216 мм и длиной 127–356 мм.");
        if(settings.Dpi is not (300 or 600) || settings.Threshold is <0 or >255)
            throw new ArgumentException("Выберите 300 или 600 DPI и порог от 0 до 255.");
        if(!double.IsFinite(settings.SafeMarginMm) || settings.SafeMarginMm<0 || settings.SafeMarginMm>=Math.Min(size.Width,size.Height)/2)
            throw new ArgumentException("Безопасные поля не оставляют печатной области.");
        if(!double.IsFinite(settings.PrintOffsetXmm) || !double.IsFinite(settings.PrintOffsetYmm) || Math.Abs(settings.PrintOffsetXmm)>30 || Math.Abs(settings.PrintOffsetYmm)>30)
            throw new ArgumentException("Поправка печати должна быть от −30 до 30 мм.");
        if(settings.PageRotationDegrees is not (0 or 180)) throw new ArgumentException("Недопустимый поворот печати.");
        settings.Notebook?.Validate();
    }
    public static bool SameStock(PrintSettingsSnapshot a,PrintSettingsSnapshot b)=>a.PaperSizeMm==b.PaperSizeMm && a.Landscape==b.Landscape
        && a.Notebook?.Variant==b.Notebook?.Variant;
    public static string GuideKey(PrintSettingsSnapshot s)=>$"{s.PrinterName.ToUpperInvariant()}|{s.Paper}|{s.Notebook?.Variant}|{s.Notebook?.Id}|{s.Notebook?.SingleMarginOnRight}|{s.PaperSizeMm.Width:R}|{s.PaperSizeMm.Height:R}|{s.Landscape}";
    public static IReadOnlyList<IReadOnlyList<DuplexSheet>> StockBatches(ManualDuplexPlan plan)
    {
        var result=new List<IReadOnlyList<DuplexSheet>>(); var current=new List<DuplexSheet>();
        foreach(var sheet in plan.Sheets)
        {
            if(current.Count!=0 && !SameStock(current[0].Front.Settings,sheet.Front.Settings))
            { result.Add(current.ToArray()); current.Clear(); }
            current.Add(sheet);
        }
        if(current.Count!=0) result.Add(current.ToArray()); return result;
    }
    public static IReadOnlyList<PrintPacket> FrontPass(IReadOnlyList<DuplexSheet> sheets)=>Packets(sheets.Select(s=>s.Front));
    public static IReadOnlyList<PrintPacket> BackPass(IReadOnlyList<DuplexSheet> sheets,bool faceDown,int rotation)
        =>sheets.All(s=>s.Back.IsBlank) ? [] : Packets((faceDown ? sheets.Reverse() : sheets).Select(s=>s.Back)
            .Select(s=>s with { Settings=s.Settings with { PageRotationDegrees=rotation } }));
    private static PreparedPrintSide Blank(PrintSettingsSnapshot settings)
    {
        var paper=new SheetPaper(settings.PaperSizeMm,settings.Landscape,SheetMargins.SafeDefault) { Notebook=settings.Notebook };
        return new(PrintPageItem.CreateBlank(paper).Snapshot(),settings,null,true);
    }
    private static IReadOnlyList<PrintPacket> Packets(IEnumerable<PreparedPrintSide> sides)
    {
        var result=new List<PrintPacket>(); var batch=new List<PageSnapshot>(); PrintSettingsSnapshot? current=null;
        foreach(var side in sides)
        {
            if(current!=null && current!=side.Settings)
            { result.Add(new(batch.ToArray(),current)); batch.Clear(); }
            current=side.Settings; batch.Add(side.Page);
        }
        if(current!=null && batch.Count!=0) result.Add(new(batch.ToArray(),current));
        return result;
    }
}

public static class PrintViewport
{
    public static RectD SourceArea(PrintSettingsSnapshot settings,RectD safe)
        =>settings.PageRotationDegrees==180
            ? new(settings.OrientedPaperMm.Width-safe.Right+settings.PrintOffsetXmm,settings.OrientedPaperMm.Height-safe.Bottom+settings.PrintOffsetYmm,safe.Width,safe.Height)
            : safe with { X=safe.X-settings.PrintOffsetXmm,Y=safe.Y-settings.PrintOffsetYmm };
}
