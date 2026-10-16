using PrintMaster.Models;
using PrintMaster.Services;
using System.Collections.Immutable;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PrintMaster.Controls;

public sealed partial class SheetCanvas
{
    public static readonly DependencyProperty ViewScaleProperty=DependencyProperty.Register(nameof(ViewScale),typeof(double),typeof(SheetCanvas),new FrameworkPropertyMetadata(1d,FrameworkPropertyMetadataOptions.AffectsRender,SnapGeometryChanged));
    public double ViewScale { get=>(double)GetValue(ViewScaleProperty); set=>SetValue(ViewScaleProperty,value); }
    private double ScreenScale=>double.IsFinite(ViewScale) && ViewScale>0 ? ViewScale : 1;
    private double HitToleranceMm=>3/ScreenScale/Units;
    private ObjectHandle _activeHandle;
    private BitmapSource? _objectPreview;
    private bool IsBoxTransform=>_activeHandle is not (ObjectHandle.None or ObjectHandle.TextWidth);
    internal SheetObject? GestureDraft=>_draft;
    internal bool IsGestureActive=>_dragging;
    internal ObjectHandle ActiveHandle=>_activeHandle;
    internal PointD HandleLocation(SheetObject item,ObjectHandle handle)
    {
        var gap=25/ScreenScale/Units;
        if(handle!=ObjectHandle.Rotate) return ObjectTransform.HandlePoint(item,handle,gap);
        var center=ObjectTransform.Center(item); var margin=6/ScreenScale/Units;
        var width=ActualWidth/Units; var height=ActualHeight/Units;
        foreach(var edge in new[] { ObjectHandle.North,ObjectHandle.East,ObjectHandle.South,ObjectHandle.West })
        {
            var p=ObjectTransform.HandlePoint(item,edge); var dx=p.X-center.X; var dy=p.Y-center.Y; var length=Math.Sqrt(dx*dx+dy*dy);
            if(length<=0) continue; var candidate=new PointD(p.X+dx/length*gap,p.Y+dy/length*gap);
            if(candidate.X>=margin && candidate.Y>=margin && candidate.X<=width-margin && candidate.Y<=height-margin) return candidate;
        }
        // A photo covering the paper has no room for an outer knob. Place it inset,
        // away from the size handles rather than clamping it beside the top handle.
        foreach(var edge in new[] { ObjectHandle.North,ObjectHandle.East,ObjectHandle.South,ObjectHandle.West })
        {
            var p=ObjectTransform.HandlePoint(item,edge); var dx=p.X-center.X; var dy=p.Y-center.Y; var length=Math.Sqrt(dx*dx+dy*dy);
            if(length<=gap+margin) continue; var candidate=new PointD(p.X-dx/length*gap,p.Y-dy/length*gap);
            if(candidate.X>=margin && candidate.Y>=margin && candidate.X<=width-margin && candidate.Y<=height-margin) return candidate;
        }
        var top=ObjectTransform.HandlePoint(item,ObjectHandle.Rotate,gap);
        return new(Math.Clamp(top.X,margin,Math.Max(margin,width-margin)),Math.Clamp(top.Y,margin,Math.Max(margin,height-margin)));
    }
    internal IReadOnlyList<SheetObject> ObjectsAt(PointD point)=>Page==null ? [] : ObjectTransform.Hits(Page.Document.Objects,point,HitToleranceMm);

