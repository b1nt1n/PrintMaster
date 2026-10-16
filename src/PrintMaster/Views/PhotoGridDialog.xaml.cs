using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.Windows;

namespace PrintMaster.Views;

public partial class PhotoGridDialog : Window
{
    internal PhotoGridViewModel ViewModel { get; }
    public PhotoGridDialog(PhotoGridViewModel model)
    {
        InitializeComponent(); DataContext=ViewModel=model; WindowWorkArea.Attach(this);
        Closed+=(_,_)=>ViewModel.Dispose();
    }
    private void ApplyClicked(object sender,RoutedEventArgs e)
    { if(DialogInputValidation.CommitAndCheck(this) && ViewModel.CanApply) DialogResult=true; }
}
