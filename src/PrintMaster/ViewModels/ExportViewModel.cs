using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrintMaster.Models;
using PrintMaster.Services;
using System.Collections.Immutable;
using System.IO;
using System.Windows.Media.Imaging;

namespace PrintMaster.ViewModels;

public sealed partial class ExportViewModel : ObservableObject,IDisposable
{
    private readonly IReadOnlyList<ExportCandidate> _candidates;
    private readonly SheetPaper _automaticPaper;
    private readonly PrinterProfile? _profile;
    private readonly IUserDialogs _dialogs;
    private readonly SheetExportRenderer _renderer=new(new(new()));
    private readonly string _suggestedStem;
    private readonly SemaphoreSlim _previewGate=new(1,1);
    private CancellationTokenSource? _preview;
    private bool _disposed;
    private ImmutableArray<ExportPage> _pages=[];
    public ExportViewModel(IReadOnlyList<ExportCandidate> candidates,SheetPaper automaticPaper,PrinterProfile? profile,
        MonochromeMode mono,int threshold,ScaleMode scaling,IUserDialogs dialogs,string suggestedStem)
    {
        _candidates=candidates; _automaticPaper=automaticPaper; _profile=profile; _dialogs=dialogs; _suggestedStem=suggestedStem;
        monochrome=mono; this.threshold=threshold; this.scaling=scaling;
        scope=candidates.Any(p=>p.Current) ? ExportScope.Current : ExportScope.All;
        Rebuild();
    }
    public IReadOnlyList<Choice<ExportScope>> Scopes=>[
        new(ExportScope.Current,"Текущая страница"),new(ExportScope.Highlighted,$"Выделенные в очереди · {_candidates.Count(p=>p.Highlighted)}"),
        new(ExportScope.Included,$"Отмеченные к печати · {_candidates.Count(p=>p.Included)}"),new(ExportScope.All,$"Все страницы · {_candidates.Count}")];
    public IReadOnlyList<Choice<ExportFormat>> Formats { get; }=[new(ExportFormat.Png,"PNG · отдельный файл на лист"),new(ExportFormat.Pdf,"PDF · все листы в одном файле")];
    public IReadOnlyList<Choice<ExportAppearance>> Appearances { get; }=[new(ExportAppearance.Color,"Цветной макет"),new(ExportAppearance.Monochrome,"Монохромный печатный вид")];
    public IReadOnlyList<Choice<MonochromeMode>> MonoChoices { get; }=[new(MonochromeMode.Auto,"Авто · по содержимому"),new(MonochromeMode.Text,"Текст · порог"),new(MonochromeMode.Photo,"Фото · дизеринг")];
    public IReadOnlyList<int> DpiChoices { get; }=[300,600];
    [ObservableProperty] private ExportScope scope;
    [ObservableProperty] private ExportFormat format=ExportFormat.Png;
    [ObservableProperty] private ExportAppearance appearance;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ScalingHint))] private ScaleMode scaling=ScaleMode.Fit;
    [ObservableProperty] private MonochromeMode monochrome;
    [ObservableProperty] private int threshold=160;
    [ObservableProperty] private int dpi=300;
    [ObservableProperty] private string outputPath="";
    [ObservableProperty] private string error="";
    [ObservableProperty] private string warning="";
    [ObservableProperty] private bool acknowledgeLoss;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private BitmapSource? preview;
    [ObservableProperty] private int previewIndex;
    public bool HasLoss=>Warning.Length>0;
    public bool IsMonochrome=>Appearance==ExportAppearance.Monochrome;
    public bool ShowThreshold=>IsMonochrome && Monochrome!=MonochromeMode.Photo;
    public bool HasAutomaticPages=>_pages.Any(p=>p.Page.UsesAutomaticContentLayout);
    public IReadOnlyList<Choice<ScaleMode>> ScalingChoices { get; }=[new(ScaleMode.Fit,"Вписать"),new(ScaleMode.Fill,"Заполнить"),new(ScaleMode.Stretch,"Растянуть")];
    public string ScalingHint=>Scaling==ScaleMode.Stretch ? "Всё изображение видно, пропорции изменяются. Круг может стать овалом. Поля принтера сохраняются." : "";
    public string Summary=>$"{RussianQuantity.Format(_pages.Length,"страница","страницы","страниц")} · {Dpi} DPI. Печатные копии не размножают экспорт.";
    public string FormatHint=>Format==ExportFormat.Pdf ? "Один PDF с собственным размером каждого листа. Страницы сохраняются изображениями: текст в PDF не будет поисковым."
        : _pages.Length>1 ? "Выберите имя первого файла. Страницы получат окончания _001, _002…" : "Один PNG с физическим размером и DPI листа.";
    public string AppearanceHint=>IsMonochrome ? "Монохромная обработка и безопасные поля. Поправки подачи и переворот бумаги применяются при печати."
        : "Содержимое составного листа до края бумаги. Разлиновка и сгиб тетради не экспортируются.";
    public string PreviewTitle=>_pages.Length==0 ? "Нет страниц" : $"Страница {PreviewIndex+1}/{_pages.Length} · {_pages[PreviewIndex].Paper.OrientedSizeMm.Width:g} × {_pages[PreviewIndex].Paper.OrientedSizeMm.Height:g} мм";
    public string Outputs { get; private set; }="Выберите путь экспорта.";
    public bool CanAccept=>Error.Length==0 && _pages.Length>0 && OutputPath.Length>0 && !IsLoading && (!HasLoss || AcknowledgeLoss);
    public string SubmitAvailability=>IsLoading ? "Подождите загрузки предпросмотра." : Error.Length>0 ? Error : _pages.Length==0 ? "Выберите страницы для экспорта." :
        OutputPath.Length==0 ? "Выберите путь сохранения." : HasLoss && !AcknowledgeLoss ? "Подтвердите экспорт видимой части или исправьте макет." : "Сохранить выбранные листы.";
    public ExportPlan? AcceptedPlan { get; private set; }
    public bool OverwriteConfirmed { get; private set; }
    public ImmutableArray<ExportPage> Pages=>_pages;
    private ExportOptions Options=>new(Format,Appearance,Dpi,Scaling,Monochrome,Threshold,AcknowledgeLoss,AcknowledgeLoss);
    partial void OnScopeChanged(ExportScope value) { PreviewIndex=0; AcknowledgeLoss=false; Rebuild(); }
    partial void OnFormatChanged(ExportFormat value)
    { if(OutputPath.Length>0) OutputPath=Path.ChangeExtension(OutputPath,value==ExportFormat.Pdf ? ".pdf" : ".png"); Rebuild(false); }
    partial void OnAppearanceChanged(ExportAppearance value) { AcknowledgeLoss=false; Rebuild(); }
    partial void OnScalingChanged(ScaleMode value) { AcknowledgeLoss=false; Rebuild(); }
    partial void OnMonochromeChanged(MonochromeMode value)=>Rebuild();
    partial void OnThresholdChanged(int value)=>Rebuild();
    partial void OnDpiChanged(int value)=>Rebuild();
    partial void OnOutputPathChanged(string value)=>Rebuild(false);
    partial void OnAcknowledgeLossChanged(bool value) { OnPropertyChanged(nameof(CanAccept)); OnPropertyChanged(nameof(SubmitAvailability)); }
    partial void OnIsLoadingChanged(bool value) { OnPropertyChanged(nameof(CanAccept)); OnPropertyChanged(nameof(SubmitAvailability)); }
    partial void OnErrorChanged(string value)=>OnPropertyChanged(nameof(SubmitAvailability));
    partial void OnPreviewIndexChanged(int value)
    { var valid=Math.Clamp(value,0,Math.Max(0,_pages.Length-1)); if(value!=valid) { PreviewIndex=valid; return; } OnPropertyChanged(nameof(PreviewTitle)); _=RefreshPreviewAsync(); }
    [RelayCommand] private void PreviousPage() { if(PreviewIndex>0) PreviewIndex--; }
    [RelayCommand] private void NextPage() { if(PreviewIndex+1<_pages.Length) PreviewIndex++; }
    [RelayCommand] private void BrowseOutput()
    {
        var name=_suggestedStem+(Format==ExportFormat.Pdf ? ".pdf" : ".png");
        var path=_dialogs.PickExportOutput(Format,OutputPath.Length==0 ? name : OutputPath);
        if(path!=null) OutputPath=path;
    }
    private void Rebuild(bool refreshPreview=true)
    {
        if(_disposed) return;
        Error=""; Warning=""; AcceptedPlan=null; OverwriteConfirmed=false;
        try
        {
            _pages=_candidates.Where(p=>Scope switch { ExportScope.Current=>p.Current,ExportScope.Highlighted=>p.Highlighted,ExportScope.Included=>p.Included,_=>true })
                .Select(p=>SheetExportRenderer.Prepare(p.Page,_automaticPaper,_profile)).ToImmutableArray();
            // Validation without user acknowledgements is a warning; all other failures remain errors.
            SheetExportRenderer.ValidatePages(_pages,Options with { AllowClipping=true,AllowOverflow=true });
            var clipped=_pages.Count(p=>SheetExportRenderer.HasClipping(p,Options)); var overflowing=_pages.Count(p=>SheetExportRenderer.HasOverflow(p.Page));
            var warnings=new List<string>();
            if(clipped>0) warnings.Add($"На {RussianQuantity.Format(clipped,"странице","страницах","страницах")} содержимое выходит за область экспорта или обрезается режимом «Заполнить».");
            if(overflowing>0) warnings.Add($"На {RussianQuantity.Format(overflowing,"странице","страницах","страницах")} текст не помещается. В файлы попадет только видимая часть; полный источник останется в проекте.");
            Warning=string.Join("\n",warnings);
            if(OutputPath.Length>0)
            {
                var plan=ExportFilePlan.Build(_pages,Options,OutputPath);
                Outputs=plan.Files.Length==1 ? plan.Files[0].Path : $"{RussianQuantity.Format(plan.Files.Length,"файл","файла","файлов")} в {Path.GetDirectoryName(plan.Files[0].Path)}\n"+
                    string.Join("\n",plan.Files.Take(3).Select(f=>Path.GetFileName(f.Path)))+(plan.Files.Length>3 ? "\n…" : "");
                if(plan.ExistingFiles.Count>0) Outputs+=$"\nУже существуют: {plan.ExistingFiles.Count}. Замена потребует подтверждения.";
            }
            else Outputs="Выберите путь экспорта.";
        }
        catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { Error=ex.Message; }
        PreviewIndex=Math.Clamp(PreviewIndex,0,Math.Max(0,_pages.Length-1));
        foreach(var name in new[] { nameof(Summary),nameof(FormatHint),nameof(AppearanceHint),nameof(PreviewTitle),nameof(Outputs),nameof(HasLoss),nameof(IsMonochrome),nameof(ShowThreshold),nameof(HasAutomaticPages),nameof(CanAccept) }) OnPropertyChanged(name);
        if(refreshPreview) _=RefreshPreviewAsync();
    }
    public bool TryAccept()
    {
        Rebuild(false); if(!CanAccept) return false;
        try
        {
            var plan=ExportFilePlan.Build(_pages,Options,OutputPath);
            if(plan.ExistingFiles.Count>0 && !_dialogs.ConfirmExportOverwrite(plan.ExistingFiles)) return false;
            OverwriteConfirmed=plan.ExistingFiles.Count>0; AcceptedPlan=plan; return true;
        }
        catch(Exception ex) when(ex is ArgumentException or IOException or UnauthorizedAccessException)
        { Error=ex.Message; OnPropertyChanged(nameof(CanAccept)); return false; }
    }
    public async Task RefreshPreviewAsync()
    {
        if(_disposed) return;
        _preview?.Cancel(); _preview?.Dispose(); _preview=new(); var token=_preview.Token;
        if(_pages.Length==0 || Error.Length>0) { Preview=null; IsLoading=false; return; }
        var page=_pages[PreviewIndex]; var options=Options; IsLoading=true;
        try
        {
            await Task.Delay(100,token); await _previewGate.WaitAsync(token);
            try
            {
                var image=await Task.Run(()=>{ using var bitmap=_renderer.Render(page,options,token,1200); return BitmapBridge.ToWpf(bitmap); },token);
                if(!token.IsCancellationRequested) Preview=image;
            }
            finally { _previewGate.Release(); }
        }
        catch(OperationCanceledException) { }
        catch(Exception ex) { if(!token.IsCancellationRequested) { Error="Не удалось подготовить просмотр: "+ex.Message; Preview=null; OnPropertyChanged(nameof(CanAccept)); } }
        finally { if(!token.IsCancellationRequested) IsLoading=false; }
    }
    public void Dispose() { if(_disposed) return; _disposed=true; _preview?.Cancel(); _preview?.Dispose(); }
}
