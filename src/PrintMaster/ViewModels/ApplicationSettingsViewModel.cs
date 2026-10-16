using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrintMaster.Models;
using PrintMaster.Services;

namespace PrintMaster.ViewModels;

public partial class ApplicationSettingsViewModel : ObservableObject
{
    public ApplicationSettingsViewModel(ApplicationPreferences preferences,IReadOnlyList<string> printers,IUserDialogs dialogs)
    {
        _original=preferences;
        _dialogs=dialogs; Printers=printers;
        PrinterName=preferences.PrinterName; Dpi=preferences.Dpi; Monochrome=preferences.Monochrome; Threshold=preferences.Threshold;
        AutosaveEnabled=preferences.AutosaveEnabled; AutosaveDelaySeconds=preferences.AutosaveDelaySeconds;
        LibreOfficePath=preferences.LibreOfficePath ?? "";
    }
    private readonly IUserDialogs _dialogs;
    private readonly ApplicationPreferences _original;
    private bool _resetPaperGuides;
    public IReadOnlyList<string> Printers { get; }
    public IReadOnlyList<Choice<string>> PrinterChoices=>new[] { new Choice<string>("","Автовыбор · HP P1102 или PDF") }.Concat(Printers.Select(p=>new Choice<string>(p,p))).ToArray();
    public IReadOnlyList<int> DpiChoices { get; }=[300,600];
    public IReadOnlyList<Choice<MonochromeMode>> MonoChoices { get; }=[new(MonochromeMode.Auto,"Авто"),new(MonochromeMode.Text,"Текст · порог"),new(MonochromeMode.Photo,"Фото · полутона")];
    [ObservableProperty] private string printerName="";
    [ObservableProperty] private int dpi;
    [ObservableProperty] private MonochromeMode monochrome;
    [ObservableProperty] private int threshold;
    [ObservableProperty] private bool autosaveEnabled;
    [ObservableProperty] private int autosaveDelaySeconds;
    [ObservableProperty] private string libreOfficePath="";
    [ObservableProperty] private string error="";
    public ApplicationPreferences? Result { get; private set; }
    public bool TryAccept()
    {
        var value=_original with { PrinterName=PrinterName,Dpi=Dpi,Monochrome=Monochrome,Threshold=Threshold,
            AutosaveEnabled=AutosaveEnabled,AutosaveDelaySeconds=AutosaveDelaySeconds,LibreOfficePath=string.IsNullOrWhiteSpace(LibreOfficePath) ? null : LibreOfficePath.Trim(),
            HiddenPaperGuides=_resetPaperGuides ? [] : _original.HiddenPaperGuides };
        try { value.Validate(); Result=value; Error=""; return true; }
        catch(ArgumentException ex) { Error=ex.Message; Result=null; return false; }
    }
    [RelayCommand] private void PickLibreOffice() { var path=_dialogs.PickLibreOffice(); if(path!=null) LibreOfficePath=path; }
    [RelayCommand] private void ResetPaperGuides() { _resetPaperGuides=true; Error="Подсказки загрузки снова появятся после сохранения настроек."; }
}
