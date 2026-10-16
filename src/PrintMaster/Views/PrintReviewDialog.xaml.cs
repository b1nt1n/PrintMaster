using PrintMaster.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PrintMaster.Views;

public partial class PrintReviewDialog : Window
{
    public PrintReviewViewModel Model { get; }
    public PrintReviewDialog(PrintReviewViewModel model)
    {
        InitializeComponent(); Model=model; DataContext=model;
        PrintMaster.Services.WindowWorkArea.Attach(this);
        Loaded+=async (_,_)=>await Model.RefreshAsync();
        Model.Settings.PropertyChanged+=(_,_)=>AcknowledgeClipping.IsChecked=false;
        Closed+=(_,_)=>Model.Dispose();
    }
    private void Previous(object sender,RoutedEventArgs e)=>Model.PreviewIndex--;
    private void Next(object sender,RoutedEventArgs e)=>Model.PreviewIndex++;
    private void ShowPaperGuide(object sender,RoutedEventArgs e)
    {
        if(!DialogInputValidation.CommitAndCheck(this) || Model.CreatePaperGuide() is not {} guide) return;
        new PaperGuideDialog(guide) { Owner=this }.ShowDialog();
    }
    private void Submit(object sender,RoutedEventArgs e)
    {
        if(!DialogInputValidation.CommitAndCheck(this)) return;
        if(Model.TryAccept(AcknowledgeClipping.IsChecked==true)) DialogResult=true;
        else if(Model.AllowCropping && AcknowledgeClipping.IsChecked!=true) AcknowledgeClipping.Focus();
    }
}
