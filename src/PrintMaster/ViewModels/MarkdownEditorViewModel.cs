using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrintMaster.Models;
using PrintMaster.Services;
using System.Collections.Immutable;
using System.IO;
using System.Windows.Media.Imaging;

namespace PrintMaster.ViewModels;

public sealed partial class MarkdownEditorViewModel : ObservableObject,IDisposable
{
    private readonly SheetPaper _initialPaper;
    private readonly TextStyle _initialBody;
    private readonly Func<MarkdownDocument,CancellationToken,Task<PreparedMarkdown>> _prepareAsync;
    private readonly Func<MarkdownDocument,SheetPaper,ImmutableArray<SourceAsset>,CancellationToken,Task<MarkdownLayoutResult>> _layoutAsync;
    private readonly bool _renderPreview;
    private readonly Func<MarkdownRenderPage,CancellationToken,BitmapSource> _previewRenderer;
    private PreparedMarkdown _lastPrepared;
    private CancellationTokenSource? _updateCancellation,_previewCancellation;
    private long _revision,_validRevision=-1,_previewEpoch;
    private bool _disposed,_changingPaper;
    internal Task UpdateTask { get; private set; }=Task.CompletedTask;
    internal Task PreviewTask { get; private set; }=Task.CompletedTask;

    public MarkdownEditorViewModel(PreparedMarkdown initial,SheetPaper paper,
        Func<MarkdownDocument,CancellationToken,Task<PreparedMarkdown>> prepareAsync,bool renderPreview=true,
        Func<MarkdownDocument,SheetPaper,ImmutableArray<SourceAsset>,CancellationToken,Task<MarkdownLayoutResult>>? layoutAsync=null,
        Func<MarkdownRenderPage,CancellationToken,BitmapSource>? previewRenderer=null)
    {
        ArgumentNullException.ThrowIfNull(initial); ArgumentNullException.ThrowIfNull(paper); ArgumentNullException.ThrowIfNull(prepareAsync);
        _lastPrepared=initial; _initialPaper=paper; _initialBody=initial.Document.Style.Body;
        _prepareAsync=prepareAsync; _layoutAsync=layoutAsync ?? ((doc,p,assets,token)=>MarkdownLayoutService.Shared.LayoutAsync(doc,p,assets,token));
        _renderPreview=renderPreview; _previewRenderer=previewRenderer ?? RenderPreview;
        source=initial.Document.Source; title=initial.Document.Title; fontFamily=_initialBody.FontFamily; fontSizePt=_initialBody.FontSizePt;
        color=_initialBody.Color; lineSpacing=_initialBody.LineSpacing; paragraphGapMm=initial.Document.Style.ParagraphGapMm;
        marginLeftMm=initial.Document.Style.Margins.LeftMm; marginTopMm=initial.Document.Style.Margins.TopMm;
        marginRightMm=initial.Document.Style.Margins.RightMm; marginBottomMm=initial.Document.Style.Margins.BottomMm;
        landscape=paper.Landscape; notebookVariant=paper.Notebook?.Variant ?? PrintMaster.Models.NotebookVariant.GridSingle;
        ScheduleUpdate();
    }

