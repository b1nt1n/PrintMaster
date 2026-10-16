using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.Windows;

namespace PrintMaster.Views;

public partial class PaperGuideDialog : Window
{
    private readonly PaperGuideViewModel _model;
    public PaperGuideDialog(PaperGuideViewModel model)
    { InitializeComponent(); DataContext=_model=model; WindowWorkArea.Attach(this); }
    private void ContinueClicked(object sender,RoutedEventArgs e)
    { if(DialogInputValidation.CommitAndCheck(this) && _model.CanContinue) DialogResult=true; }
}
