using CommunityToolkit.Mvvm.ComponentModel;

namespace PrintMaster.Models;

public partial class PrintSettings : ObservableObject
{
    [ObservableProperty] private string printerName = "";
    [ObservableProperty] private PaperPreset paper = PaperPreset.A4;
    [ObservableProperty] private double customWidthMm = 165;
    [ObservableProperty] private double customHeightMm = 205;
    [ObservableProperty] private bool landscape;
    [ObservableProperty] private NotebookProfile? notebook;
    [ObservableProperty] private bool manualDuplex;
    [ObservableProperty] private double printOffsetXmm;
    [ObservableProperty] private double printOffsetYmm;
    public IReadOnlyList<NotebookProfile> AvailableNotebookProfiles { get; set; }=NotebookProfiles.Defaults;
    [ObservableProperty] private ScaleMode scaling = ScaleMode.Fit;
    [ObservableProperty] private MonochromeMode monochrome = MonochromeMode.Auto;
    [ObservableProperty] private int dpi = 600;
    [ObservableProperty] private int threshold = 160;
    public SizeD PaperSizeMm => Paper switch
    {
        PaperPreset.A4 => new(210,297), PaperPreset.A5 => new(148,210),
        PaperPreset.Notebook => Notebook?.FeedSize ?? new(165,205), _ => new(CustomWidthMm,CustomHeightMm)
    };
    public SizeD OrientedPaperMm => PaperSizeMm.Rotated(Landscape ? 1 : 0);
    public bool IsCustom => Paper == PaperPreset.Custom;
    public bool IsNotebook => Paper == PaperPreset.Notebook;
    public PrintSettingsSnapshot Snapshot(string? outputPath = null, bool allowCrop = false)
    {
        if (string.IsNullOrWhiteSpace(PrinterName)) throw new ArgumentException("Выберите принтер.");
        var size = PaperSizeMm;
        if (!double.IsFinite(size.Width) || !double.IsFinite(size.Height) || size.Width < 25 || size.Height < 25 || size.Width > 1000 || size.Height > 1000)
            throw new ArgumentException("Размер бумаги должен быть от 25 до 1000 мм.");
        if (PrinterName.Contains("P1102",StringComparison.OrdinalIgnoreCase) && (size.Width < 76 || size.Width > 216 || size.Height < 127 || size.Height > 356))
            throw new ArgumentException("P1102 поддерживает бумагу шириной 76–216 мм и длиной 127–356 мм. Размер задается для подачи в портретном положении.");
        if (Dpi is not (300 or 600)) throw new ArgumentException("Выберите 300 или 600 DPI.");
        if(IsNotebook) Notebook?.Validate();
        if(!double.IsFinite(PrintOffsetXmm) || !double.IsFinite(PrintOffsetYmm) || Math.Abs(PrintOffsetXmm)>30 || Math.Abs(PrintOffsetYmm)>30)
            throw new ArgumentException("Поправка печати должна быть от −30 до 30 мм.");
        return new(PrinterName,Paper,size,Landscape,Scaling,Monochrome,Dpi,Math.Clamp(Threshold,0,255),5,outputPath,allowCrop,
            IsNotebook ? Notebook : null,ManualDuplex,PrintOffsetXmm,PrintOffsetYmm);
    }
}

public sealed record PrintSettingsSnapshot(string PrinterName,PaperPreset Paper,SizeD PaperSizeMm,bool Landscape,
    ScaleMode Scaling,MonochromeMode Monochrome,int Dpi,int Threshold,double SafeMarginMm,string? OutputPath,bool AllowCropping,
    NotebookProfile? Notebook=null,bool ManualDuplex=false,double PrintOffsetXmm=0,double PrintOffsetYmm=0,bool AllowNotebookOverflow=false,int PageRotationDegrees=0)
{
    public SizeD OrientedPaperMm => PaperSizeMm.Rotated(Landscape ? 1 : 0);
}

public sealed record PrinterProfile(string Name,SizeD ActualPaperMm,RectD PrintableMm,bool IsVerified,string Message);
public sealed record PrintProgress(int Completed,int Total,string Message,PrintPhase Phase=PrintPhase.Rendering);
