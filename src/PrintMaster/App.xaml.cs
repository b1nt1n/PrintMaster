using PrintMaster.Services;
using PrintMaster.Views;
using PrintMaster.ViewModels;
using System.Windows;

namespace PrintMaster;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException+=(_,error)=>
        {
            ApplicationErrorLog.TryAppend(error.Exception);
            MessageBox.Show(error.Exception.Message,"PrintMaster",MessageBoxButton.OK,MessageBoxImage.Error); error.Handled=true;
        };
        var session=new PrintSessionViewModel(new UserDialogs(),preferences:new ApplicationPreferencesStore(),workspaceState:new WorkspaceStateStore());
        await session.InitializeWorkspaceAsync();
        var window=new MainWindow(session,startupFiles:e.Args); MainWindow=window; window.Show();
    }
}
