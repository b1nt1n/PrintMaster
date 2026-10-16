using CommunityToolkit.Mvvm.ComponentModel;
using PrintMaster.Models;
using System.Globalization;

namespace PrintMaster.ViewModels;

public partial class NewSheetViewModel : ObservableObject
{
    private readonly bool _isHp;
    private readonly List<NotebookProfile> _profiles;
    private bool _selectingNotebook;
    public NewSheetViewModel(PrintSettings settings)
    {
        _isHp=settings.PrinterName.Contains("P1102",StringComparison.OrdinalIgnoreCase);
        _profiles=settings.AvailableNotebookProfiles.ToList();
        if(_profiles.Count==0) _profiles.AddRange(NotebookProfiles.Defaults);
        SelectedNotebookProfile=settings.Notebook ?? _profiles.First();
        Paper=settings.Paper; Landscape=Paper==PaperPreset.Notebook ? SelectedNotebookProfile.IsSpread : settings.Landscape;
        WidthText=settings.CustomWidthMm.ToString(CultureInfo.CurrentCulture); HeightText=settings.CustomHeightMm.ToString(CultureInfo.CurrentCulture);
        Validate();
    }
    public IReadOnlyList<Choice<PaperPreset>> PaperChoices { get; }=[new(PaperPreset.A4,"A4 · 210 × 297 мм"),new(PaperPreset.A5,"A5 · 148 × 210 мм"),new(PaperPreset.Notebook,"Тетрадный лист"),new(PaperPreset.Custom,"Пользовательский")];
    public IReadOnlyList<Choice<NotebookVariant>> NotebookChoices=>NotebookProfiles.Choices;
    public IReadOnlyList<NotebookProfile> NotebookProfileChoices=>_profiles.Where(p=>p.Variant==NotebookVariant).ToArray();
    [ObservableProperty] private NotebookVariant notebookVariant;
    private NotebookProfile? selectedNotebookProfile;
    public NotebookProfile? SelectedNotebookProfile
    {
        get=>selectedNotebookProfile;
        set
        {
            // A ComboBox clears SelectedItem while its ItemsSource changes.
            // That transient target state must not erase the chosen paper profile.
            if(value==null || value==selectedNotebookProfile) return;
            selectedNotebookProfile=value; OnSelectedNotebookProfileChanged(value);
            OnPropertyChanged(nameof(SelectedNotebookProfile));
        }
    }
    public IReadOnlyList<Choice<bool>> Orientations { get; }=[new(false,"Портрет"),new(true,"Альбом")];
    [ObservableProperty] private PaperPreset paper;
    [ObservableProperty] private bool landscape;
    [ObservableProperty] private string widthText="165";
    [ObservableProperty] private string heightText="205";
    public bool IsCustom=>Paper==PaperPreset.Custom;
    public bool IsNotebook=>Paper==PaperPreset.Notebook;
    public double WidthMinimum=>_isHp ? 76 : 25;
    public double WidthMaximum=>_isHp ? 216 : 1000;
    public double HeightMinimum=>_isHp ? 127 : 25;
    public double HeightMaximum=>_isHp ? 356 : 1000;
    public string? Error { get; private set; }
    public bool CanCreate=>Error==null;
    public string Summary { get; private set; }="";
    public string Limits=>_isHp ? "P1102: ширина подачи 76–216 мм, длина 127–356 мм. Альбомная ориентация поворачивает макет, а не бумагу в лотке." : "Пользовательский размер: 25–1000 мм. Допустимые размеры для печати определяет выбранный драйвер.";
    partial void OnPaperChanged(PaperPreset value)
    { if(value==PaperPreset.Notebook && SelectedNotebookProfile is {} notebook) Landscape=notebook.IsSpread; Validate(); }
    partial void OnLandscapeChanged(bool value)=>Validate();
    partial void OnWidthTextChanged(string value)=>Validate();
    partial void OnHeightTextChanged(string value)=>Validate();
    partial void OnNotebookVariantChanged(NotebookVariant value)
    {
        if(_selectingNotebook) return;
        SetNotebookProfile(_profiles.FirstOrDefault(p=>p.Variant==value) ?? NotebookProfiles.Default(value));
    }
    private void OnSelectedNotebookProfileChanged(NotebookProfile? value)
    {
        if(value==null) return;
        _selectingNotebook=true;
        try { NotebookVariant=value.Variant; if(Paper==PaperPreset.Notebook) Landscape=value.IsSpread; }
        finally { _selectingNotebook=false; }
        OnPropertyChanged(nameof(NotebookProfileChoices)); Validate();
    }
    public void SetNotebookProfile(NotebookProfile profile)
    {
        _profiles.RemoveAll(p=>p.Id==profile.Id); _profiles.Add(profile);
        SelectedNotebookProfile=profile; OnPropertyChanged(nameof(NotebookProfileChoices)); Validate();
    }
    public SheetPaper? Result { get; private set; }
    private void Validate()
    {
        Result=null; Error=null;
        var size=Paper switch { PaperPreset.A4=>new SizeD(210,297),PaperPreset.A5=>new(148,210),PaperPreset.Notebook=>SelectedNotebookProfile?.FeedSize ?? new(167,208),_=>new(Parse(WidthText),Parse(HeightText)) };
        if(!double.IsFinite(size.Width) || !double.IsFinite(size.Height) || size.Width<25 || size.Width>1000 || size.Height<25 || size.Height>1000) Error="Введите ширину и высоту от 25 до 1000 мм.";
        else if(_isHp && (size.Width<76 || size.Width>216 || size.Height<127 || size.Height>356)) Error="Этот размер не поддерживается P1102. Ширина: 76–216 мм, длина: 127–356 мм.";
        else Result=Paper==PaperPreset.Notebook && SelectedNotebookProfile is {} notebook ? notebook.ToPaper(landscape:Landscape) : new(size,Landscape,SheetMargins.SafeDefault);
        Summary=Result is {} r ? $"Лист {r.OrientedSizeMm.Width:g} × {r.OrientedSizeMm.Height:g} мм. Безопасные поля — минимум 5 мм; точные ограничения уточняются по драйверу." : "";
        foreach(var name in new[] { nameof(IsCustom),nameof(IsNotebook),nameof(Error),nameof(CanCreate),nameof(Summary) }) OnPropertyChanged(name);
    }
    private static double Parse(string value)=>double.TryParse(value.Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out var result) ? result : double.NaN;
}
