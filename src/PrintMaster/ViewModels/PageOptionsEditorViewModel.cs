using CommunityToolkit.Mvvm.ComponentModel;
using PrintMaster.Models;
using PrintMaster.Services;
using System.ComponentModel;

namespace PrintMaster.ViewModels;

public partial class PageOptionsEditorViewModel : ObservableObject
{
    public PageOptionsEditorViewModel(IReadOnlyList<PageSnapshot> pages,PrintSettingsSnapshot defaults,IReadOnlyList<NotebookProfile> profiles)
    {
        if(pages.Count==0)throw new ArgumentException("Выберите страницы.");
        Count=pages.Count; CanChangePaper=pages.All(p=>p.UsesAutomaticContentLayout);
        Settings.PrinterName=defaults.PrinterName; Settings.AvailableNotebookProfiles=profiles;
        var initial=ManualDuplexPlanner.ForPage(pages[0],defaults);
        Settings.Paper=initial.Paper;Settings.CustomWidthMm=initial.PaperSizeMm.Width;Settings.CustomHeightMm=initial.PaperSizeMm.Height;
        Settings.Landscape=initial.Landscape;Settings.Notebook=initial.Notebook;Settings.Scaling=initial.Scaling;
        Settings.Monochrome=initial.Monochrome;Settings.Dpi=initial.Dpi;Settings.Threshold=initial.Threshold;
        useOwnSettings=pages.Any(p=>p.PrintOptions!=null);
        Settings.PropertyChanged+=Changed;Validate();
    }
    public PrintSettings Settings {get;}=new();
    public int Count {get;}
    public bool CanChangePaper {get;}
    public bool IsCustomPaper=>Settings.IsCustom;
    public bool IsNotebookPaper=>Settings.IsNotebook;
    public bool ShowThreshold=>Settings.Monochrome!=MonochromeMode.Photo;
    public string Summary=>Count==1 ? "Параметры выбранной страницы" : $"Одна настройка для {RussianQuantity.Format(Count,"выделенной страницы","выделенных страниц","выделенных страниц")}";
    public string LayoutHint=>CanChangePaper ? "Формат и размещение относятся только к выбранным страницам."
        : "Размер готового макета задаётся самим листом. Для группы с макетами сохраняются прежние формат и масштабирование каждой страницы.";
    public bool CanApply=>!UseOwnSettings || Error.Length==0;
    public string Error {get;private set;}="";
    [ObservableProperty] private bool useOwnSettings;
    partial void OnUseOwnSettingsChanged(bool value)=>Validate();
    public IReadOnlyList<Choice<PaperPreset>> PaperChoices {get;}=[new(PaperPreset.A4,"A4 · 210 × 297 мм"),new(PaperPreset.A5,"A5 · 148 × 210 мм"),new(PaperPreset.Notebook,"Тетрадный лист"),new(PaperPreset.Custom,"Пользовательский")];
    public IReadOnlyList<Choice<bool>> OrientationChoices {get;}=[new(false,"Портрет"),new(true,"Альбом")];
    public IReadOnlyList<Choice<ScaleMode>> ScaleChoices {get;}=[new(ScaleMode.Fit,"Вписать"),new(ScaleMode.Fill,"Заполнить"),new(ScaleMode.Stretch,"Растянуть")];
    public IReadOnlyList<Choice<MonochromeMode>> MonoChoices {get;}=[new(MonochromeMode.Auto,"Авто · по содержимому"),new(MonochromeMode.Text,"Текст · резкий"),new(MonochromeMode.Photo,"Фото · дизеринг")];
    public IReadOnlyList<int> DpiChoices {get;}=[300,600];
    public IReadOnlyList<NotebookProfile> NotebookChoices=>Settings.AvailableNotebookProfiles;
    private bool IsHp=>Settings.PrinterName.Contains("P1102",StringComparison.OrdinalIgnoreCase);
    public double WidthMinimum=>IsHp ? 76 : 25;
    public double WidthMaximum=>IsHp ? 216 : 1000;
    public double HeightMinimum=>IsHp ? 127 : 25;
    public double HeightMaximum=>IsHp ? 356 : 1000;
    private void Changed(object? sender,PropertyChangedEventArgs e)
    {
        if(Settings.IsNotebook && Settings.Notebook==null)Settings.Notebook=NotebookChoices.FirstOrDefault() ?? NotebookProfiles.Default(NotebookVariant.GridSingle);
        foreach(var name in new[]{nameof(IsCustomPaper),nameof(IsNotebookPaper),nameof(ShowThreshold)})OnPropertyChanged(name);
        Validate();
    }
    public PagePrintOptions? Result()
    {
        if(!UseOwnSettings)return null;
        if(Settings.Threshold is <0 or >255)throw new ArgumentException("Порог чёрного должен быть от 0 до 255.");
        return PagePrintOptions.Capture(Settings.Snapshot());
    }
    public void Validate()
    {
        Error="";
        if(UseOwnSettings)try{Result()?.Validate();}catch(ArgumentException ex){Error=ex.Message;}
        OnPropertyChanged(nameof(Error));OnPropertyChanged(nameof(CanApply));
    }
}
