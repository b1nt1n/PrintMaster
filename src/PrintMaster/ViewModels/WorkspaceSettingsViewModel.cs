using CommunityToolkit.Mvvm.Input;
using PrintMaster.Models;
using PrintMaster.Services;
using System.ComponentModel;

namespace PrintMaster.ViewModels;

public partial class PrintSessionViewModel
{
    private IApplicationPreferencesStore _preferencesStore=new MemoryApplicationPreferencesStore();
    private ApplicationPreferences _preferences=new();
    private bool _syncingLayout,_applyingPrintSettings;
    public PrintSettings LayoutSettings { get; }=new();
    public bool IsCustomLayout=>LayoutSettings.IsCustom;
    public bool IsNotebookLayout=>LayoutSettings.IsNotebook;
    public bool HasMultipleHighlightedPages=>HighlightedPageCount>1;
    public bool ShowObjectGeometry=>HasSheetObject && !IsFlowText && Tool==EditorTool.Select && !IsEditingText && AnnotationPresentation==null;
    public bool ShowNewDrawingProperties=>ShowEditorTools && !ShowTextFormatting &&
        (Tool is EditorTool.Arrow or EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.Brush or EditorTool.Marker);
    public string InspectorTitle=>IsEditingText ? "Редактирование текста" : ShowTextFormatting ? "Текст" : ShowSelectedDrawingProperties ? SelectedDrawingObjects.Count>1 ? "Несколько объектов" : SelectedDrawingAnnotation is { Kind:AnnotationKind.Ink or AnnotationKind.Marker } || SelectedSheetObject is InkObject ? "Штрих" : "Фигура" : ShowNewDrawingProperties ? Tool switch { EditorTool.Brush=>"Новая кисть",EditorTool.Marker=>"Новый маркер",_=>"Новая фигура" } : IsSheetSelected ?
        SelectedObjectIds.Length>1 ? "Несколько объектов" : HasSheetObject ? SheetObjectSummary : "Лист" :
        IsDocumentSelected ? "Документ · просмотр" : IsImageSelected ? "Изображение" : "Свойства";
    public string CanvasModeLabel=>!HasSelection ? "Выберите страницу" : ShowPrintPreview ? "Печатный вид" : IsDocumentSelected ? "Просмотр документа" : "Редактирование листа";
    public string PrintAvailability=>PrintActionAvailability;

    private async Task InitializeSessionAsync()
    {
        await InitializeWorkspaceAsync();
        try
        {
            _preferences=await _preferencesStore.LoadAsync(_lifetime.Token);
            Settings.AvailableNotebookProfiles=LayoutSettings.AvailableNotebookProfiles=_preferences.NotebookProfiles;
            _converter.ExecutablePath=_preferences.LibreOfficePath;
            _applyingPrintSettings=true;
            Settings.PrinterName=_preferences.PrinterName; Settings.Dpi=_preferences.Dpi;
            Settings.Monochrome=_preferences.Monochrome; Settings.Threshold=_preferences.Threshold;
        }
        catch(OperationCanceledException) { return; }
        catch(Exception ex) { Status="Настройки не прочитаны: "+ex.Message+". Используются начальные значения."; }
        finally { _applyingPrintSettings=false; }
        await RefreshPrintersAsync();
    }
    private void SyncLayoutToSheet()
    {
        if(_syncingLayout) return;
        var previous=LayoutSettings.OrientedPaperMm;
        _syncingLayout=true;
        try
        {
            var paper=IsSheetSelected ? SelectedPage!.Document.Paper : new SheetPaper(Settings.PaperSizeMm,Settings.Landscape,SheetMargins.SafeDefault);
            PrintReviewViewModel.SetPaper(LayoutSettings,paper);
            LayoutSettings.PrinterName=Settings.PrinterName;
        }
        finally { _syncingLayout=false; }
        NotifyInspector();
        if(previous!=LayoutSettings.OrientedPaperMm && !_disposed && !_restoringProject) _=ProbePrinterAsync();
    }
    private void LayoutChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(_syncingLayout || _restoringProject || _applyingHistory) return;
        if(e.PropertyName is not (nameof(PrintSettings.Paper) or nameof(PrintSettings.CustomWidthMm) or nameof(PrintSettings.CustomHeightMm) or nameof(PrintSettings.Landscape))) return;
        if(!IsSheetSelected) return;
        if(!CanEdit) { SyncLayoutToSheet(); return; }
        try
        {
            if(LayoutSettings.IsNotebook && LayoutSettings.Notebook==null)
            {
                _syncingLayout=true;
                try { LayoutSettings.Notebook=NotebookProfiles.Default(Models.NotebookVariant.GridSingle); LayoutSettings.Landscape=false; }
                finally { _syncingLayout=false; }
            }
            var paper=new SheetPaper(LayoutSettings.PaperSizeMm,LayoutSettings.Landscape,SelectedPage!.Document.Paper.Margins)
                { Notebook=LayoutSettings.IsNotebook ? LayoutSettings.Notebook : null };
            var model=new NewSheetViewModel(LayoutSettings); if(!model.CanCreate) throw new ArgumentException(model.Error);
            FinishTextEditing();
            using var history=BeginHistoryEdit("Формат листа");
            if(!ReflowSelectedPaper(paper)) SelectedPage!.Document.SetPaper(paper);
            Status="Формат листа изменен. Ctrl+Z — отменить.";
        }
        catch(Exception ex) when(ex is ArgumentException or InvalidOperationException) { Status=ex.Message; SyncLayoutToSheet(); }
        NotifySheetMode(); NotifyInspector();
    }
    private void NotifyInspector()
    {
        ExportPagesCommand.NotifyCanExecuteChanged();
        foreach(var name in new[] { nameof(InspectorTitle),nameof(CanvasModeLabel),nameof(PrintAvailability),nameof(IsCustomLayout),nameof(IsNotebookLayout),nameof(ShowObjectGeometry),nameof(ShowNewDrawingProperties),nameof(HasMultipleHighlightedPages) }) OnPropertyChanged(name);
        NotifyDrawingStyleProperties();
        NotifyNotebookProperties();
    }
    [RelayCommand(CanExecute=nameof(IsIdle))]
    private async Task ApplicationSettingsAsync()
    {
        var draft=new ApplicationSettingsViewModel(_preferences,Printers.ToArray(),_dialogs);
        if(!_dialogs.EditApplicationSettings(draft) || draft.Result==null) return;
        BeginOperation(SessionOperationKind.SavePreferences);
        try
        {
            await _preferencesStore.SaveAsync(draft.Result,_operation!.Token);
            _preferences=draft.Result; _converter.ExecutablePath=_preferences.LibreOfficePath;
            ScheduleAutosave(); Status="Настройки сохранены. Параметры по умолчанию применятся к новым сеансам.";
            if(!_preferences.AutosaveEnabled) ProjectMessage="Автосохранение выключено";
        }
        catch(OperationCanceledException) { Status="Сохранение настроек отменено."; }
        catch(Exception ex) { ShowOperationFailure("Не удалось сохранить настройки","Проверьте доступ к папке настроек и выбранный путь LibreOffice. Исправьте параметры и сохраните снова.",ex); }
        finally { EndOperation(); }
    }
}
