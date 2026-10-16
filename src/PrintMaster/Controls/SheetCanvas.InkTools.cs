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
    public static readonly DependencyProperty InkOpacityProperty=DependencyProperty.Register(nameof(InkOpacity),typeof(double),typeof(SheetCanvas),new PropertyMetadata(1d));
    public static readonly DependencyProperty EraserDiameterMmProperty=DependencyProperty.Register(nameof(EraserDiameterMm),typeof(double),typeof(SheetCanvas),new FrameworkPropertyMetadata(8d,FrameworkPropertyMetadataOptions.AffectsRender));
    public double InkOpacity {get=>(double)GetValue(InkOpacityProperty);set=>SetValue(InkOpacityProperty,value);}
    public double EraserDiameterMm {get=>(double)GetValue(EraserDiameterMmProperty);set=>SetValue(EraserDiameterMmProperty,value);}
    private SheetSnapshot? _erasureDraft;
    private BitmapSource? _erasurePreview;
    private PointD _eraseLast;
    private Point? _toolPointer;
    private int _erasureEpoch,_eraseRevision;
    private bool _eraseChanged;
    private CancellationTokenSource? _erasureCancellation;
    private Task _erasurePreviewTask=Task.CompletedTask;
    private SheetSnapshot? _erasurePreviewDraft;
    internal Task ErasureCommitTask {get;private set;}=Task.CompletedTask;

    private void BeginInkErasure(PointD point)
    {
        _erasureDraft=Page!.Document.Snapshot();_eraseRevision=Page.Document.Revision;
        _erasurePreview=CaptureContent();_eraseLast=point;_eraseChanged=false;_dragging=true;
        UpdateInkErasure(point);
    }
    private void UpdateInkErasure(PointD point)
    {
        if(_erasureDraft is not {} sheet) return;
        var objects=ImmutableArray.CreateBuilder<SheetObject>();var changed=false;
        foreach(var item in sheet.Objects)
        {
            if(item.Visible && !item.Locked && item is InkObject ink)
            {var result=InkStrokeEraser.Erase(ink,_eraseLast,point,Math.Max(.01,EraserDiameterMm)/2);changed|=result.Changed;objects.AddRange(result.Fragments);}
            else if(item.Visible && !item.Locked && item is ContentObject content)
            {var result=InkStrokeEraser.Erase(content,_eraseLast,point,Math.Max(.01,EraserDiameterMm)/2);changed|=result.Changed;objects.AddRange(result.Fragments);}
            else objects.Add(item);
        }
        _eraseLast=point;
        if(!changed)return;
        _eraseChanged=true;_erasureDraft=sheet with {Objects=objects.ToImmutable()};
        _erasureCancellation?.Cancel();_erasureCancellation?.Dispose();_erasureCancellation=new();
        _erasurePreviewTask=PrepareErasurePreviewAsync(_erasureDraft,++_erasureEpoch,_erasureCancellation.Token);
    }
    private async Task PrepareErasurePreviewAsync(SheetSnapshot draft,int epoch,CancellationToken token)
    {
        try
        {
            await _renderGate.WaitAsync(token);
            BitmapSource bitmap;
            try{bitmap=await Task.Run(()=>RenderErasurePreview(draft,token),token);}
            finally{_renderGate.Release();}
            if(!token.IsCancellationRequested && epoch==_erasureEpoch && _erasureDraft!=null){_erasurePreview=bitmap;_erasurePreviewDraft=draft;InvalidateVisual();}
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(epoch==_erasureEpoch)MovePreviewFailed?.Invoke(ex.Message);}
    }
    private BitmapSource RenderErasurePreview(SheetSnapshot draft,CancellationToken token)
    {
        using var bitmap=_rasterizer.RenderSheet(draft,120,false,token,1000);return BitmapBridge.ToWpf(bitmap);
    }
    private void CommitInkErasure()
    {
        if(!_eraseChanged || _erasureDraft is not {} draft || Page?.Document.Revision!=_eraseRevision){CancelGesture();return;}
        _dragging=false;if(IsMouseCaptured)ReleaseMouseCapture();
        var page=Page;
        _committing=true;
        try
        {
            // Commit geometry at mouse-up. Save/print immediately afterwards must see the new model.
            page.Document.SetErasedDrawingObjects(draft.Objects);
            if(_erasurePreview is {} current)_committedPresentation=new(page.Id,page.Document.Revision,current);
        }
        finally{_committing=false;}
        ErasureCommitTask=CompleteInkErasurePresentationAsync(draft,page,page.Document.Revision,_erasureEpoch,_erasurePreviewTask);
    }
    private async Task CompleteInkErasurePresentationAsync(SheetSnapshot draft,PrintPageItem page,int revision,int epoch,Task preview)
    {
        await preview;
        if(epoch!=_erasureEpoch || _erasureDraft!=draft || Page!=page || page.Document.Revision!=revision || !CanEdit)
        {if(epoch==_erasureEpoch)CancelGesture();return;}
        if(_erasurePreviewDraft!=draft || _erasurePreview is not {} presentation){CancelGesture();return;}
        // The final worker frame is ready. Never replace a newer model preview with a stale frame.
        _committedPresentation=PreviewFrame is {} frame && frame.PageId==page.Id && frame.Revision>=revision ? null : new(page.Id,revision,presentation);
        CancelGesture();
    }
    private void ResetInkErasure()
    {
        _erasureEpoch++;_erasureCancellation?.Cancel();_erasureCancellation?.Dispose();_erasureCancellation=null;
        _erasureDraft=null;_erasurePreview=null;_erasurePreviewDraft=null;_eraseChanged=false;
    }
    private void DrawToolFootprint(DrawingContext dc)
    {
        if(!CanEdit || IsColorPicking || Tool is not (EditorTool.Brush or EditorTool.Marker or EditorTool.Eraser) || _toolPointer is not {} point)return;
        var radius=(Tool==EditorTool.Eraser ? EraserDiameterMm : StrokeWidthMm)*Units/2;
        if(!double.IsFinite(radius) || radius<=0)return;
        var width=1/Math.Max(.01,ViewScale);
        dc.DrawEllipse(null,new Pen(Brushes.White,width*3),point,radius,radius);
        dc.DrawEllipse(null,new Pen(Brushes.DimGray,width),point,radius,radius);
    }
    protected override void OnMouseLeave(MouseEventArgs e){base.OnMouseLeave(e);_toolPointer=null;InvalidateVisual();}
}
