using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.Windows;

namespace PrintMaster.Views;

public partial class NotebookProfileDialog : Window
{
    private readonly NotebookProfileEditorViewModel _model;
    public NotebookProfile? Result=>_model.Result;
    public NotebookProfileDialog(NotebookProfile profile,bool copy=false)
    { InitializeComponent(); DataContext=_model=new(profile,copy); WindowWorkArea.Attach(this); }
    private void ApplyClicked(object sender,RoutedEventArgs e)
    { if(DialogInputValidation.CommitAndCheck(this) && _model.CanApply) DialogResult=true; }
}
