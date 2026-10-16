using CommunityToolkit.Mvvm.Input;
using PrintMaster.Models;
using PrintMaster.Services;
namespace PrintMaster.ViewModels;
public partial class PrintSessionViewModel
{
    private PrintSettingsSnapshot DisplayDefaults=>new(Settings.PrinterName,Settings.Paper,Settings.PaperSizeMm,Settings.Landscape,
        Settings.Scaling,Settings.Monochrome,Settings.Dpi,Settings.Threshold,5,null,true,Settings.IsNotebook ? Settings.Notebook : null);
    internal PrintSettingsSnapshot EffectiveSettings(PrintPageItem? page)=>page==null ? DisplayDefaults : ManualDuplexPlanner.ForPage(page.Snapshot(),DisplayDefaults);
    public string PageOptionsSummary=>SelectedPage?.PrintOptions==null ? "Параметры задания" :
        $"Свои: {EffectiveSettings(SelectedPage).OrientedPaperMm.Width:g} × {EffectiveSettings(SelectedPage).OrientedPaperMm.Height:g} мм · {EffectiveSettings(SelectedPage).Dpi} DPI · {EffectiveSettings(SelectedPage).Monochrome switch {MonochromeMode.Text=>"Текст",MonochromeMode.Photo=>"Фото",_=>"Авто"}}";
    private void InitializePageOptionsNotifications()
    {PropertyChanged+=(_,e)=>{if(e.PropertyName is nameof(SelectedPage) or nameof(IsIdle) or nameof(HighlightedSummary)) {OnPropertyChanged(nameof(PageOptionsSummary));EditPageOptionsCommand.NotifyCanExecuteChanged();}};}
    private bool CanEditPageOptions()=>IsIdle && SelectedPage!=null;
    [RelayCommand(CanExecute=nameof(CanEditPageOptions))]
    private void EditPageOptions(string? target)
    {
        FinishTextEditing();
        var pages=target=="selection" && HighlightedPages.Count!=0 ? HighlightedPages.ToArray() : SelectedPage is {} page ? [page] : Array.Empty<PrintPageItem>();
        if(pages.Length==0)return;
        var snapshots=pages.Select(p=>(Page:p,Sheet:p.Document.Snapshot(),p.PrintOptions)).ToArray();
        var defaults=DisplayDefaults with {PrinterName=string.IsNullOrWhiteSpace(Settings.PrinterName) ? "PrintMaster" : Settings.PrinterName};
        var model=new PageOptionsEditorViewModel(pages.Select(p=>p.Snapshot()).ToArray(),defaults,Settings.AvailableNotebookProfiles);
        if(!_dialogs.EditPageOptions(model))return;
        if(snapshots.Any(p=>!Pages.Contains(p.Page) || p.Page.Document.Snapshot()!=p.Sheet || p.Page.PrintOptions!=p.PrintOptions))
        {Status="Страницы изменились. Откройте параметры снова.";return;}
        try{ApplyPageOptions(pages,model.Result(),preserveLayout:!model.CanChangePaper);}
        catch(ArgumentException ex){Status=ex.Message;}
    }
    internal void ApplyPageOptions(IReadOnlyList<PrintPageItem> pages,PagePrintOptions? options,bool preserveLayout=false)
    {
        options?.Validate();
        if(pages.Count==0 || pages.Any(p=>!Pages.Contains(p)))throw new ArgumentException("Выберите страницы текущей очереди.");
        var changes=pages.Distinct().Select(p=>
        {
            var next=options;
            if(next!=null && preserveLayout)
            {var previous=EffectiveSettings(p);next=next with {Paper=previous.Paper,PaperSizeMm=previous.PaperSizeMm,Landscape=previous.Landscape,Scaling=previous.Scaling,Notebook=previous.Notebook};}
            return (Page:p,Options:next);
        }).ToArray();
        foreach(var change in changes)change.Options?.Validate();
        using var history=BeginHistoryEdit("Параметры печати страниц");
        foreach(var change in changes)change.Page.PrintOptions=change.Options;
        Status=options==null ? "Страницы наследуют параметры задания." : $"Сохранены параметры печати для {RussianQuantity.Format(changes.Length,"страницы","страниц","страниц")}.";
    }
    private void NotifyPageOptions()
    {
        foreach(var name in new[]{nameof(PageOptionsSummary),nameof(CanvasWidth),nameof(CanvasHeight),nameof(CanvasSafeArea),nameof(PaperSummary),nameof(HasFillWarning),nameof(StretchHint),nameof(CanvasHint),nameof(IsTextMode),nameof(MonochromeHelp),nameof(HasThresholdWarning)})OnPropertyChanged(name);
    }
}
