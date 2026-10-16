using PrintMaster.Models;
using PrintMaster.ViewModels;
using System.Windows;

namespace PrintMaster.Views;

public partial class NewSheetDialog : Window
{
    public NewSheetViewModel ViewModel { get; }
    public SheetPaper? Paper=>ViewModel.Result;
    public NewSheetDialog(PrintSettings settings) { InitializeComponent(); ViewModel=new(settings); DataContext=ViewModel; PrintMaster.Services.WindowWorkArea.Attach(this); }
    private void CreateClicked(object sender,RoutedEventArgs e) { if(DialogInputValidation.CommitAndCheck(this) && ViewModel.CanCreate) DialogResult=true; }
    private void NotebookProfileClicked(object sender,RoutedEventArgs e)
    {
        if(ViewModel.SelectedNotebookProfile is not {} profile) return;
        var copy=NotebookProfiles.Defaults.Any(p=>p.Id==profile.Id);
        var dialog=new NotebookProfileDialog(profile,copy) { Owner=this };
        if(dialog.ShowDialog()==true && dialog.Result is {} result) ViewModel.SetNotebookProfile(result);
    }
}
