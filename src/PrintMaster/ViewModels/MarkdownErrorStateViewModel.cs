using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PrintMaster.ViewModels;

public partial class PrintSessionViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMarkdownWarning))]
    private string markdownWarning="";

    [ObservableProperty]
    private bool hasActiveMarkdownDraftError;

    public bool HasMarkdownWarning=>MarkdownWarning.Length!=0;

    partial void OnHasActiveMarkdownDraftErrorChanged(bool value)=>PrintCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void DismissMarkdownWarning()=>MarkdownWarning="";

    private void ReportMarkdownWarning(string reason,string effect)
    {
        MarkdownWarning=$"Markdown: {reason.Trim()} {effect}";
        Status=MarkdownWarning;
    }
}
