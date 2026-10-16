using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrintMaster.Models;
using PrintMaster.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing.Printing;
using System.Globalization;
using System.Windows.Media.Imaging;

namespace PrintMaster.ViewModels;

public partial class PrintSessionViewModel : ObservableObject,IDisposable
{
    private readonly SessionWorkspace _workspace;
    private readonly FileImportService _import;
    private readonly PageRasterizer _rasterizer;
    private readonly ImageExportService _export;
    private readonly IPrintJobRunner _engine;
    private readonly PrinterInterop _printers;
    private readonly DocxConverter _converter;
    private readonly IUserDialogs _dialogs;
    private readonly CancellationTokenSource _lifetime=new();
    private CancellationTokenSource? _operation,_previewCancellation,_probeCancellation;
    private readonly SemaphoreSlim _previewGate=new(1,1);
    private readonly SemaphoreSlim _thumbnailGate=new(1,1);
    private readonly Queue<PrintPageItem> _thumbnailLru=new();
    private bool _disposed;

    public PrintSessionViewModel(IUserDialogs dialogs,string? recoveryDirectory=null,IClipboardService? clipboard=null,IApplicationPreferencesStore? preferences=null,IPrintJobRunner? printRunner=null,IWorkspaceStateStore? workspaceState=null)
    {
        _dialogs=dialogs; _clipboard=clipboard ?? new ClipboardService(); _workspace=new(); _converter=new(); var pdf=new PdfiumRenderer(); _printers=new();
        _import=new(_workspace,_converter,pdf); _rasterizer=new(pdf); _export=new(_rasterizer); _engine=printRunner ?? new PrintEngine(_rasterizer,_printers);
        _preferencesStore=preferences ?? new MemoryApplicationPreferencesStore();
        _environmentStore=workspaceState ?? new MemoryWorkspaceStateStore();
        Settings.PropertyChanged+=SettingsChanged;
        LayoutSettings.PropertyChanged+=LayoutChanged;
        InitializeProjectState(recoveryDirectory);
        InitializeHistory();
        InitializeActionAvailability();
        InitializeColorPickingNotifications();
        InitializeInkToolSettingsNotifications();
        InitializePhotoGridNotifications();
        InitializeMarkdownNotifications();
        InitializePageOptionsNotifications();
        Pages.CollectionChanged+=(_,_)=>{ if(!IsQueueMutation && !_restoringProject && !_applyingTextFlow) NotifyQueueSelection(); };
    }

