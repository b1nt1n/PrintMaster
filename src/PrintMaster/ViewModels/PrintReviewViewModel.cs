using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrintMaster.Models;
using PrintMaster.Services;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;

namespace PrintMaster.ViewModels;

public partial class PrintReviewViewModel : ObservableObject,IDisposable
{
    private readonly PageSnapshot[] _pages;
    private readonly PageRasterizer _rasterizer=new(new());
    private CancellationTokenSource? _update;
    private readonly SemaphoreSlim _renderGate=new(1,1);
    private bool _disposed;
    private string? _driverError;
    private bool _choosingNotebook;
    private readonly IReadOnlyList<NotebookProfile> _notebookProfiles;
    private Dictionary<PrintSettingsSnapshot,PrinterProfile> _profiles=[];
    public PrintReviewViewModel(IEnumerable<PageSnapshot> pages,PrintSettings source,IReadOnlyList<string> printers)
    {
        _pages=pages.ToArray(); Printers=new(printers); _notebookProfiles=source.AvailableNotebookProfiles;
        ProjectPrintSettings.Capture(source).Apply(Settings);
        var paper=_pages.Length!=0 && _pages.All(p=>!p.UsesAutomaticContentLayout) ? _pages[0].GetSheet().Paper : null;
        if(paper!=null) SetPaper(Settings,paper);
        Settings.PropertyChanged+=Changed;
        Validate();
    }
    public PrintSettings Settings { get; }=new();
    public ObservableCollection<string> Printers { get; }
    public IReadOnlyList<Choice<PaperPreset>> PaperChoices { get; }=[new(PaperPreset.A4,"A4 · 210 × 297 мм"),new(PaperPreset.A5,"A5 · 148 × 210 мм"),new(PaperPreset.Notebook,"Тетрадный лист"),new(PaperPreset.Custom,"Пользовательский")];
    public IReadOnlyList<Choice<NotebookVariant>> NotebookChoices=>NotebookProfiles.Choices;
    public NotebookVariant? NotebookVariant
    {
        get=>Settings.Notebook?.Variant;
        set { if(value!=null && CanChangePaper && value!=NotebookVariant) { Settings.Notebook=_notebookProfiles.FirstOrDefault(p=>p.Variant==value) ?? NotebookProfiles.Default(value.Value); Settings.Landscape=Settings.Notebook.IsSpread; } }
    }
    public bool CanChangePaper=>_pages.All(p=>p.UsesAutomaticContentLayout);
    public bool HasMixedPaper
    {
        get
        {
            try
            {
                var source=Settings.Snapshot();
                var targets=_pages.Select(p=>ManualDuplexPlanner.ForPage(p,source)).ToArray();
                return targets.Skip(1).Any(s=>!ManualDuplexPlanner.SameStock(targets[0],s));
            }
            catch(ArgumentException) { return false; }
        }
    }
    public bool HasIndividualOptions=>_pages.Any(p=>p.PrintOptions!=null);
    public string PaperHint=>HasMixedPaper ? "Форматы заданы страницами. Печать пройдет частями с подтверждением смены бумаги." : !CanChangePaper ? "Размер и вид бумаги заданы макетом листа." :
        HasIndividualOptions ? "Индивидуальные настройки страниц имеют приоритет над общими." : "";
    public bool IsPhysicalPrinter=>!Settings.PrinterName.Contains("Microsoft Print to PDF",StringComparison.OrdinalIgnoreCase);
    public bool CanShowPaperGuide
    {
        get
        {
            if(!IsPhysicalPrinter || _pages.Length==0) return false;
            try { _=PreviewSettings(); return true; }
            catch(ArgumentException) { return false; }
        }
    }
    public PaperGuideViewModel? CreatePaperGuide()=>CanShowPaperGuide
        ? new(PreviewSettings(),_pages[PreviewIndex].Copies,PaperGuideStep.Preview,allowCalibration:false) : null;
    public IReadOnlyList<Choice<bool>> OrientationChoices { get; }=[new(false,"Портрет"),new(true,"Альбом")];
    public IReadOnlyList<Choice<ScaleMode>> ScaleChoices { get; }=[new(ScaleMode.Fit,"Вписать"),new(ScaleMode.Fill,"Заполнить"),new(ScaleMode.Stretch,"Растянуть")];
    public string ScalingHint=>Settings.Scaling==ScaleMode.Stretch ? "Всё изображение видно, пропорции изменяются. Круг может стать овалом. Поля принтера сохраняются." : "";
    public IReadOnlyList<Choice<MonochromeMode>> MonoChoices { get; }=[new(MonochromeMode.Auto,"Авто · по типу страницы"),new(MonochromeMode.Text,"Текст · резкий (порог)"),new(MonochromeMode.Photo,"Фото · дизеринг")];
    public IReadOnlyList<int> DpiChoices { get; }=[300,600];
    public string Summary=>$"{RussianQuantity.Format(_pages.Length,"страница","страницы","страниц")} · {RussianQuantity.Format(_pages.Sum(p=>p.Copies),"оттиск","оттиска","оттисков")}";
    public bool ShowDuplexPlan=>IsPhysicalPrinter && Settings.ManualDuplex;
    public string DuplexSummary { get; private set; }="";
    public string DuplexPairs { get; private set; }="";
    public bool HasAutomaticLayout=>_pages.Any(p=>p.UsesAutomaticContentLayout);
    public bool IsCustomPaper=>Settings.IsCustom;
    public bool IsNotebookPaper=>Settings.IsNotebook;
    private bool IsHp=>Settings.PrinterName.Contains("P1102",StringComparison.OrdinalIgnoreCase);
    public double PaperWidthMinimum=>IsHp ? 76 : 25;
    public double PaperWidthMaximum=>IsHp ? 216 : 1000;
    public double PaperHeightMinimum=>IsHp ? 127 : 25;
    public double PaperHeightMaximum=>IsHp ? 356 : 1000;
    public string PaperLimits=>IsHp ? "P1102: ширина подачи 76–216 мм, длина 127–356 мм. Ориентация поворачивает макет, а не бумагу в лотке." : "Размер бумаги: 25–1000 мм; возможность печати проверяется по выбранному драйверу.";
    public bool ShowThreshold=>Settings.Monochrome!=MonochromeMode.Photo;
    public string MonoHint
    {
        get
        {
            try
            {
                var source=Settings.Snapshot();
                return _pages.Any(p=>p.HasImages && ManualDuplexPlanner.ForPage(p,source).Monochrome==MonochromeMode.Text)
                    ? "Порог удаляет полутона. Для фото и сканов выберите «Фото» в настройках соответствующей страницы." : "";
            }
            catch(ArgumentException) { return ""; }
        }
    }
    [ObservableProperty] private string error="";
    [ObservableProperty] private string warning="";
    [ObservableProperty] private string printerMessage="Поля: не менее 5 мм";
    [ObservableProperty] private BitmapSource? preview;
    [ObservableProperty] private int previewIndex;
    [ObservableProperty] private bool isLoading;
    public int PageCount=>_pages.Length;
    public string PreviewTitle=>$"Страница {PreviewIndex+1} из {PageCount}";
    public bool CanSubmit=>!IsLoading && Error.Length==0;
    public string SubmitAvailability=>IsLoading ? "Подождите: проверяются принтер, бумага и печатный просмотр." : Error.Length>0 ? Error :
        AllowCropping ? "Проверьте просмотр и разрешите указанную обрезку перед отправкой." : "Передать задание в очередь Windows. Физический вывод зависит от принтера.";
    partial void OnIsLoadingChanged(bool value) { OnPropertyChanged(nameof(CanSubmit)); OnPropertyChanged(nameof(SubmitAvailability)); }
    public bool AllowCropping { get; private set; }
    public PrinterProfile? Profile { get; private set; }
    public PrintSettingsSnapshot? AcceptedSettings { get; private set; }
    public static void SetPaper(PrintSettings settings,SheetPaper paper)
    {
        settings.Notebook=paper.Notebook;
        settings.CustomWidthMm=paper.SizeMm.Width; settings.CustomHeightMm=paper.SizeMm.Height;
        settings.Paper=paper.Notebook!=null ? PaperPreset.Notebook : paper.SizeMm==new SizeD(210,297) ? PaperPreset.A4 : paper.SizeMm==new SizeD(148,210) ? PaperPreset.A5 : paper.SizeMm==new SizeD(165,205) ? PaperPreset.Notebook : PaperPreset.Custom;
        settings.Landscape=paper.Landscape;
    }
    private void Changed(object? sender,PropertyChangedEventArgs e)
    {
        if(_choosingNotebook) return;
        if(e.PropertyName==nameof(PrintSettings.Paper) && Settings.IsNotebook && Settings.Notebook==null && CanChangePaper)
        {
            _choosingNotebook=true;
            try { Settings.Notebook=_notebookProfiles.FirstOrDefault() ?? NotebookProfiles.Default(Models.NotebookVariant.GridSingle); Settings.Landscape=Settings.Notebook.IsSpread; }
            finally { _choosingNotebook=false; }
        }
        foreach(var name in new[] { nameof(IsCustomPaper),nameof(IsNotebookPaper),nameof(NotebookVariant),nameof(ShowThreshold),nameof(MonoHint),nameof(ScalingHint),nameof(IsPhysicalPrinter),nameof(ShowDuplexPlan),nameof(CanShowPaperGuide),nameof(PaperWidthMinimum),nameof(PaperWidthMaximum),nameof(PaperHeightMinimum),nameof(PaperHeightMaximum),nameof(PaperLimits),nameof(HasMixedPaper),nameof(PaperHint) }) OnPropertyChanged(name);
        Profile=null; _profiles.Clear(); _driverError=null; Validate(); _=RefreshAsync();
    }
    partial void OnPreviewIndexChanged(int value)
    {
        var next=Math.Clamp(value,0,Math.Max(0,PageCount-1));
        if(next!=value) { PreviewIndex=next; return; }
        OnPropertyChanged(nameof(PreviewTitle)); OnPropertyChanged(nameof(CanShowPaperGuide)); _=RefreshAsync();
    }
    private PrintSettingsSnapshot PreviewSettings(bool allowCrop=true)=>ManualDuplexPlanner.ForPage(_pages[PreviewIndex],Settings.Snapshot(allowCrop:allowCrop));
    private RectD SafeFor(PageSnapshot page)
    {
        var settings=ManualDuplexPlanner.ForPage(page,Settings.Snapshot(allowCrop:true)); var size=settings.OrientedPaperMm;
        var hardware=_profiles.TryGetValue(settings,out var profile) ? profile.PrintableMm : new RectD(5,5,size.Width-10,size.Height-10);
        return PrintViewport.SourceArea(settings,PageLayout.PrintSafeArea(size,hardware,settings.SafeMarginMm));
    }
    public void Validate()
    {
        Error=_driverError ?? ""; Warning=""; AllowCropping=false; DuplexSummary=""; DuplexPairs="";
        try
        {
            if(_pages.Length==0) throw new ArgumentException("Отметьте страницы в очереди.");
            var source=Settings.Snapshot(); ManualDuplexPlanner.ValidateQueue(_pages,source);
            if(_pages.Any(p=>p.Sheet?.Assets.Any(a=>string.IsNullOrEmpty(a.WorkingPath) || !System.IO.File.Exists(a.WorkingPath))==true))
                throw new ArgumentException("Отсутствуют исходники. Переподключите их через меню «Проект».");
            if(HasMixedPaper && !IsPhysicalPrinter) throw new ArgumentException("Размер бумаги в очереди различается. Для PDF выберите листы одного формата: смешанные размеры требуют отдельных файлов.");
            if(ShowDuplexPlan)
            {
                var plan=ManualDuplexPlanner.Build(_pages,Settings.Snapshot());
                DuplexSummary=$"{RussianQuantity.Format(plan.Sheets.Count,"лист","листа","листов")} бумаги · {RussianQuantity.Format(plan.ContentImpressions,"оттиск","оттиска","оттисков")} · {RussianQuantity.Format(plan.BlankSides,"пустая сторона","пустые стороны","пустых сторон")}";
                static string Side(PreparedPrintSide side)=>side.IsBlank ? "пусто" : $"стр. {side.QueueOrdinal}";
                DuplexPairs=string.Join("\n",plan.Sheets.Take(12).Select(s=>$"Лист {s.Number}: {Side(s.Front)} / {Side(s.Back)}"));
                if(plan.Sheets.Count>12) DuplexPairs+=$"\n… еще {RussianQuantity.Format(plan.Sheets.Count-12,"лист","листа","листов")}";
            }
            var affected=_pages.Count(p=>
                p.UsesAutomaticContentLayout && PageLayout.Crops(p.ContentSize,SafeFor(p),ManualDuplexPlanner.ForPage(p,source).Scaling)
                || SheetEditing.HasClipping(p,SafeFor(p)));
            AllowCropping=affected>0;
            if(AllowCropping) Warning=$"Будут обрезаны края на {RussianQuantity.Format(affected,"странице","страницах","страницах")}. Проверьте просмотр. Для исходных фото можно выбрать «Вписать».";
        }
        catch(Exception ex) when(ex is ArgumentException or InvalidOperationException) { Error=ex.Message; }
        OnPropertyChanged(nameof(AllowCropping));
        OnPropertyChanged(nameof(CanSubmit));
        OnPropertyChanged(nameof(SubmitAvailability));
        OnPropertyChanged(nameof(DuplexSummary)); OnPropertyChanged(nameof(DuplexPairs));
    }
    public bool TryAccept(bool acknowledgeClipping)
    {
        Validate();
        if(!CanSubmit || AllowCropping && !acknowledgeClipping) return false;
        AcceptedSettings=Settings.Snapshot(allowCrop:AllowCropping) with { ManualDuplex=IsPhysicalPrinter && Settings.ManualDuplex }; return true;
    }
    public async Task RefreshAsync()
    {
        if(_disposed) return;
        _update?.Cancel(); _update?.Dispose(); _update=new(); var token=_update.Token;
        IsLoading=true; OnPropertyChanged(nameof(CanSubmit)); Preview=null;
        try
        {
            await Task.Delay(180,token);
            Validate(); if(Error.Length!=0) return;
            var settings=PreviewSettings(); var page=_pages[PreviewIndex];
            var source=Settings.Snapshot(allowCrop:true);
            var targets=_pages.Select(p=>ManualDuplexPlanner.ForPage(p,source)).Distinct().ToArray();
            await _renderGate.WaitAsync(token);
            try
            {
                var result=await Task.Run(()=>
                {
                    var profiles=new Dictionary<PrintSettingsSnapshot,PrinterProfile>();
                    foreach(var target in targets)
                    { token.ThrowIfCancellationRequested(); profiles.Add(target,new PrinterInterop().Probe(target)); }
                    var profile=profiles[settings];
                    token.ThrowIfCancellationRequested();
                    var safe=PageLayout.PrintSafeArea(profile.ActualPaperMm,profile.PrintableMm,settings.SafeMarginMm);
                    var bitmap=_rasterizer.RenderPrintPreview(page,settings,safe,token);
                    return (profile,bitmap,profiles);
                },token);
                if(!token.IsCancellationRequested && !_disposed) { _profiles=result.profiles; Profile=result.profile; PrinterMessage=Profile.Message; Preview=result.bitmap; Validate(); }
            }
            finally { _renderGate.Release(); }
        }
        catch(OperationCanceledException) { }
        catch(Exception ex) { if(!token.IsCancellationRequested) { _driverError=Error=ex.Message; PrinterMessage="Проверьте принтер и формат бумаги."; } }
        finally { if(!token.IsCancellationRequested) { IsLoading=false; OnPropertyChanged(nameof(CanSubmit)); } }
    }
    [RelayCommand]
    private async Task RefreshPrintersAsync()
    {
        IsLoading=true; OnPropertyChanged(nameof(CanSubmit));
        try
        {
            var names=await Task.Run(PrinterInterop.ListPrinters);
            if(_disposed) return;
            var previous=Settings.PrinterName;
            Printers.Clear(); foreach(var name in names) Printers.Add(name);
            Settings.PrinterName=names.Contains(previous) ? previous : "";
            Profile=null; _profiles.Clear(); _driverError=null; await RefreshAsync();
        }
        catch(Exception ex) { if(!_disposed) { _driverError=Error=ex.Message; IsLoading=false; OnPropertyChanged(nameof(CanSubmit)); } }
    }
    public void Dispose() { _disposed=true; Settings.PropertyChanged-=Changed; _update?.Cancel(); }
}
