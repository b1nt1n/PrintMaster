using PrintMaster.Services;
using System.Windows;

namespace PrintMaster.Views;

public partial class MissingRecentProjectDialog : Window
{
    public MissingRecentProjectDialog(string path) { InitializeComponent(); MissingPath.Text=path; WindowWorkArea.Attach(this); }
    public MissingRecentProjectChoice Result { get; private set; }=MissingRecentProjectChoice.Cancel;
    private void Locate(object sender,RoutedEventArgs e) { Result=MissingRecentProjectChoice.Locate; DialogResult=true; }
    private void Remove(object sender,RoutedEventArgs e) { Result=MissingRecentProjectChoice.Remove; DialogResult=true; }
}
