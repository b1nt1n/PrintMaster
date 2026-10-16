using PrintMaster.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace PrintMaster.Views;

public partial class MainWindow
{
    private async void RecentProjectsOpened(object sender,RoutedEventArgs e)
    {
        if(e.Source!=RecentProjectsMenu) return;
        RebuildRecentProjectsMenu(); await ViewModel.RefreshRecentAvailabilityAsync();
    }
    internal void RebuildRecentProjectsMenu()
    {
        RecentProjectsMenu.Items.Clear();
        if(ViewModel.RecentProjects.Count==0)
        { RecentProjectsMenu.Items.Add(new MenuItem { Header="Список пока пуст",IsEnabled=false }); return; }
        foreach(var entry in ViewModel.RecentProjects)
        {
            var title=new TextBlock { MaxWidth=440,TextTrimming=TextTrimming.CharacterEllipsis }; title.SetBinding(TextBlock.TextProperty,new Binding(nameof(RecentProjectViewModel.DisplayName)) { Source=entry });
            var path=new TextBlock { Text=entry.Path,FontSize=11,Foreground=(Brush)FindResource("Muted"),MaxWidth=440,TextTrimming=TextTrimming.CharacterEllipsis };
            var header=new StackPanel(); header.Children.Add(title); header.Children.Add(path);
            var item=new MenuItem { Header=header,Icon=new PrintMaster.Controls.LucideIcon {Icon="file-text",Width=16,Height=16},Command=ViewModel.OpenRecentProjectCommand,CommandParameter=entry,ToolTip=entry.Path };
            System.Windows.Automation.AutomationProperties.SetName(item,entry.Name+" "+entry.Path);
            RecentProjectsMenu.Items.Add(item);
        }
        RecentProjectsMenu.Items.Add(new Separator());
        RecentProjectsMenu.Items.Add(new MenuItem { Header="Очистить список",Icon=CommandIcon("Delete"),Command=ViewModel.ClearRecentProjectsCommand });
    }
}
