using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using PrintMaster.Views;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using Xunit;

namespace PrintMaster.Tests;

public sealed partial class WpfSmokeTests
{
    private static async Task VerifyWorkspacePersistence(MainWindow owner)
    {
        var store=new WorkspaceStateStore(WorkspaceStateTests.StatePath());
        var dpi=GetDpiForWindow(new WindowInteropHelper(owner).Handle);
        await store.SaveAsync(new() { Window=new(80,90,1080,790,dpi),Panels=new(302,327) },default);
        PrintSessionViewModel Create()=>new(new TestDialogs(),Path.Combine(Fixtures.Root,"ux013-ui-recovery-"+Guid.NewGuid().ToString("N")),
            preferences:new WorkspaceStateTests.Preferences(new() { AutosaveEnabled=false }),workspaceState:store);
        async Task Close(MainWindow window)
        { await window.ViewModel.PrepareCloseAsync(); window.Close(); await WaitUntil(()=>!window.IsVisible); }
        var firstVm=Create(); await firstVm.InitializeWorkspaceAsync(); var first=new MainWindow(firstVm); first.Show();
        try
        {
            await firstVm.InitializeAsync(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            first.Width=1180; first.Height=720; first.UpdateLayout();
            var queue=(ColumnDefinition)first.FindName("QueueColumn"); var inspector=(ColumnDefinition)first.FindName("InspectorColumn");
            queue.Width=new GridLength(311); inspector.Width=new GridLength(333); first.UpdateLayout();
            ((GridSplitter)first.FindName("QueueSplitter")).RaiseEvent(new DragCompletedEventArgs(0,0,false) { RoutedEvent=Thumb.DragCompletedEvent });
            Assert.Equal(311,first.CaptureWorkspacePanels().QueueWidth,1); Assert.Equal(333,first.CaptureWorkspacePanels().InspectorWidth,1);
            ((Button)first.FindName("InspectorToggle")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(((FrameworkElement)first.FindName("InspectorPane")).IsVisible);
            first.Width=780; first.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(((FrameworkElement)first.FindName("QueuePane")).IsVisible);
            first.Width=1180; first.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(311,queue.ActualWidth,1); Assert.False(((FrameworkElement)first.FindName("InspectorPane")).IsVisible);
            Assert.Equal(333,first.CaptureWorkspacePanels().InspectorWidth,1);
            Assert.False(firstVm.HasUnsavedChanges); CaptureLayout(first,"ux013-ui-environment-first.png");
            first.WindowState=WindowState.Minimized; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(WindowEnvironment.Capture(first)!.Maximized);
        }
        finally { await Close(first); }
        var saved=await store.LoadAsync(default); Assert.False(saved.Panels.InspectorVisible); Assert.Equal(333,saved.Panels.InspectorWidth,1);
        Assert.Equal(311,saved.Panels.QueueWidth,1); Assert.NotNull(saved.Window); Assert.False(saved.Window!.Maximized);
        var secondVm=Create(); await secondVm.InitializeWorkspaceAsync(); var second=new MainWindow(secondVm); second.Show();
        try
        {
            await secondVm.InitializeAsync(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); second.UpdateLayout();
            Assert.Equal(WindowState.Normal,second.WindowState); Assert.InRange(second.ActualWidth,1177,1183);
            Assert.Equal(311,((ColumnDefinition)second.FindName("QueueColumn")).ActualWidth,1);
            Assert.False(((FrameworkElement)second.FindName("InspectorPane")).IsVisible);
            ((Button)second.FindName("InspectorToggle")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); second.UpdateLayout();
            Assert.Equal(333,((ColumnDefinition)second.FindName("InspectorColumn")).ActualWidth,1);
            var path=ProjectFileTests.PathFor("ux013-ui-recent"); await WorkspaceStateTests.CreateProjectAsync(path,"Открыть из меню");
            Assert.True(await secondVm.OpenProjectFromAsync(path));
            var recent=(MenuItem)second.FindName("RecentProjectsMenu"); Assert.True(recent.HasItems,"Recent projects must be an openable submenu before its first opening.");
            var projectMenu=Assert.IsType<MenuItem>(((Menu)second.FindName("MainMenu")).Items[0]);
            projectMenu.IsSubmenuOpen=true; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            recent.IsSubmenuOpen=true; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var item=Assert.IsType<MenuItem>(recent.Items[0]); recent.IsSubmenuOpen=false; projectMenu.IsSubmenuOpen=false;
            Assert.Same(secondVm.OpenRecentProjectCommand,item.Command); Assert.Equal(path,Assert.IsType<RecentProjectViewModel>(item.CommandParameter).Path);
            Assert.Equal(path,item.ToolTip); Assert.IsType<StackPanel>(item.Header); CaptureLayout(second,"ux013-ui-environment-restored.png");
            var missing=new MissingRecentProjectDialog(path+".moved") { Owner=second };
            missing.Loaded+=(_,_)=>missing.Dispatcher.BeginInvoke(new Action(()=>
            { missing.UpdateLayout(); Assert.True(InsideBounds((Button)missing.FindName("RemoveRecentButton"),(FrameworkElement)missing.Content)); CaptureLayout(missing,"ux013-ui-missing-project.png");
              ((Button)missing.FindName("RemoveRecentButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }));
            Assert.True(missing.ShowDialog()); Assert.Equal(MissingRecentProjectChoice.Remove,missing.Result);
            second.WindowState=WindowState.Maximized; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            second.WindowState=WindowState.Minimized; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(WindowEnvironment.Capture(second)!.Maximized);
        }
        finally { await Close(second); }
        Assert.True((await store.LoadAsync(default)).Window!.Maximized);
        var thirdVm=Create(); await thirdVm.InitializeWorkspaceAsync(); var third=new MainWindow(thirdVm); third.Show();
        try
        {
            await thirdVm.InitializeAsync(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(WindowState.Maximized,third.WindowState); CaptureLayout(third,"ux013-ui-maximized-restored.png");
        }
        finally { await Close(third); }
        var outside=(await store.LoadAsync(default)) with { Window=new(50000,-50000,51000,-49300,dpi) };
        await store.SaveAsync(outside,default);
        var relocatedVm=Create(); await relocatedVm.InitializeWorkspaceAsync(); var relocated=new MainWindow(relocatedVm); relocated.Show();
        try
        {
            await relocatedVm.InitializeAsync(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); relocated.UpdateLayout();
            var hwnd=new WindowInteropHelper(relocated).Handle; Assert.True(GetEnvironmentWindowRect(hwnd,out var rect));
            var monitor=new EnvironmentMonitor { Size=Marshal.SizeOf<EnvironmentMonitor>() };
            Assert.True(GetEnvironmentMonitorInfo(MonitorFromEnvironmentWindow(hwnd,2),ref monitor));
            Assert.InRange(rect.Left,monitor.Work.Left,monitor.Work.Right-1); Assert.InRange(rect.Top,monitor.Work.Top,monitor.Work.Bottom-1);
            Assert.True(rect.Right<=monitor.Work.Right+2 && rect.Bottom<=monitor.Work.Bottom+2);
            Assert.True(InsideBounds((FrameworkElement)relocated.FindName("PrintButton"),(FrameworkElement)relocated.Content));
            CaptureLayout(relocated,"ux013-ui-offscreen-restored.png");
        }
        finally { await Close(relocated); }
        Assert.True(owner.IsVisible);
    }
    [StructLayout(LayoutKind.Sequential)] private struct EnvironmentRect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct EnvironmentMonitor { public int Size; public EnvironmentRect Monitor,Work; public uint Flags; }
    [DllImport("user32.dll",EntryPoint="GetWindowRect")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetEnvironmentWindowRect(nint hwnd,out EnvironmentRect rect);
    [DllImport("user32.dll",EntryPoint="MonitorFromWindow")] private static extern nint MonitorFromEnvironmentWindow(nint hwnd,uint flags);
    [DllImport("user32.dll",EntryPoint="GetMonitorInfoW")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetEnvironmentMonitorInfo(nint monitor,ref EnvironmentMonitor info);
}
