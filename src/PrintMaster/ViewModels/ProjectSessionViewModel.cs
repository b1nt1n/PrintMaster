using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrintMaster.Models;
using PrintMaster.Services;
using System.Collections.Immutable;
using System.ComponentModel;
using System.IO;

namespace PrintMaster.ViewModels;

public partial class PrintSessionViewModel
{
    private readonly ProjectFileService _projectFiles=new();
    private ProjectRecoveryStore _recovery=null!;
    private CancellationTokenSource? _autosaveDelay;
    private Task _autosaveTask=Task.CompletedTask;
    private long _projectRevision;
    private bool _restoringProject,_closePrepared;
    private readonly SemaphoreSlim _closeGate=new(1,1);

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(WindowTitle),nameof(ProjectSummary))] private string? currentProjectPath;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(WindowTitle),nameof(ProjectSummary))] private bool hasUnsavedChanges;
    [ObservableProperty] private bool includeProjectSources=true;
    [ObservableProperty] private string projectMessage="Новый проект";
    public string WindowTitle=>"PrintMaster — "+(CurrentProjectPath==null ? "новый проект" : Path.GetFileName(CurrentProjectPath))+(HasUnsavedChanges ? " *" : "");
    public string ProjectSummary=>(CurrentProjectPath==null ? "Новый проект" : Path.GetFileName(CurrentProjectPath))+(HasUnsavedChanges ? " · есть изменения" : "");
    internal string RecoveryFilePath=>_recovery.RecoveryPath;
    internal bool IsClosePrepared=>_closePrepared;

    private void InitializeProjectState(string? recoveryDirectory)
    {
        _recovery=new(_projectFiles,recoveryDirectory); PropertyChanged+=ProjectPropertyChanged;
    }
    private void ProjectPropertyChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName is nameof(InkColor) or nameof(StrokeThickness) or nameof(TextSize) or nameof(CropRatio) or nameof(ExportDpi)
            or nameof(BrushOpacity) or nameof(MarkerThickness) or nameof(MarkerOpacity) or nameof(EraserDiameter)) MarkProjectChanged();
    }
    private void MarkProjectChanged()
    {
        if(_restoringProject || _disposed) return;
        _projectRevision++; RefreshHistoryDirty(); _closePrepared=false; ScheduleAutosave();
    }
    internal ProjectSession CaptureProject()=>new(ProjectPrintSettings.Capture(Settings),new(SelectedPage?.Id,Zoom,AutoFitZoom,ShowPrintPreview,
        InkColor,StrokeThickness,TextSize,CropRatio,ExportDpi,_defaultTextStyle) {
            BrushOpacity=BrushOpacity,MarkerThickness=MarkerThickness,MarkerOpacity=MarkerOpacity,EraserDiameter=EraserDiameter
        },Pages.Select(p=>new ProjectPage(p.Document.Snapshot(),p.IsIncluded,p.Copies) {PrintOptions=p.PrintOptions}).ToImmutableArray())
        {MarkdownDocuments=ProjectFileService.CollectMarkdownDocuments(Pages.Select(p=>new ProjectPage(p.Document.Snapshot(),p.IsIncluded,p.Copies) {PrintOptions=p.PrintOptions}))};

    private void ScheduleAutosave()
    {
        if(_restoringProject || _disposed || _recovery==null) return;
        _autosaveDelay?.Cancel(); _autosaveDelay?.Dispose(); _autosaveDelay=new();
        if(!_preferences.AutosaveEnabled) return;
        var token=_autosaveDelay.Token; var snapshot=CaptureProject(); var revision=_projectRevision;
        _autosaveTask=AutosaveAsync(snapshot,revision,token);
    }
    private async Task AutosaveAsync(ProjectSession snapshot,long revision,CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_preferences.AutosaveDelaySeconds),token); await _recovery.WriteAsync(snapshot,token);
            if(!_disposed && revision==_projectRevision) ProjectMessage="Автосохранение готово";
        }
        catch(OperationCanceledException) { }
        catch(Exception ex) { if(!_disposed) ProjectMessage="Автосохранение: "+ex.Message; }
    }
    internal async Task FlushRecoveryAsync()
    {
        _autosaveDelay?.Cancel(); await _autosaveTask;
        if(Pages.Count!=0 && _preferences.AutosaveEnabled) { await _recovery.WriteAsync(CaptureProject(),default); ProjectMessage="Рабочая сессия сохранена для восстановления"; }
        else if(Pages.Count==0 && File.Exists(_recovery.RecoveryPath)) File.Delete(_recovery.RecoveryPath);
    }
    public async Task PrepareCloseAsync()
    {
        await _closeGate.WaitAsync();
        try
        {
            if(_closePrepared) return;
            FinishTextEditing();
            if(IsBusy) { CancelCommand.Execute(null); while(IsBusy) await Task.Delay(100); }
            try { await FlushRecoveryAsync(); _closePrepared=true; }
            catch(Exception ex) { _dialogs.Error("Не удалось сохранить рабочую сессию перед закрытием: "+ex.Message); throw; }
        }
        finally { _closeGate.Release(); }
    }

    [RelayCommand(CanExecute=nameof(IsIdle))] private async Task SaveProjectAsync()
    {
        if(!CommitInputForAction()) return;
        var path=CurrentProjectPath ?? _dialogs.PickProjectToSave("Проект"+ProjectFileService.Extension);
        if(path!=null) await SaveProjectToAsync(path);
    }
    [RelayCommand(CanExecute=nameof(IsIdle))] private async Task SaveProjectAsAsync()
    {
        if(!CommitInputForAction()) return;
        var path=_dialogs.PickProjectToSave(CurrentProjectPath==null ? "Проект"+ProjectFileService.Extension : Path.GetFileName(CurrentProjectPath));
        if(path!=null) await SaveProjectToAsync(path);
    }
    internal async Task<bool> SaveProjectToAsync(string path)
    {
        if(IsBusy || !CommitInputForAction()) return false; FinishTextEditing(); var snapshot=CaptureProject(); var revision=_projectRevision; BeginOperation(SessionOperationKind.SaveProject);
        try
        {
            Status="Сохранение проекта…"; await _projectFiles.SaveAsync(snapshot,path,IncludeProjectSources,_operation!.Token);
            CurrentProjectPath=Path.GetFullPath(path);
            if(revision==_projectRevision) { SaveHistoryCheckpoint(); HasUnsavedChanges=false; }
            var missing=Pages.Sum(p=>p.Document.Snapshot().Assets.Count(a=>string.IsNullOrEmpty(a.WorkingPath) || !File.Exists(a.WorkingPath)));
            Status=missing==0 ? "Проект сохранен: "+CurrentProjectPath : $"Проект сохранен. Не найдены материалы: {RussianQuantity.Format(missing,"исходник","исходника","исходников")}.";
            ProjectMessage=missing>0 ? "Есть отсутствующие материалы" : IncludeProjectSources ? "Материалы включены в проект" : "Ссылки на оригиналы сохранены";
            await RememberRecentProjectAsync(CurrentProjectPath); return true;
        }
        catch(OperationCanceledException) { Status="Сохранение проекта отменено. Предыдущий файл сохранен."; return false; }
        catch(Exception ex) { ShowOperationFailure("Ошибка сохранения проекта","Выберите доступную папку и проверьте свободное место. Предыдущий файл сохраняется при неудачной записи.",ex); return false; }
        finally { EndOperation(); }
    }

    [RelayCommand(CanExecute=nameof(IsIdle))] private async Task OpenProjectAsync()
    {
        var path=_dialogs.PickProjectToOpen(); if(path!=null) await OpenProjectFromAsync(path);
    }
    internal async Task<bool> OpenProjectFromAsync(string path,bool recovery=false)
    {
        if(IsBusy || !CommitInputForAction()) return false;
        FinishTextEditing();
        if(HasUnsavedChanges && !recovery)
        {
            var choice=_dialogs.ConfirmProjectReplacement(); if(choice==UnsavedProjectChoice.Cancel) return false;
            if(choice==UnsavedProjectChoice.Save)
            {
                var target=CurrentProjectPath ?? _dialogs.PickProjectToSave("Проект"+ProjectFileService.Extension);
                if(target==null || !await SaveProjectToAsync(target)) return false;
            }
        }
        BeginOperation(SessionOperationKind.OpenProject);
        try
        {
            Status="Открытие проекта…"; var loaded=await _projectFiles.LoadAsync(path,_workspace,_operation!.Token);
            // Construct every model before touching the current queue.
            var pages=loaded.Session.Pages.Select(p=>new PrintPageItem(SheetDocument.Restore(p.Sheet)) { IsIncluded=p.IsIncluded,Copies=p.Copies,PrintOptions=p.PrintOptions }).ToArray();
            ApplyLoadedProject(loaded,pages,recovery); Status=loaded.Missing.Length==0 ? "Проект открыт: "+Path.GetFileName(path)
                : $"Проект открыт. Не найдены материалы: {RussianQuantity.Format(loaded.Missing.Length,"исходник","исходника","исходников")} — используйте «Переподключить исходники».";
            if(!recovery) await RememberRecentProjectAsync(loaded.Path);
            return true;
        }
        catch(OperationCanceledException) { Status="Открытие отменено. Текущая очередь сохранена."; return false; }
        catch(Exception ex) { ShowOperationFailure("Не удалось открыть проект","Проверьте файл проекта или выберите его копию. Текущая очередь сохранена.",ex); return false; }
        finally { EndOperation(); }
    }
    private void ApplyLoadedProject(LoadedProject loaded,IReadOnlyList<PrintPageItem> pages,bool recovery)
    {
        _restoringProject=true; _autosaveDelay?.Cancel();
        try
        {
            _previewCancellation?.Cancel(); SelectedPage=null;
            foreach(var page in Pages) { page.PropertyChanged-=PageChanged; page.Edits.PropertyChanged-=EditsChanged; }
            Pages.Clear(); _thumbnailLru.Clear();
            loaded.Session.Print.Apply(Settings);
            foreach(var page in pages) AddPage(page);
            var editor=loaded.Session.Editor; AutoFitZoom=editor.AutoFitZoom; Zoom=editor.Zoom; ShowPrintPreview=editor.ShowPrintPreview;
            InkColor=editor.InkColor; StrokeThickness=editor.StrokeThickness; TextSize=editor.TextSize; CropRatio=editor.CropRatio; ExportDpi=editor.ExportDpi;
            BrushOpacity=editor.BrushOpacity; MarkerThickness=editor.MarkerThickness; MarkerOpacity=editor.MarkerOpacity; EraserDiameter=editor.EraserDiameter;
            _defaultTextStyle=editor.SheetTextStyle ?? new(FontSizePt:editor.TextSize*0.75,Color:editor.InkColor);
            SelectedPage=Pages.FirstOrDefault(p=>p.Id==editor.SelectedPageId) ?? Pages.FirstOrDefault();
            NormalizeLoadedTextFlows();
            SelectedPage=Pages.FirstOrDefault(p=>p.Id==editor.SelectedPageId) ?? Pages.FirstOrDefault();
            CurrentProjectPath=recovery ? null : loaded.Path; HasUnsavedChanges=recovery; _projectRevision++; _closePrepared=false;
            ProjectMessage=recovery ? "Восстановлена рабочая сессия; сохраните проект" : "Проект восстановлен для редактирования";
            ResetQueueHistory();
            if(recovery) { _historySaved=null; _historySavedOptions=null; } else SaveHistoryCheckpoint();
        }
        finally { _restoringProject=false; }
        NotifyQueue(); NotifySettingsDerived(); SyncLayoutToSheet();
        foreach(var property in new[] { nameof(CanvasWidth),nameof(CanvasHeight),nameof(CanvasSafeArea),nameof(PaperSummary),nameof(MonochromeHelp),nameof(IsTextMode),nameof(HasThresholdWarning),nameof(WindowTitle) }) OnPropertyChanged(property);
        _=ProbePrinterAsync(); _=LoadEditorAsync(SelectedPage); ScheduleAutosave();
    }

    [RelayCommand(CanExecute=nameof(IsIdle))] private async Task RelinkSourcesAsync()
    {
        var replacements=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var page in Pages)
        foreach(var asset in page.Document.Snapshot().Assets.Where(a=>string.IsNullOrEmpty(a.WorkingPath) || !File.Exists(a.WorkingPath)))
        {
            var key=asset.OriginalPath ?? asset.Id.ToString();
            if(!replacements.TryGetValue(key,out var path))
            {
                path=_dialogs.PickMissingSource(new(page.Id,asset.Id,asset.DisplayName,asset.OriginalPath,key,asset.Kind));
                if(path==null) continue; replacements[key]=path;
            }
            BeginOperation(SessionOperationKind.Relink);
            try
            {
                var restored=await _projectFiles.RelinkAsync(asset,path,_workspace,_operation!.Token); page.Document.ReplaceAsset(restored);
                Status="Исходник переподключен: "+asset.DisplayName;
            }
            catch(OperationCanceledException) { Status="Переподключение отменено."; return; }
            catch(Exception ex) { ShowOperationFailure("Не удалось переподключить "+asset.DisplayName,"Выберите исходник подходящего формата с прежним числом страниц или другим корректным изображением.",ex); }
            finally { EndOperation(); }
        }
        NotifyQueue(); _=LoadEditorAsync(SelectedPage);
    }

    [RelayCommand(CanExecute=nameof(IsIdle))] private async Task RecoverSessionAsync()=>await OfferRecoveryAsync();
    internal async Task OfferRecoveryAsync()
    {
        var path=_recovery.FindLatest(); if(path==null) return;
        if(!_dialogs.ConfirmRecovery(path)) { _recovery.Dismiss(path); return; }
        if(await OpenProjectFromAsync(path,true)) { await FlushRecoveryAsync(); _recovery.Dismiss(path); }
    }
}
