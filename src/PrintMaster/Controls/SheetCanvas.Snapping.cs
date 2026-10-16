using PrintMaster.Models;
using PrintMaster.Services;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace PrintMaster.Controls;

public sealed partial class SheetCanvas
{
    public static readonly DependencyProperty SnappingEnabledProperty=DependencyProperty.Register(nameof(SnappingEnabled),typeof(bool),typeof(SheetCanvas),
        new FrameworkPropertyMetadata(true,FrameworkPropertyMetadataOptions.AffectsRender,SnappingChanged));
    public bool SnappingEnabled {get=>(bool)GetValue(SnappingEnabledProperty);set=>SetValue(SnappingEnabledProperty,value);}
    private PointD _moveDelta;
    private ModifierKeys _lastMoveKeys;
    private SnapResult? _snapState;
    private bool IsMoveGesture=>_dragging && _moving!=null && !IsBoxTransform && !_resizing;
    internal PointD MoveDelta=>_moveDelta;
    internal SnapResult? ActiveSnap=>_snapState;
    private static void SnappingChanged(DependencyObject sender,DependencyPropertyChangedEventArgs e)
    {
        var canvas=(SheetCanvas)sender;
        canvas._snapState=null;
        if(canvas.IsMoveGesture) {canvas.UpdateMoveSnapping(canvas._lastMoveKeys);canvas._draft=canvas.TranslateDraft(canvas._moving!);}
    }
    private static void SnapGeometryChanged(DependencyObject sender,DependencyPropertyChangedEventArgs e)
    {
        var canvas=(SheetCanvas)sender; canvas._snapState=null;
        if(canvas.IsMoveGesture) {canvas.UpdateMoveSnapping(canvas._lastMoveKeys);canvas._draft=canvas.TranslateDraft(canvas._moving!);canvas.InvalidateVisual();}
    }
    private void ResetSnapping(){_moveDelta=default;_snapState=null;_lastMoveKeys=ModifierKeys.None;}
    private void UpdateMoveSnapping(ModifierKeys keys)
    {
        _lastMoveKeys=keys;
        var raw=new PointD(_end.X-_start.X,_end.Y-_start.Y);
        var wasMoving=Math.Abs(_moveDelta.X)+Math.Abs(_moveDelta.Y)>1e-9;
        _moveDelta=raw;
        if(!IsMoveGesture || !SnappingEnabled || keys.HasFlag(ModifierKeys.Alt)
            || !wasMoving && _snapState==null && Math.Abs(raw.X)+Math.Abs(raw.Y)<1e-9)
        {_snapState=null;return;}
        var items=_movingGroup.Length>0 ? _movingGroup : [_moving!];
        var bounds=items.Select(SheetEditing.VisualBounds).ToArray();
        var left=bounds.Min(b=>b.X);var top=bounds.Min(b=>b.Y);var right=bounds.Max(b=>b.Right);var bottom=bounds.Max(b=>b.Bottom);
        var moving=new RectD(left+raw.X,top+raw.Y,right-left,bottom-top);
        var safe=new RectD(SafeArea.X/Units,SafeArea.Y/Units,SafeArea.Width/Units,SafeArea.Height/Units);
        _snapState=PrintableAreaSnap.Resolve(moving,safe,6/ScreenScale/Units,10/ScreenScale/Units,_snapState);
        _moveDelta=new(raw.X+_snapState.OffsetX,raw.Y+_snapState.OffsetY);
    }
    // Used by modifier events and tests; no model mutation until the normal gesture commit.
    internal void SetSnapBypass(bool bypass)
    {
        if(!IsMoveGesture) return;
        var keys=bypass ? _lastMoveKeys|ModifierKeys.Alt : _lastMoveKeys&~ModifierKeys.Alt;
        UpdateMoveSnapping(keys); _draft=TranslateDraft(_moving!); InvalidateVisual();
    }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if(IsMoveGesture && IsAltKey(e)) {SetSnapBypass(true);e.Handled=true;}
    }
    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if(IsMoveGesture && IsAltKey(e))
        {
            var released=e.Key==Key.System ? e.SystemKey : e.Key;
            SetSnapBypass(Keyboard.IsKeyDown(released==Key.LeftAlt ? Key.RightAlt : Key.LeftAlt)); e.Handled=true;
        }
    }
    private static bool IsAltKey(KeyEventArgs e)=>e.Key is Key.LeftAlt or Key.RightAlt || e.Key==Key.System && e.SystemKey is Key.LeftAlt or Key.RightAlt;
    private void DrawSnapGuides(DrawingContext dc)
    {
        if(!IsMoveGesture || !CanEdit || _snapState==null) return;
        var halo=new Pen(Brushes.White,3/ScreenScale); var line=new Pen(Brushes.DeepPink,1.2/ScreenScale);
        if(_snapState.VerticalGuide is {} vertical)
        {
            var a=new Point(vertical.Position*Units,SafeArea.Y);var b=new Point(vertical.Position*Units,SafeArea.Bottom);
            dc.DrawLine(halo,a,b);dc.DrawLine(line,a,b);
        }
        if(_snapState.HorizontalGuide is {} horizontal)
        {
            var a=new Point(SafeArea.X,horizontal.Position*Units);var b=new Point(SafeArea.Right,horizontal.Position*Units);
            dc.DrawLine(halo,a,b);dc.DrawLine(line,a,b);
        }
    }
}
