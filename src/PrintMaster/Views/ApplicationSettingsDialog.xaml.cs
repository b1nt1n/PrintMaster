using PrintMaster.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PrintMaster.Views;

public partial class ApplicationSettingsDialog : Window
{
    public ApplicationSettingsViewModel Model { get; }
    public ApplicationSettingsDialog(ApplicationSettingsViewModel model)
    {
        InitializeComponent(); Model=model; DataContext=model;
        PrintMaster.Services.WindowWorkArea.Attach(this);
    }
    private void Accept(object sender,RoutedEventArgs e)
    {
        if(!DialogInputValidation.CommitAndCheck(this)) return;
        if(Model.TryAccept()) DialogResult=true;
    }
}
