using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.Windows;

namespace PrintMaster.Views;

public partial class ExportDialog : Window
{
    public ExportViewModel Model { get; }
    public ExportDialog(ExportViewModel model)
    { InitializeComponent(); Model=model; DataContext=model; WindowWorkArea.Attach(this); Closed+=(_,_)=>model.Dispose(); }
    private void AcceptExport(object sender,RoutedEventArgs e)
    { if(DialogInputValidation.CommitAndCheck(this) && Model.TryAccept()) DialogResult=true; }
}
