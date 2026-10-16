using PrintMaster.Models;
using PrintMaster.Services;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace PrintMaster.Controls;

public sealed partial class AnnotationCanvas
{
    private CropHandle _cropHandle;
    private RectD _cropGestureRect;
    private PointD _cropGesturePoint;
    private SizeD _cropPixels;
    internal void SetCropPixelSize(SizeD size)=>_cropPixels=size;
    private SizeD CropPixelsOriented=>(_cropPixels.Width>0 ? _cropPixels : CropSourceSize).Rotated(Page!.RotationQuarterTurns);
    private SizeD CropSourceSize=>new(Page!.SourceWidth,Page.SourceHeight);
    private RectD CropBounds { get { var size=CropSourceSize.Rotated(Page!.RotationQuarterTurns); return new(0,0,size.Width,size.Height); } }
    private double CropMinimum=>Math.Min(1,Math.Min(CropBounds.Width/CropPixelsOriented.Width,CropBounds.Height/CropPixelsOriented.Height));
    internal bool IsCropDragging=>Tool==EditorTool.Crop && _dragging;
    internal RectD CurrentCrop=>_cropDraft ?? Page!.Edits.Crop;
    internal RectD OrientedCrop=>CropOrientation.ToOriented(CurrentCrop,CropSourceSize,Page!.RotationQuarterTurns);
    internal RectD CropScreenBounds
    {
        get
        {
            var crop=OrientedCrop; var bounds=CropBounds;
            return new(_placement.X+crop.X*_placement.Width/bounds.Width,_placement.Y+crop.Y*_placement.Height/bounds.Height,
                crop.Width*_placement.Width/bounds.Width,crop.Height*_placement.Height/bounds.Height);
        }
    }
    internal void BeginCropGesture(PointD sourceStart,CropHandle handle)
    {
        if(IsPrintPreview || Page?.IsImage!=true || Page.IsComposed || handle==CropHandle.None) return;
        CancelGesture(); BeforeGesture?.Invoke(this,EventArgs.Empty); SelectAnnotation(null);
        _cropHandle=handle; _cropGestureRect=OrientedCrop;
        _cropGesturePoint=CropOrientation.ToOriented(sourceStart,CropSourceSize,Page.RotationQuarterTurns);
        _cropDraft=Page.Edits.Crop; _dragging=true; InvalidateVisual();
    }
    private void UpdateCropGesture(PointD sourceEnd)
    {
        var end=CropOrientation.ToOriented(sourceEnd,CropSourceSize,Page!.RotationQuarterTurns);
        var dx=end.X-_cropGesturePoint.X; var dy=end.Y-_cropGesturePoint.Y;
        var crop=_cropHandle switch
        {
            CropHandle.Create=>ImageGeometry.AspectCrop(_cropGesturePoint,end,CropRatio,CropBounds),
            CropHandle.Move=>CropSelectionGeometry.Move(_cropGestureRect,dx,dy,CropBounds),
            _=>CropSelectionGeometry.Resize(_cropGestureRect,_cropHandle,dx,dy,CropBounds,CropRatio,CropMinimum)
        };
        _cropDraft=CropOrientation.ToSource(crop,CropSourceSize,Page.RotationQuarterTurns).Intersect(new(0,0,Page.SourceWidth,Page.SourceHeight));
    }
    internal CropHandle HitCropHandle(Point screen)
    {
        if(Page?.IsImage!=true || Preview==null || _placement.IsEmpty) return CropHandle.None;
        var r=CropScreenBounds;
        foreach(var (handle,point) in CropHandles(r))
            if(Math.Abs(screen.X-point.X)<=10 && Math.Abs(screen.Y-point.Y)<=10) return handle;
        if(Math.Abs(screen.Y-r.Y)<=7 && screen.X>=r.X && screen.X<=r.Right) return CropHandle.Top;
        if(Math.Abs(screen.Y-r.Bottom)<=7 && screen.X>=r.X && screen.X<=r.Right) return CropHandle.Bottom;
        if(Math.Abs(screen.X-r.X)<=7 && screen.Y>=r.Y && screen.Y<=r.Bottom) return CropHandle.Left;
        if(Math.Abs(screen.X-r.Right)<=7 && screen.Y>=r.Y && screen.Y<=r.Bottom) return CropHandle.Right;
        var pointD=new PointD(screen.X,screen.Y);
        return r.Contains(pointD) ? CropHandle.Move : _placement.Contains(pointD) ? CropHandle.Create : CropHandle.None;
    }
    private static IEnumerable<(CropHandle Handle,Point Point)> CropHandles(RectD r)
    {
        yield return (CropHandle.TopLeft,new(r.X,r.Y)); yield return (CropHandle.TopRight,new(r.Right,r.Y));
        yield return (CropHandle.BottomRight,new(r.Right,r.Bottom)); yield return (CropHandle.BottomLeft,new(r.X,r.Bottom));
        yield return (CropHandle.Top,new(r.X+r.Width/2,r.Y)); yield return (CropHandle.Right,new(r.Right,r.Y+r.Height/2));
        yield return (CropHandle.Bottom,new(r.X+r.Width/2,r.Bottom)); yield return (CropHandle.Left,new(r.X,r.Y+r.Height/2));
    }
    private static Cursor CropCursor(CropHandle handle)=>handle switch
    {
        CropHandle.TopLeft or CropHandle.BottomRight=>Cursors.SizeNWSE,
        CropHandle.TopRight or CropHandle.BottomLeft=>Cursors.SizeNESW,
        CropHandle.Top or CropHandle.Bottom=>Cursors.SizeNS,
        CropHandle.Left or CropHandle.Right=>Cursors.SizeWE,
        CropHandle.Move=>Cursors.SizeAll, CropHandle.Create=>Cursors.Cross,_=>Cursors.Arrow
    };
    private void DrawCropOverlay(DrawingContext dc)
    {
        var r=CropScreenBounds;
        var outside=new CombinedGeometry(GeometryCombineMode.Exclude,new RectangleGeometry(ToRect(_placement)),new RectangleGeometry(ToRect(r)));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(150,0,0,0)),null,outside);
        dc.DrawRectangle(null,new Pen(Brushes.Black,3),ToRect(r)); dc.DrawRectangle(null,new Pen(Brushes.White,1.5),ToRect(r));
        var thin=new Pen(new SolidColorBrush(Color.FromArgb(180,255,255,255)),.8);
        for(var n=1;n<=2;n++)
        {
            dc.DrawLine(thin,new(r.X+r.Width*n/3,r.Y),new(r.X+r.Width*n/3,r.Bottom));
            dc.DrawLine(thin,new(r.X,r.Y+r.Height*n/3),new(r.Right,r.Y+r.Height*n/3));
        }
        foreach(var (_,point) in CropHandles(r)) dc.DrawRectangle(Brushes.White,new Pen(Brushes.Black,1),new(point.X-5,point.Y-5,10,10));
        if(IsKeyboardFocused) dc.DrawRectangle(null,new Pen(Brushes.DodgerBlue,1) { DashStyle=DashStyles.Dot },new(r.X-8,r.Y-8,r.Width+16,r.Height+16));
    }
    private bool HandleCropKey(KeyEventArgs e)
    {
        if(_dragging || Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Shift)) return false;
        var amount=Keyboard.Modifiers==ModifierKeys.Shift ? 10 : 1;
        var stepX=amount*CropBounds.Width/CropPixelsOriented.Width; var stepY=amount*CropBounds.Height/CropPixelsOriented.Height;
        var (dx,dy)=e.Key switch { Key.Left=>(-stepX,0d),Key.Right=>(stepX,0d),Key.Up=>(0d,-stepY),Key.Down=>(0d,stepY),_=>(0d,0d) };
        if(dx==0 && dy==0) return false;
        var crop=CropSelectionGeometry.Move(OrientedCrop,dx,dy,CropBounds);
        Page!.Edits.SetCrop(CropOrientation.ToSource(crop,CropSourceSize,Page.RotationQuarterTurns).Intersect(new(0,0,Page.SourceWidth,Page.SourceHeight)));
        CropCommitted?.Invoke(this,EventArgs.Empty); e.Handled=true; return true;
    }
}
