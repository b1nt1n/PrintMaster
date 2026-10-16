namespace PrintMaster.Models;

/// <summary>
/// A page may freeze its paper and raster options while retaining the session's
/// printer, duplex workflow, calibration and print consent.
/// </summary>
public sealed record PagePrintOptions(PaperPreset Paper,SizeD PaperSizeMm,bool Landscape,
    ScaleMode Scaling,MonochromeMode Monochrome,int Dpi,int Threshold,NotebookProfile? Notebook=null)
{
    public static PagePrintOptions Capture(PrintSettingsSnapshot settings)=>new(settings.Paper,settings.PaperSizeMm,
        settings.Landscape,settings.Scaling,settings.Monochrome,settings.Dpi,settings.Threshold,settings.Notebook);

    public PrintSettingsSnapshot ApplyTo(PrintSettingsSnapshot settings)
    {
        Validate();
        return settings with { Paper=Paper,PaperSizeMm=PaperSizeMm,Landscape=Landscape,
            Scaling=Scaling,Monochrome=Monochrome,Dpi=Dpi,Threshold=Threshold,Notebook=Notebook };
    }

    public void Validate()
    {
        if(!Enum.IsDefined(Paper) || !Enum.IsDefined(Scaling) || !Enum.IsDefined(Monochrome))
            throw new ArgumentException("Недопустимый формат бумаги или режим печати страницы.");
        if(!double.IsFinite(PaperSizeMm.Width) || !double.IsFinite(PaperSizeMm.Height)
            || PaperSizeMm.Width<25 || PaperSizeMm.Height<25 || PaperSizeMm.Width>1000 || PaperSizeMm.Height>1000)
            throw new ArgumentException("Размер бумаги страницы должен быть от 25 до 1000 мм.");
        if(Dpi is not (300 or 600)) throw new ArgumentException("Выберите 300 или 600 DPI для страницы.");
        if(Threshold is <0 or >255) throw new ArgumentException("Порог чёрного страницы должен быть от 0 до 255.");
        if(Notebook!=null)
        {
            Notebook.Validate();
            if(Paper!=PaperPreset.Notebook || PaperSizeMm!=Notebook.FeedSize)
                throw new ArgumentException("Профиль тетради должен соответствовать формату бумаги страницы.");
        }
        if(Paper==PaperPreset.A4 && PaperSizeMm!=new SizeD(210,297)
            || Paper==PaperPreset.A5 && PaperSizeMm!=new SizeD(148,210))
            throw new ArgumentException("Размер стандартного листа страницы не соответствует выбранному формату.");
    }
}
