using CommunityToolkit.Mvvm.Input;
using PrintMaster.Models;
using PrintMaster.Services;
using System.Collections.Immutable;

namespace PrintMaster.ViewModels;

public partial class PrintSessionViewModel
{
    private PaperPrintWorkflow? _paperWorkflow;
    private PaperPrintWorkflow PaperWorkflow=>_paperWorkflow ??=new(_engine,_dialogs,LookupPaperCalibration,
        s=>_preferences.HiddenPaperGuides.Contains(ManualDuplexPlanner.GuideKey(s)),RememberPaperGuideAsync);
    public bool HasPendingPaperJob=>_paperWorkflow?.HasPending==true;
    public string PendingPaperMessage=>HasPendingPaperJob ? "Задание ожидает загрузки или переворота бумаги. Продолжение отправит только оставшуюся часть. Ожидание сохраняется, пока приложение открыто." : "";
    private PrinterPaperCalibration? LookupPaperCalibration(PrintSettingsSnapshot settings)=>_preferences.PaperCalibrations.FirstOrDefault(c=>
        string.Equals(c.PrinterName,settings.PrinterName,StringComparison.OrdinalIgnoreCase) && c.PaperSizeMm==settings.PaperSizeMm && c.Landscape==settings.Landscape);
    private async Task RememberPaperGuideAsync(PaperGuideViewModel model)
    {
        if(model.Calibration is not {} value) return;
        var hidden=_preferences.HiddenPaperGuides;
        if(model.CanHide)
        {
            var key=ManualDuplexPlanner.GuideKey(model.Settings); hidden=hidden.Where(k=>k!=key).ToImmutableArray();
            if(model.HideAgain) hidden=hidden.Add(key);
        }
        _preferences=_preferences with { PaperCalibrations=_preferences.PaperCalibrations.Where(c=>
            !string.Equals(c.PrinterName,value.PrinterName,StringComparison.OrdinalIgnoreCase) || c.PaperSizeMm!=value.PaperSizeMm || c.Landscape!=value.Landscape).Append(value).ToImmutableArray(),HiddenPaperGuides=hidden };
        await _preferencesWriteGate.WaitAsync(_lifetime.Token);
        try { await _preferencesStore.SaveAsync(_preferences,_lifetime.Token); }
        finally { _preferencesWriteGate.Release(); }
    }
    private void NotifyPaperJob()
    {
        foreach(var name in new[] { nameof(HasPendingPaperJob),nameof(PendingPaperMessage),nameof(PrintLabel) }) OnPropertyChanged(name);
        ResumePaperJobCommand.NotifyCanExecuteChanged(); DiscardPaperJobCommand.NotifyCanExecuteChanged(); ShowPaperGuideCommand.NotifyCanExecuteChanged(); PrintCommand.NotifyCanExecuteChanged();
    }
    private bool CanResumePaperJob()=>IsIdle && HasPendingPaperJob;
    [RelayCommand(CanExecute=nameof(CanResumePaperJob))]
    private async Task ResumePaperJobAsync()
    {
        BeginOperation(SessionOperationKind.Print);
        try
        {
            var progress=PrintOperationProgress();
            var done=await PaperWorkflow.ContinueAsync(progress,_operation!.Token);
            Status=done ? "Задание передано принтеру. Дождитесь физического выхода всех листов." : "Печать ожидает загрузки бумаги или переворота. Можно продолжить позже.";
        }
        catch(OperationCanceledException) { Status="Проход отменен. Уже отправленные листы могут выйти из принтера; автоматического повтора нет."; }
        catch(Exception ex) { ShowOperationFailure("Ошибка продолжения печати","Проверьте принтер и загрузку бумаги. Уже отправленные листы не повторяются автоматически.",ex); }
        finally { EndOperation(); NotifyPaperJob(); }
    }
    [RelayCommand(CanExecute=nameof(CanResumePaperJob))]
    private void DiscardPaperJob()
    { PaperWorkflow.Discard(); Status="Ожидающее продолжение закрыто. Уже отправленные принтеру листы остаются в его очереди."; NotifyPaperJob(); }
    private bool CanShowPaperGuide()=>IsIdle && SelectedPage!=null;
    [RelayCommand(CanExecute=nameof(CanShowPaperGuide))]
    private async Task ShowPaperGuideAsync()
    {
        var draft=new PrintSettings(); ProjectPrintSettings.Capture(Settings).Apply(draft);
        try
        {
            var page=SelectedPage!.Snapshot();
            var settings=ManualDuplexPlanner.ForPage(page,draft.Snapshot());
            var model=new PaperGuideViewModel(settings,SelectedPage.Copies,PaperGuideStep.Preview,LookupPaperCalibration(settings));
            if(_dialogs.ShowPaperGuide(model)) await RememberPaperGuideAsync(model);
        }
        catch(Exception ex) { Status=ex.Message; }
    }
}
