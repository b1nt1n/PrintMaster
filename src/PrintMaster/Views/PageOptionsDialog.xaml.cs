using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.Windows;
namespace PrintMaster.Views;
public partial class PageOptionsDialog : Window
{
    internal PageOptionsEditorViewModel ViewModel {get;}
    public PageOptionsDialog(PageOptionsEditorViewModel model)
    {InitializeComponent();DataContext=ViewModel=model;WindowWorkArea.Attach(this);}
    private void ApplyClicked(object sender,RoutedEventArgs e)
    {if(!DialogInputValidation.CommitAndCheck(this))return;ViewModel.Validate();if(ViewModel.CanApply)DialogResult=true;}
}
