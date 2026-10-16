using PrintMaster.Models;
using PrintMaster.Services;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PrintMaster.Controls;

// Page coordinates are always mm, independently of the parent's LayoutTransform (zoom).
public sealed partial class SheetCanvas : FrameworkElement
{
    public static readonly DependencyProperty PageProperty=DependencyProperty.Register(nameof(Page),typeof(PrintPageItem),typeof(SheetCanvas),new FrameworkPropertyMetadata(null,FrameworkPropertyMetadataOptions.AffectsRender,PageChanged));
    public static readonly DependencyProperty PreviewProperty=DependencyProperty.Register(nameof(Preview),typeof(BitmapSource),typeof(SheetCanvas),new FrameworkPropertyMetadata(null,FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PreviewFrameProperty=DependencyProperty.Register(nameof(PreviewFrame),typeof(EditorPreviewFrame),typeof(SheetCanvas),new FrameworkPropertyMetadata(null,FrameworkPropertyMetadataOptions.AffectsRender,FrameChanged));
    public static readonly DependencyProperty SafeAreaProperty=DependencyProperty.Register(nameof(SafeArea),typeof(RectD),typeof(SheetCanvas),new FrameworkPropertyMetadata(default(RectD),FrameworkPropertyMetadataOptions.AffectsRender,SnapGeometryChanged));
    public static readonly DependencyProperty ToolProperty=DependencyProperty.Register(nameof(Tool),typeof(EditorTool),typeof(SheetCanvas),new FrameworkPropertyMetadata(EditorTool.Select,FrameworkPropertyMetadataOptions.AffectsRender,ModeChanged));
    public static readonly DependencyProperty IsPrintPreviewProperty=DependencyProperty.Register(nameof(IsPrintPreview),typeof(bool),typeof(SheetCanvas),new FrameworkPropertyMetadata(false,FrameworkPropertyMetadataOptions.AffectsRender,ModeChanged));
    public static readonly DependencyProperty SelectedObjectIdProperty=DependencyProperty.Register(nameof(SelectedObjectId),typeof(Guid?),typeof(SheetCanvas),new FrameworkPropertyMetadata(null,FrameworkPropertyMetadataOptions.AffectsRender|FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty SelectedObjectIdsProperty=DependencyProperty.Register(nameof(SelectedObjectIds),typeof(Guid[]),typeof(SheetCanvas),new FrameworkPropertyMetadata(Array.Empty<Guid>(),FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty InkColorProperty=DependencyProperty.Register(nameof(InkColor),typeof(string),typeof(SheetCanvas),new PropertyMetadata("#111827"));
    public static readonly DependencyProperty StrokeWidthMmProperty=DependencyProperty.Register(nameof(StrokeWidthMm),typeof(double),typeof(SheetCanvas),new FrameworkPropertyMetadata(0.5d,FrameworkPropertyMetadataOptions.AffectsRender));
    public PrintPageItem? Page { get=>(PrintPageItem?)GetValue(PageProperty); set=>SetValue(PageProperty,value); }
    public BitmapSource? Preview { get=>(BitmapSource?)GetValue(PreviewProperty); set=>SetValue(PreviewProperty,value); }
    public EditorPreviewFrame? PreviewFrame { get=>(EditorPreviewFrame?)GetValue(PreviewFrameProperty); set=>SetValue(PreviewFrameProperty,value); }
    public RectD SafeArea { get=>(RectD)GetValue(SafeAreaProperty); set=>SetValue(SafeAreaProperty,value); }
    public EditorTool Tool { get=>(EditorTool)GetValue(ToolProperty); set=>SetValue(ToolProperty,value); }
    public bool IsPrintPreview { get=>(bool)GetValue(IsPrintPreviewProperty); set=>SetValue(IsPrintPreviewProperty,value); }
    public Guid? SelectedObjectId { get=>(Guid?)GetValue(SelectedObjectIdProperty); set=>SetValue(SelectedObjectIdProperty,value); }
    public Guid[] SelectedObjectIds { get=>(Guid[])GetValue(SelectedObjectIdsProperty); set=>SetValue(SelectedObjectIdsProperty,value); }
    public event EventHandler<SheetSelectionRequest>? SelectionRequested;
    public string InkColor { get=>(string)GetValue(InkColorProperty); set=>SetValue(InkColorProperty,value); }
    public double StrokeWidthMm { get=>(double)GetValue(StrokeWidthMmProperty); set=>SetValue(StrokeWidthMmProperty,value); }
    public event EventHandler<SheetTextEditRequest>? TextRequested;
    public event EventHandler<TextEditRequest>? SourceTextRequested;
    public event EventHandler? BeforeGesture;
    private PointD _start,_end;
    private SheetObject? _moving,_draft;
    private SheetObject[] _movingGroup=[];
    private readonly List<PointD> _points=[];
    private bool _dragging,_committing,_resizing;
    private CancellationTokenSource? _moveCancellation;
    private BitmapSource? _background,_movingBitmap;
    private EditorPreviewFrame? _committedPresentation;
    internal bool HasCommittedPresentation=>_committedPresentation!=null;
    private string? _movePreviewError;
    private readonly SemaphoreSlim _renderGate=new(1,1);
    private readonly PageRasterizer _rasterizer=new(new PdfiumRenderer());
    internal bool MovePreviewReady=>_background!=null && (IsBoxTransform ? _draft is ShapeObject or InkObject || _objectPreview!=null : _movingBitmap!=null);
    internal string? MovePreviewError=>_movePreviewError;
    public event Action<string>? MovePreviewFailed;
    private const double Units=SheetEditing.DipPerMm;
    public SheetCanvas() { Focusable=true; ClipToBounds=true; Unloaded+=(_,_)=>{ CancelGesture(); _committedPresentation=null; }; IsEnabledChanged+=(_,_)=>{if(!IsEnabled)CancelGesture();}; }
    private static void FrameChanged(DependencyObject d,DependencyPropertyChangedEventArgs e)
    {
        var canvas=(SheetCanvas)d;
        if(e.NewValue is EditorPreviewFrame frame && canvas._committedPresentation is {} held
            && frame.PageId==held.PageId && frame.PageId==canvas.Page?.Id && frame.Revision>=held.Revision)
            canvas._committedPresentation=null;
    }
    private static void PageChanged(DependencyObject d,DependencyPropertyChangedEventArgs e)
    {
        var c=(SheetCanvas)d;
        if(e.OldValue is PrintPageItem old) old.Document.PropertyChanged-=c.DocumentChanged;
        if(e.NewValue is PrintPageItem page) page.Document.PropertyChanged+=c.DocumentChanged;
        c.CancelGesture(); c._committedPresentation=null; c.SetCurrentValue(SelectedObjectIdProperty,null);
    }
    private static void ModeChanged(DependencyObject d,DependencyPropertyChangedEventArgs e)
    {
        var c=(SheetCanvas)d; c.CancelGesture(); if(e.Property==IsPrintPreviewProperty) c._committedPresentation=null; c.Cursor=c.Tool==EditorTool.Select || c.IsPrintPreview ? Cursors.Arrow : Cursors.Cross;
    }
    private void DocumentChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName!=nameof(SheetDocument.Revision)) return;
        if(!_committing) CancelGesture();
        if(Page?.Document.Objects.Any(o=>o.Id==SelectedObjectId)!=true) SetCurrentValue(SelectedObjectIdProperty,null);
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); var full=new Rect(0,0,ActualWidth,ActualHeight); dc.DrawRectangle(Brushes.White,null,full);
        if(IsPrintPreview) { if((PreviewFrame?.Bitmap ?? Preview) is {} printBitmap) dc.DrawImage(printBitmap,Rect(SafeArea)); }
        else
        {
            DrawContent(dc,full);
            if(Page?.Document.Paper.Notebook is {} && Page.IsSheetLayout)
            {
                foreach(var guide in NotebookGeometry.Guides(Page.Document.Paper))
                {
                    var pen=guide.IsFold ? new Pen(Brushes.DimGray,1) { DashStyle=DashStyles.Dash }
                        : new Pen(new SolidColorBrush(guide.IsMargin ? Color.FromArgb(110,180,68,82) : Color.FromArgb(75,87,141,185)),guide.IsMargin ? 1 : 0.7);
                    dc.DrawLine(pen,new(guide.Start.X*Units,guide.Start.Y*Units),new(guide.End.X*Units,guide.End.Y*Units));
                }
            }
            if(Page!=null)
            {
                var safe=new RectD(SafeArea.X/Units,SafeArea.Y/Units,SafeArea.Width/Units,SafeArea.Height/Units);
                foreach(var item in Page.Document.Objects.Where(o=>o.Visible))
                {
                    var visible=_draft?.Id==item.Id ? _draft : _movingGroup.Any(o=>o.Id==item.Id) ? TranslateDraft(item) : item; var bounds=SheetEditing.VisualBounds(visible);
                    if(SheetEditing.Outside(visible,safe,Page.Document.Paper) || visible is TextObject text && TextLayoutService.Overflows(text)) dc.DrawRectangle(null,new Pen(Brushes.DarkOrange,1.5) { DashStyle=DashStyles.Dash },MmRect(bounds));
                    if((SelectedObjectIds.Contains(item.Id) || SelectedObjectId==item.Id) && Tool==EditorTool.Select)
                    {
                        DrawSelection(dc,visible,SelectedObjectIds.Length<=1);
                    }
                }
            }
        }
        dc.DrawRectangle(null,new Pen(Brushes.SlateGray,1) { DashStyle=DashStyles.Dash },Rect(SafeArea));
        if(!IsPrintPreview) DrawSnapGuides(dc);
        DrawToolFootprint(dc);
    }
    private void DrawContent(DrawingContext dc,Rect full)
    {
        dc.DrawRectangle(Brushes.White,null,full);
        if(_erasureDraft!=null && _erasurePreview!=null){dc.DrawImage(_erasurePreview,full);return;}
        if(IsBoxTransform && MovePreviewReady && _draft!=null)
        {
            dc.DrawImage(_background,full); DrawTransformedObject(dc,_draft);
        }
        else if(MovePreviewReady && _moving!=null && _draft!=null)
        {
            dc.DrawImage(_background,full);
            var original=_moving.Placement.BoundsMm; var current=_draft.Placement.BoundsMm;
            dc.PushTransform(new TranslateTransform((current.X-original.X)*Units,(current.Y-original.Y)*Units)); dc.DrawImage(_movingBitmap,full); dc.Pop();
        }
        else if(_committedPresentation is {} held && held.PageId==Page?.Id) dc.DrawImage(held.Bitmap,full);
        else if(((PreviewFrame?.PageId==Page?.Id ? PreviewFrame?.Bitmap : null) ?? Preview) is {} bitmap) dc.DrawImage(bitmap,full);
        if(_draft!=null && _moving==null) DrawDraft(dc,_draft);
    }
    private BitmapSource CaptureContent()
    {
        var width=Math.Max(1,ActualWidth); var height=Math.Max(1,ActualHeight);
        var scale=Math.Min(1,1600/Math.Max(width,height));
        var visual=new DrawingVisual();
        using(var dc=visual.RenderOpen())
        { dc.PushTransform(new ScaleTransform(scale,scale)); DrawContent(dc,new(0,0,width,height)); dc.Pop(); }
        var bitmap=new RenderTargetBitmap(Math.Max(1,(int)Math.Ceiling(width*scale)),Math.Max(1,(int)Math.Ceiling(height*scale)),96,96,PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if(IsColorPicking) {e.Handled=RequestColorSampleAt(e.GetPosition(this));return;}
        if(!CanEdit) return;
        Focus(); BeginGesture(Map(e.GetPosition(this)),e.ClickCount==2); if(_dragging) CaptureMouse(); e.Handled=true;
    }
    private bool CanEdit=>IsEnabled && !IsPrintPreview && Page?.IsSheetLayout==true && !Page.HasMissingSources;
    private static PointD Map(Point p)=>new(p.X/Units,p.Y/Units);
    internal void SelectClipboardAt(Point point)
    {
        if(!CanEdit) return; CancelGesture(); var location=Map(point);
        var hit=ObjectsAt(location).FirstOrDefault();
        if(hit!=null && SelectedObjectIds.Contains(hit.Id)) return;
        SelectionRequested?.Invoke(this,new(hit?.Id,false)); SetCurrentValue(SelectedObjectIdProperty,hit?.Id); InvalidateVisual();
    }
    internal void BeginGesture(PointD point,bool doubleClick=false,ModifierKeys? modifiers=null)
    {
        if(!CanEdit) return; _movePreviewError=null; BeforeGesture?.Invoke(this,EventArgs.Empty); CancelGesture(); _start=point; _end=point;
        var selected=Page!.Document.Objects.FirstOrDefault(t=>t.Id==SelectedObjectId && t.Visible && !t.Locked);
        var handle=Tool==EditorTool.Select && SelectedObjectIds.Length<=1 && selected!=null ? HitHandle(selected,point) : ObjectHandle.None;
        if(handle!=ObjectHandle.None && selected!=null)
        {
            _moving=selected; _draft=selected; _dragging=true; _activeHandle=handle; _resizing=handle==ObjectHandle.TextWidth;
            _moveCancellation=new();
            if(_resizing) _=PrepareMoveAsync(Page.Document.Snapshot(),selected,_moveCancellation.Token);
            else _=PrepareBoxTransformAsync(Page.Document.Snapshot(),selected,_moveCancellation.Token);
            InvalidateVisual(); return;
        }
        var hit=ObjectsAt(point).FirstOrDefault(); var keys=modifiers ?? Keyboard.Modifiers;
        if((Tool==EditorTool.Text || Tool==EditorTool.Select && doubleClick) && hit is ContentObject photo)
        {
            var annotation=AnnotationTextLayout.HitText(photo,point,photo.Placement.BoundsMm);
            if(annotation!=null)
            { SourceTextRequested?.Invoke(this,new(annotation.Start,annotation,photo.Id)); return; }
        }
        if(Tool==EditorTool.Text) { TextRequested?.Invoke(this,new(point,hit as TextObject)); return; }
        if(Tool==EditorTool.Eraser){BeginInkErasure(point);InvalidateVisual();return;}
        if(Tool==EditorTool.ObjectEraser) { if(hit!=null) Page.Document.RemoveObject(hit.Id); return; }
        if(Tool==EditorTool.Select)
        {
            if(hit is TextObject { Flow.Notebook:true } linked && !doubleClick && !keys.HasFlag(ModifierKeys.Alt) && !keys.HasFlag(ModifierKeys.Control) && !keys.HasFlag(ModifierKeys.Shift))
            { SelectionRequested?.Invoke(this,new(linked.Id,false)); SetCurrentValue(SelectedObjectIdProperty,linked.Id); InvalidateVisual(); return; }
            if(keys.HasFlag(ModifierKeys.Alt))
            {
                var id=ObjectTransform.NextHit(Page.Document.Objects,point,SelectedObjectId,HitToleranceMm);
                SelectionRequested?.Invoke(this,new(id,false)); SetCurrentValue(SelectedObjectIdProperty,id); InvalidateVisual(); return;
            }
            if(keys.HasFlag(ModifierKeys.Control) || keys.HasFlag(ModifierKeys.Shift))
            {
                if(hit!=null) SelectionRequested?.Invoke(this,new(hit.Id,true)); InvalidateVisual(); return;
            }
            if(hit!=null && !SelectedObjectIds.Contains(hit.Id)) SelectionRequested?.Invoke(this,new(hit.Id,false));
            if(hit==null) SelectionRequested?.Invoke(this,new(null,false));
            SetCurrentValue(SelectedObjectIdProperty,hit?.Id);
            if(hit==null) return;
            if(doubleClick && hit is TextObject text) { TextRequested?.Invoke(this,new(point,text)); return; }
            _moving=hit; _draft=hit; _dragging=true;
            _movingGroup=Page.Document.Objects.Where(o=>SelectedObjectIds.Contains(o.Id) && o.Visible && !o.Locked).ToArray();
            if(_movingGroup.Length==0) _movingGroup=[hit];
            _moveCancellation=new(); _=PrepareMoveAsync(Page.Document.Snapshot(),hit,_moveCancellation.Token);
        }
        else if(Tool!=EditorTool.Crop)
        {
            SetCurrentValue(SelectedObjectIdProperty,null); _points.Clear(); _points.Add(point); _dragging=true; _draft=MakeDraft(point);
        }
        InvalidateVisual();
    }
    private async Task PrepareMoveAsync(SheetSnapshot sheet,SheetObject selected,CancellationToken token)
    {
        try
        {
            await _renderGate.WaitAsync(token);
            try
            {
                var movingItems=_resizing && _draft?.Id==selected.Id ? new[] { _draft } : _movingGroup.Length>0 ? _movingGroup : [selected];
                var ids=movingItems.Select(o=>o.Id).ToHashSet();
                var images=await Task.Run(()=>
                {
                    using var background=_rasterizer.RenderSheet(sheet with { Objects=sheet.Objects.Where(o=>!ids.Contains(o.Id)).ToImmutableArray() },120,false,token,1600);
                    using var moving=_rasterizer.RenderSheet(sheet with { Objects=movingItems.ToImmutableArray() },120,false,token,1600,true);
                    return (BitmapBridge.ToWpf(background),BitmapBridge.ToWpf(moving));
                },token);
                if(!token.IsCancellationRequested && _moving?.Id==selected.Id) { (_background,_movingBitmap)=images; InvalidateVisual(); }
            }
            finally { _renderGate.Release(); }
        }
        catch(OperationCanceledException) { }
        catch(Exception ex) when(ex is not OperationCanceledException)
        {
            _movePreviewError=ex.Message;
            MovePreviewFailed?.Invoke(ex.Message);
        }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _toolPointer=e.GetPosition(this);InvalidateVisual();
        if(IsColorPicking) {Cursor=Cursors.Cross;return;}
        if(!_dragging) { UpdateHandleCursor(Map(e.GetPosition(this))); return; }
        UpdateGesture(Map(e.GetPosition(this))); e.Handled=true;
    }
    internal void UpdateGesture(PointD point,ModifierKeys? modifiers=null)
    {
        if(!_dragging || !CanEdit) return;
        _end=point;
        var shift=(modifiers ?? Keyboard.Modifiers).HasFlag(ModifierKeys.Shift);
        if(_erasureDraft!=null)UpdateInkErasure(point);
        else if(IsBoxTransform && _moving!=null)
            _draft=_activeHandle==ObjectHandle.Rotate ? ObjectTransform.Rotate(_moving,_start,point,shift) : ObjectTransform.Resize(_moving,_activeHandle,_start,point,shift);
        else if(_resizing && _moving is TextObject text)
        {
            var width=Math.Max(5,text.Placement.BoundsMm.Width+point.X-_start.X); var height=TextLayoutService.Layout(text.Text,text.Style,width).HeightMm;
            _draft=text with { Placement=text.Placement with { BoundsMm=text.Placement.BoundsMm with { Width=width,Height=height } } };
            _moveCancellation?.Cancel(); _moveCancellation?.Dispose(); _moveCancellation=new();
            _=ResizePreviewAsync(Page!.Document.Snapshot(),text,_moveCancellation.Token);
        }
        else if(_moving!=null)
        {
            UpdateMoveSnapping(modifiers ?? Keyboard.Modifiers);
            _draft=TranslateDraft(_moving);
        }
        else
        {
            if(Tool is EditorTool.Brush or EditorTool.Marker && Distance(_points[^1],point)>0.15) _points.Add(point);
            _draft=MakeDraft(point);
        }
        InvalidateVisual();
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { base.OnMouseLeftButtonUp(e); if(!_dragging) return; CommitGesture(); e.Handled=true; }
    internal void CommitGesture()
    {
        if(!_dragging || !CanEdit) { CancelGesture(); return; }
        if(_erasureDraft!=null){CommitInkErasure();return;}
        if(_moving!=null && _moving==_draft) { CancelGesture(); return; }
        var beforeRevision=Page!.Document.Revision;
        try { EnsureGesturePreview(); }
        catch(Exception ex) { _movePreviewError=ex.Message; MovePreviewFailed?.Invoke(ex.Message); }
        var presentation=_moving==null || MovePreviewReady ? CaptureContent() : null;
        _committing=true;
        try
        {
            if(_moving!=null && _draft!=null && _moving!=_draft)
            {
                if(IsBoxTransform) Page!.Document.ReplaceObject(_draft,_activeHandle==ObjectHandle.Rotate ? "Повернуть объект" : "Изменить размер объекта");
                else if(_resizing) Page!.Document.ReplaceObject(_draft,"Изменить ширину текста");
                else Page!.Document.ReplaceObjects(_movingGroup.Select(TranslateDraft),"Переместить выделенные объекты");
            }
            else if(_moving==null && _draft!=null && (_draft is InkObject || Distance(_start,_end)>1)) Page!.Document.AddObject(_draft);
            if(_draft!=null) SetCurrentValue(SelectedObjectIdProperty,_draft.Id);
            if(Page.Document.Revision!=beforeRevision && presentation!=null)
                _committedPresentation=new(Page.Id,Page.Document.Revision,presentation);
        }
        finally { _committing=false; CancelGesture(); }
    }
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); if(_dragging) CancelGesture(); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if(e.Handled) return;
        base.OnKeyDown(e);
        if(e.Key==Key.Escape) { CancelGesture(); SetCurrentValue(SelectedObjectIdProperty,null); e.Handled=true; }
        if(!CanEdit) return;
        var item=Page!.Document.Objects.FirstOrDefault(o=>o.Id==SelectedObjectId && !o.Locked);
        if(e.Key==Key.Delete && item!=null) { Page.Document.RemoveObject(item.Id); e.Handled=true; }
        if(Keyboard.Modifiers==ModifierKeys.Control && e.Key==Key.Z) { Page.Edits.Undo(); e.Handled=true; }
        if(Keyboard.Modifiers==ModifierKeys.Control && e.Key==Key.Y) { Page.Edits.Redo(); e.Handled=true; }
    }
    internal void CancelGesture()
    {
        _dragging=false; _resizing=false; _activeHandle=ObjectHandle.None; _objectPreview=null; _moving=null; _draft=null; _movingGroup=[]; _background=null; _movingBitmap=null;
        ResetSnapping();
        ResetInkErasure();
        _moveCancellation?.Cancel(); _moveCancellation?.Dispose(); _moveCancellation=null;
        if(IsMouseCaptured) ReleaseMouseCapture(); InvalidateVisual();
    }
    private SheetObject MakeDraft(PointD end)
    {
        var style=new StrokeStyle(InkColor,StrokeWidthMm,Tool is EditorTool.Brush or EditorTool.Marker ? InkOpacity : 1);
        if(Tool is EditorTool.Brush or EditorTool.Marker)
        {
            var minX=_points.Min(p=>p.X); var minY=_points.Min(p=>p.Y); var width=Math.Max(0.01,_points.Max(p=>p.X)-minX); var height=Math.Max(0.01,_points.Max(p=>p.Y)-minY);
            return new InkObject(_draft?.Id ?? Guid.NewGuid(),new(new(minX,minY,width,height)),_points.Select(p=>new PointD((p.X-minX)/width,(p.Y-minY)/height)).ToImmutableArray(),style);
        }
        var bounds=new RectD(Math.Min(_start.X,end.X),Math.Min(_start.Y,end.Y),Math.Max(0.01,Math.Abs(end.X-_start.X)),Math.Max(0.01,Math.Abs(end.Y-_start.Y)));
        var shape=Tool==EditorTool.Arrow ? SheetShapeKind.Arrow : Tool==EditorTool.Ellipse ? SheetShapeKind.Ellipse : SheetShapeKind.Rectangle;
        var angle=shape==SheetShapeKind.Arrow ? (Math.Atan2(end.Y-_start.Y,end.X-_start.X)-Math.Atan2(bounds.Height,bounds.Width))*180/Math.PI : 0;
        return new ShapeObject(_draft?.Id ?? Guid.NewGuid(),new(bounds,angle),shape,style);
    }
    private SheetObject TranslateDraft(SheetObject item)=>item with { Placement=item.Placement with { BoundsMm=item.Placement.BoundsMm with { X=item.Placement.BoundsMm.X+_moveDelta.X,Y=item.Placement.BoundsMm.Y+_moveDelta.Y } } };
    private void DrawDraft(DrawingContext dc,SheetObject item)
    {
        var style=item is InkObject ink ? ink.Style : ((ShapeObject)item).Style;
        var brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(style.Color)) { Opacity=style.Opacity };
        var pen=new Pen(brush,style.WidthMm*Units) { StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round };
        var b=item.Placement.BoundsMm; var rect=MmRect(b);
        dc.PushTransform(new RotateTransform(item.Placement.RotationDegrees,rect.X+rect.Width/2,rect.Y+rect.Height/2));
        if(item is InkObject drawing)
        {
            var points=drawing.Points.Select(p=>new Point((b.X+p.X*b.Width)*Units,(b.Y+p.Y*b.Height)*Units)).ToArray();
            for(var i=1;i<points.Length;i++) dc.DrawLine(pen,points[i-1],points[i]);
            if(points.Length==1) dc.DrawEllipse(brush,null,points[0],pen.Thickness/2,pen.Thickness/2);
        }
        else if(item is ShapeObject shape)
        {
            var fill=style.FillColor is {} fillColor ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(fillColor)) { Opacity=style.Opacity } : null;
            if(shape.Shape==SheetShapeKind.Rectangle) dc.DrawRectangle(fill,pen,rect);
            else if(shape.Shape==SheetShapeKind.Ellipse) dc.DrawEllipse(fill,pen,new(rect.X+rect.Width/2,rect.Y+rect.Height/2),rect.Width/2,rect.Height/2);
            else
            {
                var start=new Point(rect.X,rect.Y); var end=new Point(rect.Right,rect.Bottom); dc.DrawLine(pen,start,end);
                var angle=Math.Atan2(rect.Height,rect.Width); var length=Math.Max(3,style.WidthMm*4)*Units;
                dc.DrawLine(pen,end,new(end.X-length*Math.Cos(angle-0.5),end.Y-length*Math.Sin(angle-0.5)));
                dc.DrawLine(pen,end,new(end.X-length*Math.Cos(angle+0.5),end.Y-length*Math.Sin(angle+0.5)));
            }
        }
        dc.Pop();
    }
    private static double Distance(PointD a,PointD b)=>Math.Sqrt(Math.Pow(a.X-b.X,2)+Math.Pow(a.Y-b.Y,2));
    private static Rect Rect(RectD b)=>new(b.X,b.Y,b.Width,b.Height);
    private static Rect MmRect(RectD b)=>new(b.X*Units,b.Y*Units,b.Width*Units,b.Height*Units);
    private static Rect ResizeHandle(SheetObject text) { var b=text.Placement.BoundsMm; return new(b.Right*Units-4,(b.Y+b.Height/2)*Units-4,8,8); }
    private async Task ResizePreviewAsync(SheetSnapshot sheet,TextObject text,CancellationToken token)
    {
        try { await Task.Delay(100,token); await PrepareMoveAsync(sheet,text,token); } catch(OperationCanceledException) { }
    }
}
public sealed record SheetTextEditRequest(PointD Location,TextObject? Existing);
public sealed record SheetSelectionRequest(Guid? Id,bool Toggle);
