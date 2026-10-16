using PrintMaster.Models;
using PrintMaster.Services;
using System.Windows;

namespace PrintMaster.Views;

public partial class OperationErrorDialog : Window
{
    public OperationErrorInfo Model { get; }
    public OperationErrorDialog(OperationErrorInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        InitializeComponent(); Model=model; DataContext=model;
        OperationErrorDetailsExpander.Visibility=string.IsNullOrWhiteSpace(model.Details) ? Visibility.Collapsed : Visibility.Visible;
        WindowWorkArea.Attach(this);
    }
}
