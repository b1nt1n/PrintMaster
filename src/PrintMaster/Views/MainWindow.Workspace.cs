using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PrintMaster.Models;
using PrintMaster.Services;

namespace PrintMaster.Views;

public partial class MainWindow
{
    private WorkspacePanels _panelPreferences=new();
    private bool _environmentApplied;
    private bool _queueVisible=true,_inspectorVisible=true;
    private void RestoreWorkspaceEnvironment()
    {
        if(_environmentApplied) return;
        _environmentApplied=true; _panelPreferences=ViewModel.SavedWorkspace.Panels;
        var saved=ViewModel.SavedWorkspace.Window;
        if(saved!=null)
        {
            WindowStartupLocation=WindowStartupLocation.Manual;
            void Restore()
            {
                if(!WindowEnvironment.Restore(this,saved))
                { WindowStartupLocation=WindowStartupLocation.CenterScreen; ViewModel.Status="Положение окна не восстановлено. Используется начальное расположение."; }
            }
            if(new System.Windows.Interop.WindowInteropHelper(this).Handle==0) SourceInitialized+=(_,_)=>Restore(); else Restore();
        }
        AdaptWorkspace(this,null!);
    }
    private void AdaptWorkspace(object sender,SizeChangedEventArgs e)
    {
        var width=WorkspaceRoot.ActualWidth>0 ? WorkspaceRoot.ActualWidth : Math.Max(1,ActualWidth-16);
        (_queueVisible,_inspectorVisible)=_panelPreferences.VisibleAt(width);
        var compact=width<940;
        Grid.SetRow(MainMenu,compact ? 1 : 0); Grid.SetColumn(MainMenu,compact ? 0 : 1); Grid.SetColumnSpan(MainMenu,compact ? 3 : 1);
        MainMenu.Margin=compact ? new Thickness(0,4,0,0) : new Thickness(6,0,0,0);
        ApplyWorkspacePanels();
    }
    private void ToggleQueue(object sender,RoutedEventArgs e)
    {
        var visible=!_queueVisible;
        _panelPreferences=_panelPreferences with { QueueVisible=visible,PreferredPanel=WorkspacePanel.Queue,
            ExpandInCompact=WorkspaceRoot.ActualWidth<820 ? visible || _inspectorVisible : _panelPreferences.ExpandInCompact };
        AdaptWorkspace(this,null!);
    }
    private void ToggleInspector(object sender,RoutedEventArgs e)
    {
        var visible=!_inspectorVisible;
        _panelPreferences=_panelPreferences with { InspectorVisible=visible,PreferredPanel=WorkspacePanel.Inspector,
            ExpandInCompact=WorkspaceRoot.ActualWidth<820 ? visible || _queueVisible : _panelPreferences.ExpandInCompact };
        AdaptWorkspace(this,null!);
    }
    private void ApplyWorkspacePanels()
    {
        if(!_queueVisible && Keyboard.FocusedElement is DependencyObject queueFocus && Inside(queueFocus,QueuePane)) QueueToggle.Focus();
        if(!_inspectorVisible && Keyboard.FocusedElement is DependencyObject focus && Inside(focus,InspectorPane)) InspectorToggle.Focus();
        QueuePane.Visibility=QueueSplitter.Visibility=_queueVisible ? Visibility.Visible : Visibility.Collapsed;
        InspectorPane.Visibility=InspectorSplitter.Visibility=_inspectorVisible ? Visibility.Visible : Visibility.Collapsed;
        var width=Math.Max(360,WorkspaceRoot.ActualWidth);
        var queueWidth=_panelPreferences.QueueWidth;
        var inspectorWidth=_panelPreferences.InspectorWidth;
        QueueColumn.MinWidth=_queueVisible ? 220 : 0;
        InspectorColumn.MinWidth=_inspectorVisible ? 240 : 0;
        QueueColumn.MaxWidth=_queueVisible ? Math.Max(220,Math.Min(340,width-364-(_inspectorVisible ? 294 : 0))) : 340;
        InspectorColumn.MaxWidth=_inspectorVisible ? Math.Max(240,Math.Min(360,width-364-(_queueVisible ? 264 : 0))) : 360;
        QueueColumn.Width=new GridLength(_queueVisible ? Math.Clamp(queueWidth,QueueColumn.MinWidth,QueueColumn.MaxWidth) : 0); QueueSplitColumn.Width=new GridLength(_queueVisible ? 4 : 0);
        InspectorColumn.Width=new GridLength(_inspectorVisible ? Math.Clamp(inspectorWidth,InspectorColumn.MinWidth,InspectorColumn.MaxWidth) : 0); InspectorSplitColumn.Width=new GridLength(_inspectorVisible ? 4 : 0);
        QueueToggle.FontWeight=_queueVisible ? FontWeights.SemiBold : FontWeights.Normal;
        InspectorToggle.FontWeight=_inspectorVisible ? FontWeights.SemiBold : FontWeights.Normal;
    }
    private void WorkspaceSplitterFinished(object sender,System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if(_queueVisible) _panelPreferences=_panelPreferences with { QueueWidth=Math.Clamp(QueueColumn.ActualWidth,220,340) };
        if(_inspectorVisible) _panelPreferences=_panelPreferences with { InspectorWidth=Math.Clamp(InspectorColumn.ActualWidth,240,360) };
        ApplyWorkspacePanels();
    }
    internal WorkspacePanels CaptureWorkspacePanels()=>_panelPreferences;
}