    public ObservableCollection<PrintPageItem> Pages { get; }=new();
    public ObservableCollection<string> Printers { get; }=new();
    public PrintSettings Settings { get; }=new();
    public IReadOnlyList<Choice<PaperPreset>> PaperChoices { get; }=[new(PaperPreset.A4,"A4 · 210 × 297 мм"),new(PaperPreset.A5,"A5 · 148 × 210 мм"),new(PaperPreset.Notebook,"Тетрадный лист"),new(PaperPreset.Custom,"Пользовательский")];
    public IReadOnlyList<Choice<bool>> OrientationChoices { get; }=[new(false,"Портрет"),new(true,"Альбом")];
    public IReadOnlyList<Choice<ScaleMode>> ScaleChoices { get; }=[new(ScaleMode.Fit,"Вписать (Fit)"),new(ScaleMode.Fill,"Заполнить (Fill)"),new(ScaleMode.Stretch,"Растянуть (Stretch)")];
    public IReadOnlyList<Choice<MonochromeMode>> MonoChoices { get; }=[new(MonochromeMode.Auto,"Авто · по типу страницы"),new(MonochromeMode.Text,"Текст · резкий (порог)"),new(MonochromeMode.Photo,"Фото · дизеринг (полутона)")];
    public IReadOnlyList<int> DpiChoices { get; }=[300,600];
    public IReadOnlyList<Choice<double>> CropRatios { get; }=[new(0,"Свободно"),new(1,"1 : 1"),new(4d/3,"4 : 3"),new(3d/2,"3 : 2"),new(16d/9,"16 : 9"),new(210d/297,"A4 портрет")];
    public IReadOnlyList<Choice<string>> InkColors { get; }=[new("#111827","Черный"),new("#777777","Серый"),new("#C62828","Красный"),new("#175E9C","Синий")];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSelection),nameof(IsImageSelected),nameof(IsDocumentSelected),nameof(SelectionTitle))]
    [NotifyCanExecuteChangedFor(nameof(RotateCommand),nameof(SaveCopyCommand),nameof(UndoCommand),nameof(RedoCommand),nameof(ResetCropCommand))]
    private PrintPageItem? selectedPage;
    [ObservableProperty] private BitmapSource? editorPreview;
    [ObservableProperty] private EditorPreviewFrame? editorFrame;
    [ObservableProperty] private PrinterProfile? printerProfile;
    [ObservableProperty] private string printerMessage="Проверяем параметры драйвера…";
    [ObservableProperty] private string status="Добавьте документы или изображения, чтобы начать.";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle),nameof(CanEdit))]
    [NotifyCanExecuteChangedFor(nameof(AddFilesCommand),nameof(PrintCommand),nameof(SaveCopyCommand),nameof(RotateCommand),nameof(RemovePageCommand),nameof(MoveUpCommand),nameof(MoveDownCommand),nameof(UndoCommand),nameof(RedoCommand),nameof(ResetCropCommand),nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveProjectCommand),nameof(SaveProjectAsCommand),nameof(OpenProjectCommand),nameof(RelinkSourcesCommand),nameof(RecoverSessionCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenRecentProjectCommand),nameof(RemoveRecentProjectCommand),nameof(ClearRecentProjectsCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateSheetCommand),nameof(InsertSheetImagesCommand),nameof(AlignSheetObjectCommand),nameof(RemoveSheetObjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(BeginFlowTextCommand),nameof(EditSelectedTextCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopySelectionCommand),nameof(PasteClipboardCommand))]
    [NotifyCanExecuteChangedFor(nameof(IncreaseCopiesCommand),nameof(DecreaseCopiesCommand))]
    private bool isBusy;
    [ObservableProperty] private int progressCompleted;
    [ObservableProperty] private int progressTotal=1;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ZoomFactor))] private double zoom=50;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsCropTool),nameof(ToolHint))] private EditorTool tool=EditorTool.Select;
    [ObservableProperty] private string inkColor="#111827";
    [ObservableProperty] private double strokeThickness=4;
    [ObservableProperty] private double textSize=36;
    [ObservableProperty] private double cropRatio;
    [ObservableProperty] private int exportDpi=300;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanEdit),nameof(ShowEditorTools),nameof(CanvasHint))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand),nameof(RedoCommand),nameof(CopySelectionCommand))] private bool showPrintPreview;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PreviewActivity))] private bool isPreviewLoading;

    public bool HasSelection=>SelectedPage!=null;
    public bool IsImageSelected=>SelectedPage?.IsImage==true;
    public bool IsDocumentSelected=>SelectedPage!=null && !SelectedPage.IsImage && !SelectedPage.IsBlank;
    public bool IsIdle=>!IsBusy;
    public bool CanEdit=>IsIdle && (IsSheetSelected || CanCropImage) && SelectedPage?.HasMissingSources!=true && !ShowPrintPreview;
    public bool ShowEditorTools=>(IsSheetSelected || CanCropImage) && !ShowPrintPreview;
    public string SelectionTitle=>SelectedPage is {} p ? $"{p.FileName} · {p.Description}" : "Предпросмотр";
    public bool IsCropTool=>Tool==EditorTool.Crop;
    public double ZoomFactor=>Zoom/100;
    public int SelectedCount=>Pages.Count(p=>p.IsIncluded);
    public int Impressions=>Pages.Where(p=>p.IsIncluded).Sum(p=>p.Copies);
    public string QueueSummary=>$"{RussianQuantity.Format(Pages.Count,"страница","страницы","страниц")} в очереди";
    public string PrintLabel=>HasPendingPaperJob ? PaperWorkflow.PendingLabel : $"Печать…  {SelectedCount} стр. · {Impressions} отт.";
    private SizeD DisplayPaperMm=>EffectiveSettings(SelectedPage).OrientedPaperMm;
    public double CanvasWidth=>DisplayPaperMm.Width*96/25.4;
    public double CanvasHeight=>DisplayPaperMm.Height*96/25.4;
    public RectD CanvasSafeArea
    {
        get
        {
            var size=DisplayPaperMm;
            var profile=PrinterProfile is {} candidate && Math.Abs(candidate.ActualPaperMm.Width-size.Width)<1.5 && Math.Abs(candidate.ActualPaperMm.Height-size.Height)<1.5 ? candidate : null;
            var hardware=profile?.PrintableMm ?? new RectD(5,5,size.Width-10,size.Height-10);
            try { var mm=PageLayout.PrintSafeArea(size,hardware,5); return new(mm.X*96/25.4,mm.Y*96/25.4,mm.Width*96/25.4,mm.Height*96/25.4); }
            catch { return new(19,19,Math.Max(1,CanvasWidth-38),Math.Max(1,CanvasHeight-38)); }
        }
    }
    public bool HasFillWarning=>EffectiveSettings(SelectedPage).Scaling==ScaleMode.Fill && SelectedPage is {} p && p.Snapshot().UsesAutomaticContentLayout && PageLayout.Crops(p.Snapshot().ContentSize,CanvasSafeArea,EffectiveSettings(p).Scaling);
    public string PaperSummary=>$"{DisplayPaperMm.Width:0.###} × {DisplayPaperMm.Height:0.###} мм · {EffectiveSettings(SelectedPage).Dpi} DPI";
    public string ToolHint=>Tool switch
    {
        EditorTool.Crop=>"Кадрирование открывается в отдельном окне. Перетаскивайте рамку; Enter — применить, Esc — отменить.",
        EditorTool.Text=>"Щелкните по листу, чтобы добавить текст. Двойной щелчок в режиме выбора — правка.",
        EditorTool.Eraser=>"Ведите ластиком по штрихам кисти/маркера. Фото, текст и фигуры сохраняются. Esc — отмена жеста.",
        EditorTool.ObjectEraser=>"Щелкните по объекту, чтобы удалить его целиком. Ctrl+Z — отменить.",
        EditorTool.Select=>"Перетаскивайте объекты и ручки. Фото сохраняет пропорции; Shift — пропорции фигур или поворот с шагом 15°. Alt+клик — объект под другим. Delete — удалить, Ctrl+Z — отменить.",
        _=>"Нажмите и потяните, чтобы нарисовать. Esc — отмена жеста."
    };
    public string CanvasHint=>IsPickingProjectColor ? "Пипетка: выберите цвет на холсте. Esc — отмена." : StretchHint+(ShowPrintPreview ? "Печатный вид · до 120 DPI. Полутона и порог уже применены; фактура точек зависит от DPI печати." : IsSheetSelected ? ToolHint : SelectedPage?.IsComposed==true
        ? "Макет листа · размеры объектов заданы в миллиметрах. Пунктир показывает безопасную область." : IsImageSelected ? ToolHint
        : "Пунктир — безопасная печатная область. Документы доступны для просмотра и поворота.");
    private string StretchHint=>EffectiveSettings(SelectedPage).Scaling==ScaleMode.Stretch && SelectedPage?.Snapshot().UsesAutomaticContentLayout==true
        ? "Растянуть: всё изображение видно, пропорции изменяются. " : "";
    public string MonochromeHelp=>EffectiveSettings(SelectedPage).Monochrome switch
    {
        MonochromeMode.Auto=>"Авто: изображения — с полутонами; PDF/DOCX — резкий текст. Для сканов и серых шкал в документах выберите «Фото».",
        MonochromeMode.Text=>"Порог: все оттенки станут только черным или белым. Светлые линии исчезнут, темные полутона сольются. Для серых шкал выберите «Фото».",
        _=>"Серые оттенки передаются мелкими черными точками. Подходит для фотографий, сканов и тестовых шкал."
    };
    public bool HasThresholdWarning=>Pages.Any(p=>p.IsIncluded && EffectiveSettings(p).Monochrome==MonochromeMode.Text && p.Snapshot().HasImages);

    private Task? _initialization;
    public Task InitializeAsync()=>_initialization ??=InitializeSessionAsync();
    partial void OnSelectedPageChanging(PrintPageItem? value)
    {
        if(!_restoringProject && !_applyingTextFlow && value!=SelectedPage) FinishTextEditing();
    }
    partial void OnSelectedPageChanged(PrintPageItem? value)
    {
        // Completing an inline flow edit can replace the page models while a queue
        // selection is being assigned. Rebind to the live page with the same identity.
        if(value!=null && !Pages.Contains(value))
        {
            var story=value.Document.Objects.OfType<TextObject>().FirstOrDefault(t=>t.Flow!=null)?.Flow?.StoryId;
            var live=Pages.FirstOrDefault(p=>p.Id==value.Id) ?? Pages.FirstOrDefault(p=>story!=null && p.Document.Objects.OfType<TextObject>().Any(t=>t.Flow?.StoryId==story)) ?? Pages.FirstOrDefault();
            if(live!=value) { SelectedPage=live; return; }
        }
        SelectedSheetObjectId=null;
        SelectedSourceAnnotation=null;
        SourceImageSelected=false;
        if(!_restoringProject) SyncLayoutToSheet();
        Tool=EditorTool.Select; OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(HasFillWarning));
        OnPropertyChanged(nameof(ShowEditorTools)); OnPropertyChanged(nameof(CanvasHint)); OnPropertyChanged(nameof(IsTextMode));
        NotifySheetMode();
        if(value?.IsBlank==true) SelectedSheetObjectId=value.Document.Objects.OfType<TextObject>().FirstOrDefault(t=>t.Flow!=null)?.Id;
        _=LoadEditorAsync(value);
        if(!string.IsNullOrWhiteSpace(Settings.PrinterName))_=ProbePrinterAsync();
        TrackActiveQueuePage(value);
        if(!IsBusy) Status=value==null ? "Добавьте файлы или создайте лист." : value.HasMissingSources ? "Исходник отсутствует: переподключите файл через «Проект»." : IsDocumentSelected ? "Документ открыт для просмотра и поворота." : "Выберите инструмент или объект на листе.";
        NotifyInspector();
        NotifyPageOptions();
        ScheduleAutosave();
    }
    partial void OnShowPrintPreviewChanged(bool value) { Tool=EditorTool.Select; NotifySheetMode(); _=LoadEditorAsync(SelectedPage); ScheduleAutosave(); }
    partial void OnShowPrintPreviewChanging(bool value) { if(value) FinishTextEditing(); }
    partial void OnIsBusyChanged(bool value) { NotifySheetMode(); NotifyQueueSelection(); ApplicationSettingsCommand.NotifyCanExecuteChanged(); NotifyInspector(); }
    partial void OnToolChanged(EditorTool value) { DrawingStyleError=""; OnPropertyChanged(nameof(CanvasHint)); NotifyTextProperties(); NotifyInspector(); }
    partial void OnToolChanging(EditorTool value)
    {
        if(IsEditingText && !_endingTextEdit && value!=EditorTool.Select) FinishTextEditing();
        if(value==EditorTool.Text && !IsEditingText)
        { SelectedSourceAnnotation=null; _selectedTextAnnotationContentId=null; SetObjectSelection([]); }
    }
    partial void OnZoomChanged(double value) { var clamped=ViewportZoom.Clamp(value); if(value!=clamped) Zoom=clamped; OnPropertyChanged(nameof(ZoomText)); ScheduleAutosave(); }
    public string ZoomText
    {
        get=>Zoom.ToString("0.##",CultureInfo.CurrentCulture);
        set
        {
            if(NumericInputRules.TryParse(value,out var number,true) && number>=ViewportZoom.Minimum && number<=ViewportZoom.Maximum) SetManualZoom(number);
            else { Status="Введите масштаб числом от 5 до 800%."; OnPropertyChanged(nameof(ZoomText)); }
        }
    }
    public event EventHandler<double>? ManualZoomRequested;
    public void SetManualZoom(double value) { if(ManualZoomRequested!=null) ManualZoomRequested.Invoke(this,ViewportZoom.Clamp(value)); else ApplyManualZoom(value); }
    internal void ApplyManualZoom(double value) { AutoFitZoom=false; Zoom=ViewportZoom.Clamp(value); ScheduleAutosave(); }
    partial void OnPrinterProfileChanged(PrinterProfile? value)
    {
        OnPropertyChanged(nameof(CanvasSafeArea)); OnPropertyChanged(nameof(HasFillWarning));
        OnPropertyChanged(nameof(HasSheetClipping));
        NotifyTextProperties();
        if(ShowPrintPreview && SelectedPage!=null) _=LoadEditorAsync(SelectedPage);
    }

    private void SettingsChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(_restoringProject || _applyingHistory || _applyingPrintSettings) return;
        if(IsEditingText && e.PropertyName is nameof(PrintSettings.Paper) or nameof(PrintSettings.CustomWidthMm) or nameof(PrintSettings.CustomHeightMm) or nameof(PrintSettings.Landscape) or nameof(PrintSettings.Scaling)) FinishTextEditing();
        if(Pages.Count!=0 || CurrentProjectPath!=null) MarkProjectChanged();
        NotifyPageOptions();
        foreach(var property in new[] { nameof(CanvasWidth),nameof(CanvasHeight),nameof(CanvasSafeArea),nameof(PaperSummary),nameof(HasFillWarning),nameof(CanvasHint) }) OnPropertyChanged(property);
        if(e.PropertyName==nameof(PrintSettings.Monochrome))
        { OnPropertyChanged(nameof(IsTextMode)); OnPropertyChanged(nameof(MonochromeHelp)); OnPropertyChanged(nameof(HasThresholdWarning)); }
        if(ShowPrintPreview && SelectedPage!=null) _=LoadEditorAsync(SelectedPage);
        if(e.PropertyName==nameof(PrintSettings.PrinterName)) PrintCommand.NotifyCanExecuteChanged();
        if(e.PropertyName is nameof(PrintSettings.Paper) or nameof(PrintSettings.CustomWidthMm) or nameof(PrintSettings.CustomHeightMm) or nameof(PrintSettings.Landscape))
        { NotifySettingsDerived(); NotifySheetMode(); if(!IsSheetSelected) SyncLayoutToSheet(); }
        if(e.PropertyName is nameof(PrintSettings.PrinterName) or nameof(PrintSettings.Paper) or nameof(PrintSettings.CustomWidthMm) or nameof(PrintSettings.CustomHeightMm) or nameof(PrintSettings.Landscape) or nameof(PrintSettings.Dpi))
            _=ProbePrinterAsync();
    }
    private void NotifySettingsDerived() { OnPropertyChanged(nameof(IsCustomPaper)); OnPropertyChanged(nameof(IsNotebookPaper)); }
    public bool IsCustomPaper=>Settings.IsCustom;
    public bool IsNotebookPaper=>Settings.IsNotebook;
    public bool IsTextMode=>MonochromeRasterizer.Resolve(EffectiveSettings(SelectedPage).Monochrome,SelectedPage?.Snapshot().HasImages==true ? SourceKind.Image : SelectedPage?.Kind ?? SourceKind.Image)==MonochromeMode.Text;
    public bool AutoFitZoom { get; private set; }=true;
    public event EventHandler? FitZoomRequested;
    public event EventHandler? CropRequested;
    public void FitToViewport(double width,double height)
    {
        if(AutoFitZoom && width>0 && height>0) Zoom=Math.Floor(Math.Min(width/CanvasWidth,height/CanvasHeight)*100);
    }

    private void PageChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName==nameof(PrintPageItem.Copies)) { IncreaseCopiesCommand.NotifyCanExecuteChanged(); DecreaseCopiesCommand.NotifyCanExecuteChanged(); }
        if(e.PropertyName==nameof(PrintPageItem.PrintOptions)){NotifyPageOptions();if(sender==SelectedPage)_=ProbePrinterAsync();}
        if(_applyingHistory) return;
        if(e.PropertyName is nameof(PrintPageItem.IsIncluded) or nameof(PrintPageItem.Copies) or nameof(PrintPageItem.PrintOptions)) { NotifyQueue(); MarkProjectChanged(); }
        if(e.PropertyName==nameof(PrintPageItem.PrintOptions) && sender==SelectedPage)_=LoadEditorAsync(SelectedPage);
        if(e.PropertyName==nameof(PrintPageItem.HasMissingSources)) { PrintCommand.NotifyCanExecuteChanged(); if(sender==SelectedPage) _=LoadEditorAsync(SelectedPage); }
        if(e.PropertyName==nameof(PrintPageItem.RotationQuarterTurns) && sender==SelectedPage) _=LoadEditorAsync(SelectedPage);
        if(e.PropertyName==nameof(PrintPageItem.IsComposed) && sender==SelectedPage)
        {
            OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(ShowEditorTools)); OnPropertyChanged(nameof(CanvasHint)); NotifySheetMode();
            if(_previewComposed!=SelectedPage!.IsComposed) _=LoadEditorAsync(SelectedPage);
        }
    }
    private void EditsChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(_applyingHistory) return;
        if(e.PropertyName!=nameof(ImageEditState.Revision)) return;
        if(SelectedPage?.Edits==sender) NotifyDrawingStyleProperties();
        MarkProjectChanged();
        UndoCommand.NotifyCanExecuteChanged(); RedoCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(HasFillWarning));
        var page=Pages.FirstOrDefault(x=>x.Edits==sender);
        if(page!=null && page==SelectedPage && _selectedObjectIds.Any(id=>!page.Document.Objects.Any(o=>o.Id==id && o.Visible && !o.Locked)))
            SetObjectSelection(_selectedObjectIds,SelectedSheetObjectId);
        UndoQueueCommand.NotifyCanExecuteChanged(); RedoQueueCommand.NotifyCanExecuteChanged();
        if(page==SelectedPage && IsSheetSelected)
        {
            SyncLayoutToSheet(); NotifySheetMode();
            OnPropertyChanged(nameof(IsTextMode)); OnPropertyChanged(nameof(HasThresholdWarning));
        }
        if(page!=null) { page.Thumbnail=null; _=EnsureThumbnailAsync(page); }
        if(page==SelectedPage && (ShowPrintPreview || page?.IsComposed==true)) _=LoadEditorAsync(page);
    }
    private void NotifyQueue()
    {
        ExportPagesCommand.NotifyCanExecuteChanged();
        foreach(var property in new[] { nameof(SelectedCount),nameof(Impressions),nameof(PrintLabel),nameof(QueueSummary),nameof(HasThresholdWarning),nameof(HasSheetPaperMismatch),nameof(SheetPaperWarning) }) OnPropertyChanged(property);
        PrintCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(HighlightedSummary));
        NotifyInspector();
    }

    [RelayCommand(CanExecute=nameof(IsIdle))]
    private async Task AddFilesAsync() => await ImportFilesAsync(_dialogs.PickFiles());

    public async Task ImportFilesAsync(IEnumerable<string> paths)
    {
        if(IsBusy) return; FinishTextEditing(); var input=paths.ToArray(); if(input.Length==0) return;
        using var history=BeginHistoryEdit("Добавить файлы");
        BeginOperation(SessionOperationKind.Import,total:input.Length); var errors=new List<string>(); var initialCount=Pages.Count;
        try
        {
            for(var index=0;index<input.Length;index++)
            {
                _operation!.Token.ThrowIfCancellationRequested(); Status=$"Загрузка {index+1}/{input.Length}: {System.IO.Path.GetFileName(input[index])}";
                try
                {
                    if(string.Equals(System.IO.Path.GetExtension(input[index]),".md",StringComparison.OrdinalIgnoreCase))await ImportMarkdownAsync(input[index],_operation.Token);
                    else{var pages=await _import.ImportAsync(input[index],_operation.Token,ImportProgressFor(input[index],index,input.Length));foreach(var page in pages)AddPage(page);}
                }
                catch(OperationCanceledException) { throw; }
                catch(Exception ex) { errors.Add(System.IO.Path.GetFileName(input[index])+": "+ex.Message); }
                ApplyOperationReport(new($"Обработано файлов: {index+1}/{input.Length}",index+1,input.Length));
            }
            Status=$"Добавление завершено: {RussianQuantity.Format(Pages.Count-initialCount,"страница","страницы","страниц")}. Выберите страницы и количество копий.";
            if(errors.Count!=0)
            {
                FreezeOperationReports(); var reason=errors[0].Split('\n')[0].Trim(); if(reason.Length>250) reason=reason[..250]+"…";
                Status=$"Импорт завершён с ошибками: {RussianQuantity.Format(errors.Count,"файл","файла","файлов")}. Добавлено: {RussianQuantity.Format(Pages.Count-initialCount,"страница","страницы","страниц")}. Проверьте исходники и повторите импорт оставшихся.";
                _dialogs.ShowOperationError(new("Не все файлы добавлены",reason+$"\nНе удалось добавить файлов: {errors.Count}. Уже добавленные страницы сохранены.",
                    "Проверьте исходники. Для DOCX проверьте LibreOffice в настройках, документ и доступ к файлу.",string.Join("\n\n",errors)));
            }
        }
        catch(OperationCanceledException) { Status=$"Импорт отменён. В очереди: {RussianQuantity.Format(Pages.Count-initialCount,"страница","страницы","страниц")}; добавленное содержимое доступно."; }
        finally { EndOperation(); }
    }

    public async Task EnsureThumbnailAsync(PrintPageItem page)
    {
        if(_disposed || page.Thumbnail!=null || page.IsLoading) return;
        page.IsLoading=true; var version=page.Edits.Revision; var rotation=page.RotationQuarterTurns;
        if(page.HasMissingSources) { page.IsLoading=false; return; }
        try
        {
            await _thumbnailGate.WaitAsync(_lifetime.Token);
            try
            {
                var snapshot=page.Snapshot(); var thumbnail=await Task.Run(()=>_rasterizer.RenderPreview(snapshot,180,200,_lifetime.Token),_lifetime.Token);
                if(!_disposed && Pages.Contains(page) && version==page.Edits.Revision && rotation==page.RotationQuarterTurns)
                {
                    page.Thumbnail=thumbnail; _thumbnailLru.Enqueue(page);
                    if(SelectedPage==page && PreviewError.StartsWith("Не удалось загрузить миниатюру",StringComparison.Ordinal)) PreviewError="";
                    while(_thumbnailLru.Count>96) { var old=_thumbnailLru.Dequeue(); if(old!=page) old.Thumbnail=null; }
                }
            }
            finally { _thumbnailGate.Release(); }
        }
        catch(OperationCanceledException) { }
        catch(Exception) { if(SelectedPage==page && version==page.Edits.Revision && rotation==page.RotationQuarterTurns) { PreviewError="Не удалось загрузить миниатюру. Переподключите исходник или выберите страницу снова."; OnPropertyChanged(nameof(PreviewActivity)); } }
        finally { page.IsLoading=false; }
        if(!_disposed && Pages.Contains(page) && (version!=page.Edits.Revision || rotation!=page.RotationQuarterTurns))
            await EnsureThumbnailAsync(page);
    }
    internal void AddPage(PrintPageItem page)
    {
        if(Pages.Any(p=>p.Id==page.Id)) throw new ArgumentException("Этот лист уже находится в очереди.");
        using var history=BeginHistoryEdit("Добавить страницу");
        page.PropertyChanged+=PageChanged; page.Edits.PropertyChanged+=EditsChanged; Pages.Add(page);
        SelectedPage ??=Pages.FirstOrDefault(); NotifyQueue();
        MarkProjectChanged();
    }
    private Guid? _previewPageId;
    private bool _previewPrintMode,_previewComposed;
    private async Task LoadEditorAsync(PrintPageItem? page)
    {
        _previewCancellation?.Cancel(); _previewCancellation?.Dispose();
        _previewCancellation=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); var token=_previewCancellation.Token;
        if(_previewPageId!=page?.Id || _previewPrintMode!=ShowPrintPreview || _previewComposed!=page?.IsComposed || page==null) { EditorPreview=null; EditorFrame=null; }
        _previewPageId=page?.Id; _previewPrintMode=ShowPrintPreview; _previewComposed=page?.IsComposed==true;
        PreviewError=""; IsPreviewLoading=page!=null; OnPropertyChanged(nameof(PreviewActivity)); if(page==null) return;
        if(page.HasMissingSources) { EditorPreview=null; EditorFrame=null; IsPreviewLoading=false; PreviewError="Исходник выбранной страницы отсутствует. Переподключите файл через меню «Проект»."; OnPropertyChanged(nameof(PreviewActivity)); return; }
        try
        {
            await Task.Delay(100,token);
            await _previewGate.WaitAsync(token);
            try
            {
                var revision=page.Document.Revision; var snapshot=page.Snapshot(); var printView=ShowPrintPreview;
                PrintSettingsSnapshot? settings=printView ? ManualDuplexPlanner.ForPage(snapshot,Settings.Snapshot()) : null;
                var safe=CanvasSafeArea; var safeMm=new RectD(safe.X*25.4/96,safe.Y*25.4/96,safe.Width*25.4/96,safe.Height*25.4/96);
                var effective=EffectiveSettings(page);
                var paper=page.IsSheetLayout ? page.Document.Paper : new SheetPaper(effective.PaperSizeMm,effective.Landscape,SheetMargins.SafeDefault);
                var preview=await Task.Run(()=>printView ? _rasterizer.RenderPrintPreview(snapshot,settings!,safeMm,token) : _rasterizer.LoadEditorSource(snapshot,token,paper),token);
                if(SelectedPage==page && !token.IsCancellationRequested && page.Document.Revision==revision)
                { EditorPreview=preview; EditorFrame=new(page.Id,revision,preview); }
            }
            finally { _previewGate.Release(); }
        }
        catch(OperationCanceledException) { }
        catch(Exception ex) { if(!token.IsCancellationRequested && SelectedPage==page) { PreviewError="Ошибка просмотра: "+ex.Message+". Выберите страницу снова или переподключите исходник."; OnPropertyChanged(nameof(PreviewActivity)); } }
        finally { if(!token.IsCancellationRequested) IsPreviewLoading=false; }
    }

    [RelayCommand] private async Task RefreshPrintersAsync()
    {
        try
        {
            var names=await Task.Run(PrinterInterop.ListPrinters); Printers.Clear(); foreach(var name in names) Printers.Add(name);
            if(!Printers.Contains(Settings.PrinterName))
            {
                Settings.PrinterName=names.FirstOrDefault(x=>x.Contains("P1102",StringComparison.OrdinalIgnoreCase))
                    ?? names.FirstOrDefault(x=>x.Contains("Print to PDF",StringComparison.OrdinalIgnoreCase))
                    ?? names.FirstOrDefault() ?? "";
            }
            await ProbePrinterAsync();
        }
        catch(Exception ex) { PrinterMessage="Не удалось получить принтеры: "+ex.Message; }
    }
    private async Task ProbePrinterAsync()
    {
        _probeCancellation?.Cancel(); _probeCancellation?.Dispose();
        _probeCancellation=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); var token=_probeCancellation.Token;
        PrinterProfile=null; PrinterMessage="Проверяем бумагу и поля драйвера…";
        try
        {
            await Task.Delay(300,token);
            var settings=EffectiveSettings(SelectedPage);
            ManualDuplexPlanner.ValidateSettings(settings);
            var profile=await Task.Run(()=>_printers.Probe(settings),token);
            if(!token.IsCancellationRequested) { PrinterProfile=profile; PrinterMessage=profile.Message; }
        }
        catch(OperationCanceledException) { }
        catch(Exception ex) { if(!token.IsCancellationRequested) PrinterMessage="Проверка драйвера: "+ex.Message+" Пока показаны ориентировочные поля 5 мм."; }
    }

    private bool CanPrint()=>IsIdle && !HasActiveMarkdownDraftError && (SelectedCount!=0 || HasPendingPaperJob);
    [RelayCommand(CanExecute=nameof(CanPrint))]
    private async Task PrintAsync()
    {
        if(!CommitInputForAction()) return;
        if(HasPendingPaperJob) { await ResumePaperJobAsync(); return; }
        FinishTextEditing();
        var pages=Pages.Where(p=>p.IsIncluded).Select(p=>p.Snapshot()).ToArray();
        var defaults=new PrintSettings { AvailableNotebookProfiles=_preferences.NotebookProfiles };
        ProjectPrintSettings.Capture(Settings).Apply(defaults);
        if(pages.All(p=>!p.UsesAutomaticContentLayout) && pages.FirstOrDefault()?.Sheet?.Paper is {} firstPaper) PrintReviewViewModel.SetPaper(defaults,firstPaper);
        // An unsupported draft is explained in the review dialog, where the printer can be changed.
        try
        {
            if(!string.IsNullOrWhiteSpace(defaults.PrinterName) && LookupPaperCalibration(defaults.Snapshot()) is {} calibration)
            { defaults.PrintOffsetXmm=calibration.OffsetXmm; defaults.PrintOffsetYmm=calibration.OffsetYmm; }
        }
        catch(ArgumentException) { }
        using var review=new PrintReviewViewModel(pages,defaults,Printers.ToArray());
        if(!_dialogs.ReviewPrint(review) || review.AcceptedSettings==null) return;
        var accepted=review.AcceptedSettings;
        var overflowing=pages.Where(p=>p.Sheet is {} sheet && NotebookTextLayout.Remainder(sheet).Length!=0).ToArray();
        if(overflowing.Length!=0 && !_dialogs.ConfirmNotebookOverflow(overflowing.Length,overflowing.Sum(p=>NotebookTextLayout.Remainder(p.Sheet!).Length))) return;
        accepted=accepted with { AllowNotebookOverflow=overflowing.Length!=0 };
        var isPdf=accepted.PrinterName.Contains("Microsoft Print to PDF",StringComparison.OrdinalIgnoreCase);
        var path=isPdf ? _dialogs.PickPdfOutput() : null; if(isPdf && path==null) return;
        ProjectPrintSettings.Capture(review.Settings).Apply(Settings);
        BeginOperation(SessionOperationKind.Print,total:pages.Sum(p=>p.Copies));
        try
        {
            var settings=accepted with { OutputPath=path };
            ProgressCompleted=0; ProgressTotal=pages.Sum(p=>p.Copies);
            var progress=PrintOperationProgress();
            var completed=await PaperWorkflow.StartAsync(pages,settings,progress,_operation!.Token);
            Status=!completed ? "Печать ожидает загрузки или переворота бумаги. Продолжение доступно кнопкой печати." : path!=null ? "PDF сохранен: "+path : "Задание передано в очередь принтера. Физический вывод зависит от его состояния.";
        }
        catch(OperationCanceledException) { Status="Печать отменена. Уже переданные принтеру оттиски могут быть напечатаны."; }
        catch(Exception ex) { ShowOperationFailure("Ошибка печати","Проверьте принтер, бумагу и печатный вид. Уже отправленные оттиски могут выйти; повторите только оставшиеся.",ex); }
        finally { EndOperation(); NotifyPaperJob(); }
    }

    private bool CanSave()=>IsIdle && IsImageSelected;
    [RelayCommand(CanExecute=nameof(CanSave))] private async Task SaveCopyAsync()
    {
        if(!CommitInputForAction() || SelectedPage==null) return; FinishTextEditing(); var page=SelectedPage.Snapshot(); string? output=null;
        if(string.IsNullOrEmpty(page.OriginalPath) || !System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(page.OriginalPath)))
        { output=_dialogs.PickImageOutput("Изображение_edited.png"); if(output==null) return; }
        BeginOperation(SessionOperationKind.SaveImage);
        try { Status="Сохранение копии…"; Status="Копия сохранена: "+await _export.SaveCopyAsync(page,ExportDpi,_operation!.Token,output); }
        catch(OperationCanceledException) { Status="Сохранение копии отменено. Исходное изображение сохранено."; }
        catch(Exception ex) { ShowOperationFailure("Не удалось сохранить копию","Выберите доступную папку и проверьте свободное место. Исходник не изменён.",ex); }
        finally { EndOperation(); }
    }
    private bool CanRotate()=>IsIdle && (IsSheetSelected ? SelectedSheetObject is ContentObject : SelectedPage?.Document.PrimaryContent!=null);
    [RelayCommand(CanExecute=nameof(CanRotate))] private void Rotate()
    {
        if(SelectedPage==null) return;
        if(IsSheetSelected && SelectedSheetObject is ContentObject content)
        {
            var b=content.Placement.BoundsMm;
            var bounds=new RectD(b.X+(b.Width-b.Height)/2,b.Y+(b.Height-b.Width)/2,b.Height,b.Width);
            SelectedPage.Document.ReplaceObject(content with { Edits=content.Edits with { QuarterTurns=(content.Edits.QuarterTurns+1)%4 },Placement=content.Placement with { BoundsMm=bounds } },"Повернуть изображение");
        }
        else SelectedPage.RotationQuarterTurns=(SelectedPage.RotationQuarterTurns+1)%4;
        SelectedPage.Thumbnail=null; _=EnsureThumbnailAsync(SelectedPage); OnPropertyChanged(nameof(HasFillWarning));
    }
    [RelayCommand(CanExecute=nameof(IsIdle))] private void RemovePage(PrintPageItem? page)
    {
        if(page==null) return; var id=page.Id; FinishTextEditing(); page=Pages.FirstOrDefault(p=>p.Id==id); if(page==null) return;
        using var history=BeginHistoryEdit("Удалить страницу");
        var index=Pages.IndexOf(page);
        page.PropertyChanged-=PageChanged; page.Edits.PropertyChanged-=EditsChanged; Pages.Remove(page); page.Thumbnail=null;
        if(SelectedPage==page) SelectedPage=Pages.Count==0 ? null : Pages[Math.Clamp(index,0,Pages.Count-1)]; NotifyQueue(); MarkProjectChanged();
    }
    [RelayCommand(CanExecute=nameof(IsIdle))] private void MoveUp(PrintPageItem? page)=>MoveSinglePage(page,-1);
    [RelayCommand(CanExecute=nameof(IsIdle))] private void MoveDown(PrintPageItem? page)=>MoveSinglePage(page,1);
    private void MoveSinglePage(PrintPageItem? page,int direction)
    {
        if(!IsIdle || page==null) return;var id=page.Id;FinishTextEditing();
        page=Pages.FirstOrDefault(p=>p.Id==id);if(page==null)return;
        var index=Pages.IndexOf(page);var target=index+direction;if(target<0 || target>=Pages.Count)return;
        using var history=BeginHistoryEdit("Переставить страницу");Pages.Move(index,target);MarkProjectChanged();
    }
    public void MovePage(PrintPageItem source,PrintPageItem target)
    {
        if(!IsIdle)return;var sourceId=source.Id;var targetId=target.Id;FinishTextEditing();
        var liveSource=Pages.FirstOrDefault(p=>p.Id==sourceId);var liveTarget=Pages.FirstOrDefault(p=>p.Id==targetId);
        if(liveSource==null || liveTarget==null)return;
        var from=Pages.IndexOf(liveSource);var to=Pages.IndexOf(liveTarget);
        if(from<0 || to<0 || from==to)return;
        using var history=BeginHistoryEdit("Переставить страницу");Pages.Move(from,to);MarkProjectChanged();
    }
    private bool CanIncreasePageCopies(PrintPageItem? page)=>IsIdle && page?.CanIncreaseCopies==true && Pages.Contains(page);
    private bool CanDecreasePageCopies(PrintPageItem? page)=>IsIdle && page?.CanDecreaseCopies==true && Pages.Contains(page);
    [RelayCommand(CanExecute=nameof(CanIncreasePageCopies))] private void IncreaseCopies(PrintPageItem? page) { if(CanIncreasePageCopies(page)) page!.Copies++; }
    [RelayCommand(CanExecute=nameof(CanDecreasePageCopies))] private void DecreaseCopies(PrintPageItem? page) { if(CanDecreasePageCopies(page)) page!.Copies--; }
    [RelayCommand] private void SetTool(EditorTool value)
    {
        if(!CanEdit || (value==EditorTool.Crop && !CanCropSelection)) return;
        FinishTextEditing();
        if(value==EditorTool.Crop)
        {
            CropRequested?.Invoke(this,EventArgs.Empty);
            return;
        }
        if(value!=EditorTool.Select)
        {
            SetObjectSelection([]); SelectedSourceAnnotation=null; SourceImageSelected=false;
        }
        if(Tool==value) OnPropertyChanged(nameof(Tool)); else Tool=value;
    }
    [RelayCommand] private void ZoomIn()=>SetManualZoom(Zoom+10);
    [RelayCommand] private void ZoomOut()=>SetManualZoom(Zoom-10);
    [RelayCommand] private void FitZoom() { AutoFitZoom=true; FitZoomRequested?.Invoke(this,EventArgs.Empty); }
    private bool CanUndo()=>IsIdle && !IsEditingText && !ShowPrintPreview && _historyDepth==0 && _historyUndo.Count>0;
    private bool CanRedo()=>IsIdle && !IsEditingText && !ShowPrintPreview && _historyDepth==0 && _historyRedo.Count>0;
    [RelayCommand(CanExecute=nameof(CanUndo))] private void Undo()=>UndoHistory();
    [RelayCommand(CanExecute=nameof(CanRedo))] private void Redo()=>RedoHistory();
    [RelayCommand(CanExecute=nameof(CanSave))] private void ResetCrop()
    { if(SelectedPage!=null) SelectedPage.Edits.SetCrop(new(0,0,SelectedPage.SourceWidth,SelectedPage.SourceHeight)); Tool=EditorTool.Select; }
    [RelayCommand] private void SelectAll() { if(IsIdle) { FinishTextEditing(); using var history=BeginHistoryEdit("Отметить все к печати"); foreach(var page in Pages) page.IsIncluded=true; } }
    [RelayCommand] private void SelectNone() { if(IsIdle) { FinishTextEditing(); using var history=BeginHistoryEdit("Снять все галочки печати"); foreach(var page in Pages) page.IsIncluded=false; } }
    [RelayCommand] private void PickLibreOffice()
    { var path=_dialogs.PickLibreOffice(); if(path!=null) { _converter.ExecutablePath=path; Status="LibreOffice: "+path; } }
    [RelayCommand(CanExecute=nameof(CanCancelOperation))] private void Cancel()=>RequestOperationCancellation();
    public void Dispose()
    {
        if(_disposed) return; _disposed=true; _lifetime.Cancel(); _operation?.Cancel(); _previewCancellation?.Cancel(); _probeCancellation?.Cancel();
        _autosaveDelay?.Cancel(); _recovery.Dispose();
        DisposeHistory();
        _workspace.Dispose();
    }
}
