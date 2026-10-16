namespace PrintMaster.Models;

public sealed record ApplicationPreferences
{
    public int Version { get; init; }=1;
    public string PrinterName { get; init; }="";
    public int Dpi { get; init; }=600;
    public MonochromeMode Monochrome { get; init; }=MonochromeMode.Auto;
    public int Threshold { get; init; }=160;
    public bool AutosaveEnabled { get; init; }=true;
    public int AutosaveDelaySeconds { get; init; }=2;
    public string? LibreOfficePath { get; init; }
    public System.Collections.Immutable.ImmutableArray<NotebookProfile> NotebookProfiles { get; init; }=Models.NotebookProfiles.Defaults;
    public System.Collections.Immutable.ImmutableArray<PrinterPaperCalibration> PaperCalibrations { get; init; }=[];
    public System.Collections.Immutable.ImmutableArray<string> HiddenPaperGuides { get; init; }=[];
    public bool Equals(ApplicationPreferences? other)=>ReferenceEquals(this,other) || other!=null && Version==other.Version &&
        PrinterName==other.PrinterName && Dpi==other.Dpi && Monochrome==other.Monochrome && Threshold==other.Threshold &&
        AutosaveEnabled==other.AutosaveEnabled && AutosaveDelaySeconds==other.AutosaveDelaySeconds && LibreOfficePath==other.LibreOfficePath &&
        Same(NotebookProfiles,other.NotebookProfiles) && Same(PaperCalibrations,other.PaperCalibrations) && Same(HiddenPaperGuides,other.HiddenPaperGuides);
    private static bool Same<T>(System.Collections.Immutable.ImmutableArray<T> a,System.Collections.Immutable.ImmutableArray<T> b)
        =>a.IsDefault || b.IsDefault ? a.IsDefault==b.IsDefault : a.SequenceEqual(b);
    public override int GetHashCode()
    {
        var hash=new HashCode(); hash.Add(Version); hash.Add(PrinterName); hash.Add(Dpi); hash.Add(Monochrome); hash.Add(Threshold);
        hash.Add(AutosaveEnabled); hash.Add(AutosaveDelaySeconds); hash.Add(LibreOfficePath);
        if(!NotebookProfiles.IsDefault) foreach(var p in NotebookProfiles) hash.Add(p);
        if(!PaperCalibrations.IsDefault) foreach(var p in PaperCalibrations) hash.Add(p);
        if(!HiddenPaperGuides.IsDefault) foreach(var p in HiddenPaperGuides) hash.Add(p);
        return hash.ToHashCode();
    }

    public void Validate()
    {
        if(NotebookProfiles.IsDefault || PaperCalibrations.IsDefault || HiddenPaperGuides.IsDefault || NotebookProfiles.Length>200 || PaperCalibrations.Length>500 || HiddenPaperGuides.Length>1000)
            throw new ArgumentException("Недопустимый список профилей бумаги.");
        foreach(var profile in NotebookProfiles) profile.Validate();
        foreach(var calibration in PaperCalibrations) calibration.Validate();
        if(NotebookProfiles.Select(p=>p.Id).Distinct().Count()!=NotebookProfiles.Length) throw new ArgumentException("Повторяющиеся профили тетради.");
        if(Version!=1 || Dpi is not (300 or 600) || !Enum.IsDefined(Monochrome) || Threshold is <0 or >255 ||
           AutosaveDelaySeconds is <1 or >60 || PrinterName==null)
            throw new ArgumentException("Некорректные настройки приложения.");
        if(!string.IsNullOrWhiteSpace(LibreOfficePath) &&
           (!System.IO.File.Exists(LibreOfficePath) || System.IO.Path.GetFileName(LibreOfficePath).ToLowerInvariant() is not ("soffice.com" or "soffice.exe")))
            throw new ArgumentException("Укажите существующий soffice.com или soffice.exe из папки LibreOffice.");
    }
}