    public PreparedMarkdown? Result { get; private set; }
    public MarkdownLayoutResult? Layout { get; private set; }
    public SheetPaper Paper=>PaperFormat switch
    {
        "A4"=>SheetPaper.A4 with {Landscape=Landscape},
        "A5"=>new(new(148,210),Landscape,SheetMargins.SafeDefault),
        "Notebook"=>NotebookPaper(),
        _=>_initialPaper with {Landscape=Landscape}
    };
    public MarkdownStyle Style=>new(_initialBody with {FontFamily=FontFamily,FontSizePt=FontSizePt,Color=Color,LineSpacing=LineSpacing},
        new(MarginLeftMm,MarginTopMm,MarginRightMm,MarginBottomMm),ParagraphGapMm);
    public MarkdownDocument DraftDocument=>_lastPrepared.Document with {Source=Source,Title=Title,Style=Style};
    public bool CanApply=>!_disposed && !IsUpdating && Result!=null && Layout!=null && _validRevision==_revision && !HasBlockingError
        && (!_renderPreview || !IsPreviewLoading && Preview!=null);
    public bool HasError=>Error.Length!=0;
    public bool HasDiagnostics=>Diagnostics.Length!=0;
    public bool HasPreviewError=>PreviewError.Length!=0;
    public bool HasBlockingError=>HasError || HasPreviewError;
    public string BlockingError=>HasError ? Error : PreviewError;
    public bool HasErrorLine=>ErrorLine is >0;
    public bool IsNotebookPaper=>PaperFormat=="Notebook" || PaperFormat=="current" && _initialPaper.Notebook!=null;
    public bool CanChangeNotebookVariant=>PaperFormat=="Notebook";
    public IReadOnlyList<string> Fonts=>TextLayoutService.FontNames;
    public IReadOnlyList<Choice<string>> PaperChoices=>[new("current",$"Текущий · {_initialPaper.OrientedSizeMm.Width:g} × {_initialPaper.OrientedSizeMm.Height:g} мм"),
        new("A4","A4 · 210 × 297 мм"),new("A5","A5 · 148 × 210 мм"),new("Notebook","Тетрадный лист")];
    public IReadOnlyList<Choice<bool>> OrientationChoices {get;}=[new(false,"Портрет"),new(true,"Альбом")];
    public IReadOnlyList<Choice<NotebookVariant>> NotebookChoices=>NotebookProfiles.Choices;
    public string PaperSummary=>$"{Paper.OrientedSizeMm.Width:g} × {Paper.OrientedSizeMm.Height:g} мм";
    public string FontWarning=>string.IsNullOrWhiteSpace(FontFamily) || Fonts.Contains(FontFamily,StringComparer.OrdinalIgnoreCase)
        ? "" : "Шрифт не установлен: при вёрстке будет использован Segoe UI.";
    public bool HasFontWarning=>FontWarning.Length!=0;
    public int PageCount=>Layout?.Pages.Length ?? 0;
    public string PageLabel=>PageCount==0 ? "Нет готовых страниц" : $"Страница {SelectedPageIndex+1} из {PageCount}";
    public bool CanGoPrevious=>!_disposed && !IsUpdating && !HasBlockingError && SelectedPageIndex>0 && PageCount>0;
    public bool CanGoNext=>!_disposed && !IsUpdating && !HasBlockingError && SelectedPageIndex<PageCount-1;
    public string PreviewStatus=>IsUpdating ? "Обновляем вёрстку…" : IsPreviewLoading ? "Рисуем предварительный вид…" :
        HasBlockingError ? "Документ не готов: исправьте ошибку ниже" : Preview!=null ? "Предварительный вид · до 120 DPI" : "Предварительный вид";
    public string ApplyHint=>IsUpdating ? "Дождитесь проверки текущего исходника." : IsPreviewLoading ? "Дождитесь предварительного вида текущих страниц." : HasBlockingError ? "Исходник остаётся в редакторе; его можно выгрузить в .md." :
        CanApply ? $"Будет создано страниц: {PageCount}. Формулы оформляются, вычисления не выполняются." : "Введите Markdown и дождитесь вёрстки.";

