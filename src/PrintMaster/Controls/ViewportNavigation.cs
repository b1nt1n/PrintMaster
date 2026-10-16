using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PrintMaster.Controls;

// Navigation owns viewport pixels only. Page geometry and rendering DPI stay in the model.
public sealed class ViewportNavigation : IDisposable
{
    private readonly ScrollViewer _scroll;
    private readonly FrameworkElement _stage;
    private readonly Func<FrameworkElement?> _paper;
    private readonly PrintSessionViewModel _model;
    private ScrollContentPresenter? _viewport;
    private bool _updating,_scheduled,_centerPending,_disposed;
    private Guid? _paperId;
    private Size _paperSize;
    private Point? _panStart;
    private Point _panOffset;
    private Cursor? _previousCursor;
    public ViewportNavigation(ScrollViewer scroll,FrameworkElement stage,Func<FrameworkElement?> paper,PrintSessionViewModel model)
    {
        (_scroll,_stage,_paper,_model)=(scroll,stage,paper,model);
        scroll.Loaded+=Loaded; scroll.ScrollChanged+=Scrolled; scroll.PreviewMouseWheel+=Wheel;
        scroll.PreviewMouseDown+=PanStart; scroll.PreviewMouseMove+=PanMove; scroll.PreviewMouseUp+=PanEnd; scroll.LostMouseCapture+=LostCapture;
        model.FitZoomRequested+=FitRequested; model.ManualZoomRequested+=ManualZoom; model.PropertyChanged+=ModelChanged;
    }
    private void Loaded(object sender,RoutedEventArgs e)=>Schedule(true);
    private void Scrolled(object sender,ScrollChangedEventArgs e)
    {
        if(e.ViewportWidthChange!=0 || e.ViewportHeightChange!=0) Schedule(_model.AutoFitZoom);
    }
    private void ModelChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName is not (nameof(PrintSessionViewModel.SelectedPage) or nameof(PrintSessionViewModel.CanvasWidth) or nameof(PrintSessionViewModel.CanvasHeight))) return;
        var id=_model.SelectedPage?.Id; var size=new Size(_model.CanvasWidth,_model.CanvasHeight);
        if(id==_paperId && size==_paperSize) return;
        _paperId=id; _paperSize=size; Schedule(true);
    }
    private void FitRequested(object? sender,EventArgs e)=>Schedule(true);
    private void ManualZoom(object? sender,double value)=>ZoomAt(ViewportCenter(),value);
    private void Schedule(bool center)
    {
        if(_disposed || _updating) return; _centerPending|=center;
        if(_scheduled) return; _scheduled=true;
        _= _scroll.Dispatcher.BeginInvoke(()=>
        {
            _scheduled=false; if(_disposed) return; var shouldCenter=_centerPending; _centerPending=false; Refit(shouldCenter);
        },DispatcherPriority.Loaded);
    }
    private ScrollContentPresenter? Viewport()
    {
        _scroll.ApplyTemplate(); return _viewport ??=Find<ScrollContentPresenter>(_scroll);
    }
    private Point ViewportCenter()=>Viewport() is {} view ? new(view.ActualWidth/2,view.ActualHeight/2) : new(0,0);
    private void UpdateWorkspace()
    {
        var view=Viewport(); if(view==null) return;
        // A halo around the page permits cursor-preserving zoom even while the full page fits.
        _stage.Margin=_model.HasSelection ? new(view.ActualWidth/2,view.ActualHeight/2,view.ActualWidth/2,view.ActualHeight/2) : new(0);
    }
    internal void Refit(bool center=true)
    {
        if(_updating || !_model.HasSelection || Viewport() is not {} view || _paper() is not {} paper) return;
        _updating=true;
        try
        {
            var before=view.TranslatePoint(ViewportCenter(),paper); UpdateWorkspace();
            if(_model.AutoFitZoom) _model.FitToViewport(Math.Max(1,view.ActualWidth-40),Math.Max(1,view.ActualHeight-40));
            _scroll.UpdateLayout(); UpdateWorkspace(); _scroll.UpdateLayout();
            RestoreAnchor(center || _model.AutoFitZoom ? new(paper.ActualWidth/2,paper.ActualHeight/2) : before,ViewportCenter());
        }
        finally { _updating=false; }
    }
    internal bool HandleWheel(Point viewportPoint,int delta,bool control)
    {
        if(!control || !_model.HasSelection || delta==0) return false;
        ZoomAt(viewportPoint,ViewportZoom.FromWheel(_model.Zoom,delta)); return true;
    }
    private void Wheel(object sender,MouseWheelEventArgs e)
    {
        if(Viewport() is {} view && HandleWheel(e.GetPosition(view),e.Delta,Keyboard.Modifiers.HasFlag(ModifierKeys.Control))) e.Handled=true;
    }
    internal void ZoomAt(Point viewportPoint,double percent)
    {
        if(_updating || !_model.HasSelection || Viewport() is not {} view || _paper() is not {} paper) return;
        _updating=true;
        try
        {
            var anchor=view.TranslatePoint(viewportPoint,paper);
            _model.ApplyManualZoom(percent); UpdateWorkspace(); _scroll.UpdateLayout(); UpdateWorkspace(); _scroll.UpdateLayout();
            RestoreAnchor(anchor,viewportPoint);
        }
        finally { _updating=false; }
    }
    private void RestoreAnchor(Point paperPoint,Point viewportPoint)
    {
        if(Viewport() is not {} view || _paper() is not {} paper) return;
        for(var pass=0;pass<2;pass++)
        {
            var current=paper.TranslatePoint(paperPoint,view);
            _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset+current.X-viewportPoint.X);
            _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset+current.Y-viewportPoint.Y);
            _scroll.UpdateLayout();
        }
    }
    private void PanStart(object sender,MouseButtonEventArgs e)
    {
        if(e.ChangedButton!=MouseButton.Middle || !_model.HasSelection || Viewport() is not {} view) return;
        _panStart=e.GetPosition(view); _panOffset=new(_scroll.HorizontalOffset,_scroll.VerticalOffset);
        _previousCursor=_scroll.Cursor; _scroll.Cursor=Cursors.Hand; _scroll.CaptureMouse(); e.Handled=true;
    }
    private void PanMove(object sender,MouseEventArgs e)
    {
        if(_panStart is not {} start || Viewport() is not {} view) return;
        if(e.MiddleButton!=MouseButtonState.Pressed) { EndPan(); return; }
        var point=e.GetPosition(view); PanBy(new(point.X-start.X,point.Y-start.Y),_panOffset); e.Handled=true;
    }
    internal void PanBy(Point distance,Point? from=null)
    {
        var offset=from ?? new(_scroll.HorizontalOffset,_scroll.VerticalOffset);
        _scroll.ScrollToHorizontalOffset(offset.X-distance.X); _scroll.ScrollToVerticalOffset(offset.Y-distance.Y); _scroll.UpdateLayout();
    }
    private void PanEnd(object sender,MouseButtonEventArgs e) { if(e.ChangedButton==MouseButton.Middle && _panStart!=null) { EndPan(); e.Handled=true; } }
    private void LostCapture(object sender,MouseEventArgs e) { if(_panStart!=null) EndPan(); }
    private void EndPan() { _panStart=null; _scroll.Cursor=_previousCursor; if(_scroll.IsMouseCaptured) _scroll.ReleaseMouseCapture(); }
    private static T? Find<T>(DependencyObject root) where T:DependencyObject
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) { var child=VisualTreeHelper.GetChild(root,i); if(child is T found) return found; if(Find<T>(child) is {} result) return result; } return null;
    }
    public void Dispose()
    {
        _disposed=true; EndPan(); _scroll.Loaded-=Loaded; _scroll.ScrollChanged-=Scrolled; _scroll.PreviewMouseWheel-=Wheel;
        _scroll.PreviewMouseDown-=PanStart; _scroll.PreviewMouseMove-=PanMove; _scroll.PreviewMouseUp-=PanEnd; _scroll.LostMouseCapture-=LostCapture;
        _model.FitZoomRequested-=FitRequested; _model.ManualZoomRequested-=ManualZoom; _model.PropertyChanged-=ModelChanged;
    }
}