    internal ObjectHandle HitHandle(SheetObject item,PointD point)
    {
        var radius=7/ScreenScale/Units;
        return ObjectTransform.Handles(item).Where(h=>h!=ObjectHandle.TextWidth || item.Placement.RotationDegrees==0)
            .Select(h=>(Handle:h,Distance:Distance(HandleLocation(item,h),point)))
            .Where(h=>h.Distance<=radius).OrderBy(h=>h.Distance).Select(h=>h.Handle).FirstOrDefault();
    }
    private void DrawSelection(DrawingContext dc,SheetObject item,bool single)
    {
        var rect=MmRect(item.Placement.BoundsMm); var center=ObjectTransform.Center(item);
        var pen=new Pen(Brushes.RoyalBlue,1.2/ScreenScale) { DashStyle=DashStyles.Dash };
        dc.PushTransform(new RotateTransform(item.Placement.RotationDegrees,center.X*Units,center.Y*Units));
        dc.DrawRectangle(null,pen,rect); dc.Pop();
        if(!single || !CanEdit) return;
        var size=9/ScreenScale; var stroke=new Pen(Brushes.RoyalBlue,1/ScreenScale);
        foreach(var handle in ObjectTransform.Handles(item))
        {
            if(handle==ObjectHandle.TextWidth && item.Placement.RotationDegrees!=0) continue;
            var point=HandleLocation(item,handle); var p=new Point(point.X*Units,point.Y*Units);
            if(handle==ObjectHandle.Rotate)
            {
                var edge=new[] {ObjectHandle.North,ObjectHandle.East,ObjectHandle.South,ObjectHandle.West}.Select(h=>ObjectTransform.HandlePoint(item,h)).OrderBy(e=>Distance(e,point)).First();
                dc.DrawLine(stroke,new(edge.X*Units,edge.Y*Units),p);
                dc.DrawEllipse(Brushes.White,stroke,p,size/2,size/2);
            }
            else dc.DrawRectangle(Brushes.White,stroke,new(p.X-size/2,p.Y-size/2,size,size));
        }
    }
    private void UpdateHandleCursor(PointD point)
    {
        if(!CanEdit || Tool!=EditorTool.Select) return;
        var item=Page!.Document.Objects.FirstOrDefault(o=>o.Id==SelectedObjectId && !o.Locked);
        var handle=item!=null && SelectedObjectIds.Length<=1 ? HitHandle(item,point) : ObjectHandle.None;
        if(handle==ObjectHandle.Rotate) { Cursor=Cursors.Hand; ToolTip="Потяните для поворота. Shift — шаг 15°."; return; }
        if(handle!=ObjectHandle.None)
        {
            var at=ObjectTransform.HandlePoint(item!,handle); var center=ObjectTransform.Center(item!);
            var angle=(Math.Atan2(at.Y-center.Y,at.X-center.X)*180/Math.PI+180)%180;
            Cursor=angle<22.5 || angle>=157.5 ? Cursors.SizeWE : angle<67.5 ? Cursors.SizeNWSE : angle<112.5 ? Cursors.SizeNS : Cursors.SizeNESW;
            ToolTip=item is ContentObject photo ? photo.KeepAspectRatio ? "Размер фото: пропорции сохраняются." : "Свободный размер фото. Shift — сохранить текущие пропорции." : item is TextObject ? "Ширина текста: строки переносятся автоматически." : "Размер объекта. Shift — сохранить пропорции.";
        }
        else { Cursor=ObjectsAt(point).Count>0 ? Cursors.SizeAll : Cursors.Arrow; ToolTip=null; }
    }
    private (BitmapSource Background,BitmapSource? Object) BoxLayers(SheetSnapshot sheet,SheetObject selected,CancellationToken token,int cap=1600)
    {
        using var background=_rasterizer.RenderSheet(sheet with { Objects=sheet.Objects.Where(o=>o.Id!=selected.Id).ToImmutableArray() },120,false,token,cap);
        BitmapSource? image=null;
        if(selected is ContentObject photo)
        {
            var b=photo.Placement.BoundsMm;
            var single=sheet with { Paper=new(new(b.Width,b.Height),false,new(0,0,0,0)),PrimaryContentId=null,
                Objects=[photo with { Placement=new(new(0,0,b.Width,b.Height)) }] };
            using var raster=_rasterizer.RenderSheet(single,120,false,token,cap,true); image=BitmapBridge.ToWpf(raster);
        }
        return (BitmapBridge.ToWpf(background),image);
    }
    private async Task PrepareBoxTransformAsync(SheetSnapshot sheet,SheetObject selected,CancellationToken token)
    {
        try
        {
            await _renderGate.WaitAsync(token);
            try
            {
                var layers=await Task.Run(()=>BoxLayers(sheet,selected,token),token);
                if(!token.IsCancellationRequested && IsBoxTransform && _moving?.Id==selected.Id) { _background=layers.Background; _objectPreview=layers.Object; InvalidateVisual(); }
            }
            finally { _renderGate.Release(); }
        }
        catch(OperationCanceledException) { }
        catch(Exception ex) { _movePreviewError=ex.Message; MovePreviewFailed?.Invoke(ex.Message); }
    }
    private void DrawTransformedObject(DrawingContext dc,SheetObject item)
    {
        if(item is ShapeObject or InkObject) { DrawDraft(dc,item); return; }
        if(_objectPreview==null) return;
        var rect=MmRect(item.Placement.BoundsMm);
        dc.PushTransform(new RotateTransform(item.Placement.RotationDegrees,rect.X+rect.Width/2,rect.Y+rect.Height/2));
        dc.DrawImage(_objectPreview,rect); dc.Pop();
    }
    private void EnsureGesturePreview()
    {
        if(_moving==null || _draft==null) return;
        // A very short gesture can end before the async split is ready. Only this bounded
        // UI-preview fallback is synchronous; it never renders at print DPI.
        if(IsBoxTransform)
        {
            if(MovePreviewReady) return;
            var layers=BoxLayers(Page!.Document.Snapshot(),_moving,default,1000); _background=layers.Background; _objectPreview=layers.Object;
        }
        else if(!MovePreviewReady || _resizing)
        {
            var sheet=Page!.Document.Snapshot(); var items=_resizing ? new[] {_draft} : _movingGroup; var ids=items.Select(o=>o.Id).ToHashSet();
            using var background=_rasterizer.RenderSheet(sheet with { Objects=sheet.Objects.Where(o=>!ids.Contains(o.Id)).ToImmutableArray() },120,false,default,1000);
            using var moving=_rasterizer.RenderSheet(sheet with { Objects=items.ToImmutableArray() },120,false,default,1000,true);
            _background=BitmapBridge.ToWpf(background); _movingBitmap=BitmapBridge.ToWpf(moving);
        }
    }
}
