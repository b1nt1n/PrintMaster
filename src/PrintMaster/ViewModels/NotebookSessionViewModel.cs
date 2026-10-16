using CommunityToolkit.Mvvm.Input;
using PrintMaster.Models;
using PrintMaster.Services;
using System.Collections.Immutable;

namespace PrintMaster.ViewModels;

public partial class PrintSessionViewModel
{
    private readonly SemaphoreSlim _preferencesWriteGate=new(1,1);
    public IReadOnlyList<Choice<NotebookVariant>> NotebookChoices=>NotebookProfiles.Choices;
    public NotebookVariant? NotebookVariant
    {
        get=>LayoutSettings.Notebook?.Variant;
        set
        {
            if(value==null || !CanEdit || value==NotebookVariant) return;
            SelectedNotebookProfile=_preferences.NotebookProfiles.FirstOrDefault(p=>p.Variant==value) ?? NotebookProfiles.Default(value.Value);
        }
    }
    public NotebookProfile? SelectedNotebookProfile
    {
        get=>LayoutSettings.Notebook;
        set
        {
            if(value==null || !CanEdit || value==LayoutSettings.Notebook) return;
            FinishTextEditing(); ApplyNotebookProfile(value,value.IsSpread);
        }
    }
    public IReadOnlyList<NotebookProfile> NotebookProfileChoices
    {
        get
        {
            var current=SelectedNotebookProfile;
            var values=_preferences.NotebookProfiles.Where(p=>p.Variant==(current?.Variant ?? Models.NotebookVariant.GridSingle));
            if(current!=null) values=values.Where(p=>p.Id!=current.Id).Prepend(current);
            return values.ToArray();
        }
    }
    public string NotebookProfileSummary=>SelectedNotebookProfile is {} p
        ? $"{p.WidthMm:0.###} × {p.HeightMm:0.###} мм · {(p.IsGrid ? $"клетка {p.GridWidthMm:g} × {p.GridHeightMm:g}" : $"строки {p.LineStepMm:g}")} мм"
        : "Старый формат без разлиновки. Выберите вид тетрадного листа.";
    public string FlowTextLabel=>SelectedPage?.Document.Paper.Notebook!=null ? "Текст на тетрадном листе" : "Текст на страницах";
    public string NotebookOverflowMessage
    {
        get
        {
            if(SelectedPage==null) return "";
            var remainder=NotebookTextLayout.Remainder(SelectedPage.Document.Snapshot());
            return remainder.Length==0 ? "" : $"Не помещается {RussianQuantity.Format(remainder.Length,"символ","символа","символов")}. Исходный текст сохранен целиком. Перед печатью потребуется отдельное подтверждение.";
        }
    }
    private void NotifyNotebookProperties()
    {
        foreach(var name in new[] { nameof(NotebookVariant),nameof(SelectedNotebookProfile),nameof(NotebookProfileChoices),nameof(NotebookProfileSummary),nameof(FlowTextLabel),nameof(NotebookOverflowMessage) }) OnPropertyChanged(name);
        EditNotebookProfileCommand.NotifyCanExecuteChanged(); CopyNotebookProfileCommand.NotifyCanExecuteChanged();
        ShowPaperGuideCommand.NotifyCanExecuteChanged();
    }
    private void ApplyNotebookProfile(NotebookProfile profile,bool landscape)
    {
        try
        {
            profile.Validate(); var paper=profile.ToPaper(SelectedPage?.Document.Paper.Margins,landscape);
            using var history=BeginHistoryEdit("Профиль тетрадного листа");
            if(!ReflowSelectedPaper(paper)) SelectedPage!.Document.SetPaper(paper);
            SyncLayoutToSheet(); NotifySheetMode(); NotifyNotebookProperties(); _=ProbePrinterAsync();
        }
        catch(Exception ex) when(ex is ArgumentException or InvalidOperationException) { Status=ex.Message; SyncLayoutToSheet(); }
    }
    private bool CanEditNotebookProfile()=>CanEdit && IsNotebookLayout;
    [RelayCommand(CanExecute=nameof(CanEditNotebookProfile))]
    private Task EditNotebookProfileAsync()=>EditNotebookProfileCoreAsync(false);
    [RelayCommand(CanExecute=nameof(CanEditNotebookProfile))]
    private Task CopyNotebookProfileAsync()=>EditNotebookProfileCoreAsync(true);
    private async Task EditNotebookProfileCoreAsync(bool copy)
    {
        var current=SelectedNotebookProfile ?? NotebookProfiles.Default(Models.NotebookVariant.GridSingle);
        var result=_dialogs.EditNotebookProfile(current,copy || NotebookProfiles.Defaults.Any(p=>p.Id==current.Id));
        if(result==null) return;
        FinishTextEditing(); ApplyNotebookProfile(result,result.IsSpread);
        await RememberNotebookProfileAsync(result);
    }
    private async Task RememberNotebookProfileAsync(NotebookProfile profile)
    {
        if(_preferences.NotebookProfiles.Any(p=>p==profile)) return;
        _preferences=_preferences with { NotebookProfiles=_preferences.NotebookProfiles.Where(p=>p.Id!=profile.Id).Append(profile).ToImmutableArray() };
        Settings.AvailableNotebookProfiles=LayoutSettings.AvailableNotebookProfiles=_preferences.NotebookProfiles;
        NotifyNotebookProperties();
        try
        {
            await _preferencesWriteGate.WaitAsync(_lifetime.Token);
            try { await _preferencesStore.SaveAsync(_preferences,_lifetime.Token); }
            finally { _preferencesWriteGate.Release(); }
        }
        catch(OperationCanceledException) { }
        catch(Exception ex) { Status="Профиль сохранен в листе; настройки приложения не записаны: "+ex.Message; }
    }
    internal void SetNotebookStory(PrintPageItem page,string source,TextStyle style,Guid? story=null,SheetPaper? paper=null)
    {
        var target=paper ?? page.Document.Paper; var previous=page.Document.Objects.OfType<TextObject>().Where(t=>t.Flow?.Notebook==true).ToArray();
        var value=new NotebookTextStory(story ?? page.Document.NotebookText?.Id ?? Guid.NewGuid(),source,style with { FixedLineHeightMm=null });
        var result=NotebookTextLayout.Layout(target,value,TextFlowSafe(target),previous);
        page.Document.SetNotebookText(value,result.Objects,target); NotifyNotebookProperties();
    }
}