    [ObservableProperty] private string source="";
    [ObservableProperty] private string title="Markdown";
    [ObservableProperty] private string fontFamily="Segoe UI";
    [ObservableProperty] private double fontSizePt=12;
    [ObservableProperty] private string color="#111827";
    [ObservableProperty] private double lineSpacing=1.15;
    [ObservableProperty] private double paragraphGapMm=2;
    [ObservableProperty] private double marginLeftMm=10;
    [ObservableProperty] private double marginTopMm=10;
    [ObservableProperty] private double marginRightMm=10;
    [ObservableProperty] private double marginBottomMm=10;
    [ObservableProperty] private string paperFormat="current";
    [ObservableProperty] private bool landscape;
    [ObservableProperty] private NotebookVariant notebookVariant=PrintMaster.Models.NotebookVariant.GridSingle;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApply),nameof(PreviewStatus),nameof(ApplyHint))] private bool isUpdating;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError),nameof(HasBlockingError),nameof(BlockingError),nameof(CanApply),nameof(PreviewStatus),nameof(ApplyHint))] private string error="";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasErrorLine))] private int? errorLine;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasDiagnostics))] private string diagnostics="";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPreviewError),nameof(HasBlockingError),nameof(BlockingError),nameof(CanApply),nameof(PreviewStatus),nameof(ApplyHint))] private string previewError="";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PreviewStatus),nameof(CanApply),nameof(ApplyHint))] private bool isPreviewLoading;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PreviewStatus),nameof(CanApply))] private BitmapSource? preview;
    [ObservableProperty] private int selectedPageIndex;
    [ObservableProperty] private string saveStatus="";

    partial void OnIsUpdatingChanged(bool value)=>NotifyNavigation();
    partial void OnErrorChanged(string value)=>NotifyNavigation();
    partial void OnPreviewErrorChanged(string value)=>NotifyNavigation();

    partial void OnSourceChanged(string value)=>ScheduleUpdate();
    partial void OnTitleChanged(string value)=>ScheduleUpdate();
    partial void OnFontFamilyChanged(string value)
    {OnPropertyChanged(nameof(FontWarning));OnPropertyChanged(nameof(HasFontWarning));ScheduleUpdate();}
    partial void OnFontSizePtChanged(double value)=>ScheduleUpdate();
    partial void OnColorChanged(string value)=>ScheduleUpdate();
    partial void OnLineSpacingChanged(double value)=>ScheduleUpdate();
    partial void OnParagraphGapMmChanged(double value)=>ScheduleUpdate();
    partial void OnMarginLeftMmChanged(double value)=>ScheduleUpdate();
    partial void OnMarginTopMmChanged(double value)=>ScheduleUpdate();
    partial void OnMarginRightMmChanged(double value)=>ScheduleUpdate();
    partial void OnMarginBottomMmChanged(double value)=>ScheduleUpdate();
    partial void OnLandscapeChanged(bool value)=>PaperChanged();
    partial void OnNotebookVariantChanged(NotebookVariant value)
    {
        if(PaperFormat=="Notebook")
        {
            _changingPaper=true; try {Landscape=NotebookProfiles.Default(value).IsSpread;} finally {_changingPaper=false;}
        }
        PaperChanged();
    }
    partial void OnPaperFormatChanged(string value)
    {
        if(value=="Notebook")
        {_changingPaper=true;try {Landscape=NotebookProfiles.Default(NotebookVariant).IsSpread;}finally {_changingPaper=false;}}
        PaperChanged();
    }
    private SheetPaper NotebookPaper()
    {
        var profile=_initialPaper.Notebook is {} original && original.Variant==NotebookVariant ? original : NotebookProfiles.Default(NotebookVariant);
        return profile.ToPaper(landscape:Landscape);
    }
    private void PaperChanged()
    {
        if(_changingPaper || _disposed) return;
        foreach(var property in new[] {nameof(Paper),nameof(PaperSummary),nameof(IsNotebookPaper),nameof(CanChangeNotebookVariant)}) OnPropertyChanged(property);
        ScheduleUpdate();
    }
    public Task RefreshAsync()
    {ScheduleUpdate(false);return UpdateTask;}
    private void ScheduleUpdate(bool debounce=true)
    {
        if(_disposed) return;
        _updateCancellation?.Cancel(); _updateCancellation?.Dispose(); _updateCancellation=new();
        _previewCancellation?.Cancel(); ++_previewEpoch; var revision=++_revision;
        IsUpdating=true;Error="";ErrorLine=null;Diagnostics="";PreviewError="";Preview=null;SaveStatus="";IsPreviewLoading=false;
        try
        {
            var candidate=DraftDocument; var paper=Paper; ValidateCandidate(candidate,paper);
            UpdateTask=UpdateAsync(candidate,paper,revision,debounce,_updateCancellation.Token);
        }
        catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or IOException or InvalidDataException)
        {Fail(ex);IsUpdating=false;UpdateTask=Task.CompletedTask;}
        OnPropertyChanged(nameof(Style));OnPropertyChanged(nameof(DraftDocument));OnPropertyChanged(nameof(CanApply));
    }
    private static void ValidateCandidate(MarkdownDocument candidate,SheetPaper paper)
    {
        ProjectFileService.ValidateMarkdownDocument(candidate);
        if(string.IsNullOrWhiteSpace(candidate.Source)) throw new ArgumentException("Введите текст Markdown или формулу.");
        if(candidate.Source.Length>TextLimits.MaxCharacters) throw new ArgumentException("Максимум — 1 000 000 символов Markdown.");
        if(string.IsNullOrWhiteSpace(candidate.Title) || candidate.Title.Length>512) throw new ArgumentException("Название документа: от 1 до 512 символов.");
        TextLayoutService.ValidateStyle(candidate.Style.Body);paper.Validate();
        if(!double.IsFinite(candidate.Style.ParagraphGapMm) || candidate.Style.ParagraphGapMm<0 || candidate.Style.ParagraphGapMm>100)
            throw new ArgumentException("Интервал абзацев должен быть от 0 до 100 мм.");
        var m=candidate.Style.Margins;
        if(new[] {m.LeftMm,m.TopMm,m.RightMm,m.BottomMm}.Any(v=>!double.IsFinite(v) || v<0 || v>1000))
            throw new ArgumentException("Поля должны быть конечными неотрицательными числами до 1000 мм.");
        var size=paper.OrientedSizeMm;
        if(m.LeftMm+m.RightMm>=size.Width || m.TopMm+m.BottomMm>=size.Height)
            throw new ArgumentException("Поля занимают весь лист. Уменьшите отступы или выберите другой формат.");
    }
    private async Task UpdateAsync(MarkdownDocument candidate,SheetPaper paper,long revision,bool debounce,CancellationToken token)
    {
        try
        {
            if(debounce) await Task.Delay(300,token);
            var preparedAndLayout=await Task.Run(async ()=>
            {
                var prepared=await _prepareAsync(candidate,token).ConfigureAwait(false);token.ThrowIfCancellationRequested();
                if(prepared.Document.Id!=candidate.Id || prepared.Document.Source!=candidate.Source || prepared.Document.Style!=candidate.Style || prepared.Document.Title!=candidate.Title)
                    throw new InvalidOperationException("Подготовка изображений изменила исходник или оформление документа.");
                var layout=await _layoutAsync(prepared.Document,paper,prepared.Assets,token).ConfigureAwait(false);token.ThrowIfCancellationRequested();
                if(layout.Pages.IsDefaultOrEmpty) throw new InvalidOperationException("Вёрстка не создала страниц. Проверьте исходник.");
                return (Prepared:prepared,Layout:layout);
            },token);
            if(_disposed || token.IsCancellationRequested || revision!=_revision) return;
            Result=preparedAndLayout.Prepared;_lastPrepared=preparedAndLayout.Prepared;Layout=preparedAndLayout.Layout;_validRevision=revision;
            Diagnostics=string.Join("\n",Layout.Diagnostics.Select(d=>d.Line is {} line ? $"Строка {line}: {d.Message}" : d.Message));
            Error="";ErrorLine=null;IsUpdating=false;
            var pageIndex=Math.Clamp(SelectedPageIndex,0,PageCount-1);
            if(SelectedPageIndex!=pageIndex) SelectedPageIndex=pageIndex;
            else {NotifyLayout();SchedulePreview();}
        }
        catch(OperationCanceledException) { }
        catch(Exception ex) {if(!_disposed && !token.IsCancellationRequested && revision==_revision) Fail(ex);}
        finally {if(!_disposed && revision==_revision) IsUpdating=false;}
    }
    private void Fail(Exception ex)
    {
        _previewCancellation?.Cancel();++_previewEpoch;IsPreviewLoading=false;
        Result=null;Layout=null;Preview=null;_validRevision=-1;Error=ex.Message;ErrorLine=ex is MarkdownLayoutException e ? e.Line : null;
        NotifyLayout();
    }
    private void NotifyNavigation()
    {
        OnPropertyChanged(nameof(CanGoPrevious));OnPropertyChanged(nameof(CanGoNext));
        PreviousPageCommand.NotifyCanExecuteChanged();NextPageCommand.NotifyCanExecuteChanged();
    }
    private void NotifyLayout()
    {
        foreach(var property in new[] {nameof(Result),nameof(Layout),nameof(PageCount),nameof(PageLabel),nameof(CanGoPrevious),nameof(CanGoNext),nameof(CanApply),nameof(ApplyHint)}) OnPropertyChanged(property);
        PreviousPageCommand.NotifyCanExecuteChanged();NextPageCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedPageIndexChanged(int value)
    {
        var next=Math.Clamp(value,0,Math.Max(0,PageCount-1));
        if(next!=value) {SelectedPageIndex=next;return;}
        NotifyLayout();SchedulePreview();
    }
    [RelayCommand(CanExecute=nameof(CanGoPrevious))] private void PreviousPage()=>SelectedPageIndex--;
    [RelayCommand(CanExecute=nameof(CanGoNext))] private void NextPage()=>SelectedPageIndex++;
    private void SchedulePreview()
    {
        _previewCancellation?.Cancel();_previewCancellation?.Dispose();_previewCancellation=new();var epoch=++_previewEpoch;
        if(!_renderPreview || _disposed || IsUpdating || HasError || Layout==null || PageCount==0) {IsPreviewLoading=false;PreviewTask=Task.CompletedTask;return;}
        IsPreviewLoading=true;PreviewError="";PreviewTask=UpdatePreviewAsync(Layout.Pages[SelectedPageIndex],epoch,_previewCancellation.Token);
    }
    private async Task UpdatePreviewAsync(MarkdownRenderPage page,long epoch,CancellationToken token)
    {
        try
        {
            var image=await Task.Run(()=>_previewRenderer(page,token),token);
            if(!_disposed && !token.IsCancellationRequested && epoch==_previewEpoch) Preview=image;
        }
        catch(OperationCanceledException) { }
        catch(Exception ex)
        {if(!_disposed && !token.IsCancellationRequested && epoch==_previewEpoch) {Preview=null;PreviewError="Не удалось показать предварительный вид: "+ex.Message;}}
        finally {if(!_disposed && epoch==_previewEpoch) IsPreviewLoading=false;}
    }
    private static BitmapSource RenderPreview(MarkdownRenderPage page,CancellationToken token)
    {
        using var image=MarkdownDrawingRenderer.Render(page,120,token,1000);token.ThrowIfCancellationRequested();return BitmapBridge.ToWpf(image);
    }
    public void Dispose()
    {
        if(_disposed) return;
        _disposed=true;++_revision;++_previewEpoch;_updateCancellation?.Cancel();_previewCancellation?.Cancel();
        _updateCancellation?.Dispose();_previewCancellation?.Dispose();_updateCancellation=null;_previewCancellation=null;
        IsUpdating=false;IsPreviewLoading=false;NotifyLayout();
    }
}
