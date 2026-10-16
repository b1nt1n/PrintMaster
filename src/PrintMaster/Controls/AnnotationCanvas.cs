using PrintMaster.Models;
using PrintMaster.Services;
using System.ComponentModel;
using System.Collections.Immutable;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PrintMaster.Controls;

public sealed partial class AnnotationCanvas : FrameworkElement
{
    public static readonly DependencyProperty PageProperty=DependencyProperty.Register(nameof(Page),typeof(PrintPageItem),typeof(AnnotationCanvas),new FrameworkPropertyMetadata(null,FrameworkPropertyMetadataOptions.AffectsRender,PageChanged));
    public static readonly DependencyProperty PreviewProperty=DependencyProperty.Register(nameof(Preview),typeof(BitmapSource),typeof(AnnotationCanvas),new FrameworkPropertyMetadata(null,FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsPrintPreviewProperty=DependencyProperty.Register(nameof(IsPrintPreview),typeof(bool),typeof(AnnotationCanvas),new FrameworkPropertyMetadata(false,FrameworkPropertyMetadataOptions.AffectsRender,PreviewModeChanged));
    public static readonly DependencyProperty SafeAreaProperty=DependencyProperty.Register(nameof(SafeArea),typeof(RectD),typeof(AnnotationCanvas),new FrameworkPropertyMetadata(new RectD(19,19,756,1084),FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ScalingProperty=DependencyProperty.Register(nameof(Scaling),typeof(ScaleMode),typeof(AnnotationCanvas),new FrameworkPropertyMetadata(ScaleMode.Fit,FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ToolProperty=DependencyProperty.Register(nameof(Tool),typeof(EditorTool),typeof(AnnotationCanvas),new FrameworkPropertyMetadata(EditorTool.Select,FrameworkPropertyMetadataOptions.AffectsRender,ToolChanged));
    public static readonly DependencyProperty InkColorProperty=DependencyProperty.Register(nameof(InkColor),typeof(string),typeof(AnnotationCanvas),new PropertyMetadata("#111827"));
    public static readonly DependencyProperty StrokeThicknessProperty=DependencyProperty.Register(nameof(StrokeThickness),typeof(double),typeof(AnnotationCanvas),new FrameworkPropertyMetadata(4d,FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TextSizeProperty=DependencyProperty.Register(nameof(TextSize),typeof(double),typeof(AnnotationCanvas),new PropertyMetadata(36d));
    public static readonly DependencyProperty CropRatioProperty=DependencyProperty.Register(nameof(CropRatio),typeof(double),typeof(AnnotationCanvas),new PropertyMetadata(0d));
    public PrintPageItem? Page { get=>(PrintPageItem?)GetValue(PageProperty); set=>SetValue(PageProperty,value); }
    public BitmapSource? Preview { get=>(BitmapSource?)GetValue(PreviewProperty); set=>SetValue(PreviewProperty,value); }
    public bool IsPrintPreview { get=>(bool)GetValue(IsPrintPreviewProperty); set=>SetValue(IsPrintPreviewProperty,value); }
    public RectD SafeArea { get=>(RectD)GetValue(SafeAreaProperty); set=>SetValue(SafeAreaProperty,value); }
    public ScaleMode Scaling { get=>(ScaleMode)GetValue(ScalingProperty); set=>SetValue(ScalingProperty,value); }
    public EditorTool Tool { get=>(EditorTool)GetValue(ToolProperty); set=>SetValue(ToolProperty,value); }
    public string InkColor { get=>(string)GetValue(InkColorProperty); set=>SetValue(InkColorProperty,value); }
    public double StrokeThickness { get=>(double)GetValue(StrokeThicknessProperty); set=>SetValue(StrokeThicknessProperty,value); }
    public double TextSize { get=>(double)GetValue(TextSizeProperty); set=>SetValue(TextSizeProperty,value); }
    public double CropRatio { get=>(double)GetValue(CropRatioProperty); set=>SetValue(CropRatioProperty,value); }

    public event EventHandler<TextEditRequest>? TextRequested;
    public event EventHandler? BeforeGesture;
    public event EventHandler? CropCommitted;
    public event EventHandler? SelectionChanged;
    public Annotation? SelectedAnnotation=>Page?.Edits.Annotations.FirstOrDefault(a=>a.Id==_selected);
    private void SelectAnnotation(Guid? id) { WholeImageSelected=false; _selection=id==null ? [] : [id.Value]; _selected=id; SelectionChanged?.Invoke(this,EventArgs.Empty); }
    private Annotation? _draft,_movingOriginal;
    private readonly List<PointD> _ink=new();
    private PointD _start;
    private RectD? _cropDraft;
    private bool _dragging;
    private Guid? _selected;
    private Guid[] _selection=[];
    public bool WholeImageSelected { get; private set; }
    public Guid[] SelectedAnnotationIds=>_selection;
    internal void SelectEverything()
    {
        if(IsPrintPreview || Page?.IsImage!=true) return;
        CancelGesture(); WholeImageSelected=true; _selection=Page.Edits.Annotations.Select(a=>a.Id).ToArray(); _selected=_selection.Length==0 ? null : _selection[^1];
        SelectionChanged?.Invoke(this,EventArgs.Empty); InvalidateVisual();
    }
    internal void ClearSelection() { WholeImageSelected=false; _selection=[]; SelectAnnotation(null); InvalidateVisual(); }
    internal void SelectClipboardAt(Point point)
    {
        if(IsPrintPreview || Page?.IsImage!=true || Page.IsComposed || Preview==null) return;
        CancelGesture(); var location=Map(point);
        if(!_placement.Contains(new(point.X,point.Y)) || !SafeArea.Contains(new(point.X,point.Y))) { ClearSelection(); return; }
        if(WholeImageSelected && Page.Edits.Crop.Contains(location)) return;
        var hit=HitAnnotation(location);
        if(hit!=null) { if(!_selection.Contains(hit.Id)) SelectAnnotation(hit.Id); InvalidateVisual(); }
        else if(_placement.Contains(new(point.X,point.Y))) SelectEverything();
        else ClearSelection();
    }
    internal void DeleteSelection()
    {
        if(IsPrintPreview || Page?.IsImage!=true) return;
        if(WholeImageSelected && Page.Document.PrimaryContent is {} content) Page.Document.RemoveObjects([content.Id]);
        else if(_selection.Length>0 && Page.Document.PrimaryContent is {} primary)
            Page.Document.SetContentEdits(primary.Id,primary.Edits with { Annotations=primary.Edits.Annotations.Where(a=>!_selection.Contains(a.Id)).ToImmutableArray() },"Удалить выделенные аннотации");
        ClearSelection();
    }
    internal PointD SourcePointAt(Point point)=>Map(point);
    internal RectD? SelectionBounds=>WholeImageSelected ? Page?.Edits.Crop : SelectedAnnotation is {} a ? Bounds(a) : null;
    internal Point SelectionMenuPoint
    {
        get
        {
            if(SelectionBounds is not {} b) return new(24,24);
            var corners=new[] {new PointD(b.X,b.Y),new(b.Right,b.Y),new(b.X,b.Bottom),new(b.Right,b.Bottom)}.Select(_transform.Transform).ToArray();
            return new(corners.Min(p=>p.X),corners.Min(p=>p.Y));
        }
    }
    internal bool CanOrderSelection(int direction)=>!IsPrintPreview && Page?.IsImage==true && !WholeImageSelected && _selection.Length>0
        && !SelectionOrder.Move(Page.Edits.Annotations,a=>_selection.Contains(a.Id),direction).SequenceEqual(Page.Edits.Annotations);
    internal void OrderSelection(int direction)
    {
        if(!CanOrderSelection(direction) || Page?.Document.PrimaryContent is not {} primary) return;
        var ordered=SelectionOrder.Move(Page.Edits.Annotations,a=>_selection.Contains(a.Id),direction);
        Page.Document.SetContentEdits(primary.Id,primary.Edits with { Annotations=ordered },"Порядок аннотаций");
    }
    internal void AlignSelection(string alignment)
    {
        if(IsPrintPreview || Page?.IsImage!=true || WholeImageSelected || SelectedAnnotation is not {} item) return;
        var aligned=AnnotationLayout.Align(item,Page.Edits.Crop,Page.RotationQuarterTurns,alignment);
        if(aligned!=item) Page.Edits.Replace(aligned);
    }
    private Affine _transform;
    private RectD _placement;
    private static readonly Brush SafeBrush=CreateSafeBrush();
    private static Brush CreateSafeBrush()
    {
        var brush=new SolidColorBrush(Color.FromRgb(111,128,147));
        brush.Freeze(); return brush;
    }

    public AnnotationCanvas() { Focusable=true; ClipToBounds=true; Cursor=Cursors.Arrow; Unloaded+=(_,_)=>CancelGesture(); IsEnabledChanged+=(_,_)=>{if(!IsEnabled)CancelGesture();}; }
    private static void PageChanged(DependencyObject d,DependencyPropertyChangedEventArgs e)
    {
        var canvas=(AnnotationCanvas)d;
        if(e.OldValue is PrintPageItem old) { old.PropertyChanged-=canvas.ModelChanged; old.Edits.PropertyChanged-=canvas.ModelChanged; }
        if(e.NewValue is PrintPageItem current) { current.PropertyChanged+=canvas.ModelChanged; current.Edits.PropertyChanged+=canvas.ModelChanged; }
        canvas.CancelGesture(); canvas.SelectAnnotation(null); canvas.InvalidateVisual();
    }
    private static void ToolChanged(DependencyObject d,DependencyPropertyChangedEventArgs e)
    {
        var canvas=(AnnotationCanvas)d; canvas.CancelGesture();
        canvas.Cursor=canvas.Tool is EditorTool.Select ? Cursors.Arrow : Cursors.Cross;
    }
    private static void PreviewModeChanged(DependencyObject d,DependencyPropertyChangedEventArgs e)
    {
        var canvas=(AnnotationCanvas)d; canvas.CancelGesture(); canvas.SelectAnnotation(null);
        canvas.Cursor=canvas.IsPrintPreview ? Cursors.Arrow : canvas.Tool==EditorTool.Select ? Cursors.Arrow : Cursors.Cross;
    }
    private void ModelChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(_erasedAnnotations!=null && Page?.Document.Revision!=_eraseRevision) CancelGesture();
        if(Page?.IsImage!=true) WholeImageSelected=false;
        _selection=_selection.Where(id=>Page?.Edits.Annotations.Any(a=>a.Id==id)==true).ToArray();
        if(_selected!=null && !_selection.Contains(_selected.Value)) _selected=null;
        SelectionChanged?.Invoke(this,EventArgs.Empty); InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); dc.DrawRectangle(Brushes.White,null,new(0,0,ActualWidth,ActualHeight));
        if(IsPrintPreview && Preview!=null)
        {
            dc.PushClip(new RectangleGeometry(ToRect(SafeArea))); dc.DrawImage(Preview,ToRect(SafeArea)); dc.Pop();
        }
        else if(Page!=null && Preview!=null)
        {
            if(Page.IsComposed)
            {
                dc.DrawImage(Preview,new(0,0,ActualWidth,ActualHeight));
                dc.DrawRectangle(null,new Pen(SafeBrush,1) { DashStyle=DashStyles.Dash },ToRect(SafeArea)); return;
            }
            var crop=DisplayCrop();
            var size=(Page.IsImage ? new SizeD(crop.Width,crop.Height) : new(Page.SourceWidth,Page.SourceHeight)).Rotated(Page.RotationQuarterTurns);
            _placement=PageLayout.Place(size,SafeArea,Tool==EditorTool.Crop && Page.IsImage ? ScaleMode.Fit : Scaling);
            dc.PushClip(new RectangleGeometry(ToRect(SafeArea)));
            if(!Page.IsImage) dc.DrawImage(Preview,ToRect(_placement));
            else
            {
                _transform=ImageGeometry.CreateTransform(crop,Page.RotationQuarterTurns,_placement);
                if(Tool!=EditorTool.Crop) dc.PushClip(new RectangleGeometry(ToRect(_placement)));
                dc.PushTransform(new MatrixTransform(ToMatrix(_transform)));
                dc.DrawImage(Preview,new(0,0,Page.SourceWidth,Page.SourceHeight));
                foreach(var annotation in _erasedAnnotations ?? Page.Edits.Annotations.ToImmutableArray())
                {
                    if(_movingOriginal?.Id==annotation.Id && _draft!=null) continue;
                    DrawAnnotation(dc,annotation);
                }
                if(WholeImageSelected) dc.DrawRectangle(null,new Pen(Brushes.RoyalBlue,1.5/TransformScale()) { DashStyle=DashStyles.Dash },ToRect(crop));
                if(_draft!=null) DrawAnnotation(dc,_draft);
                if(_selected is Guid id && Tool==EditorTool.Select)
                {
                    var selected=_draft?.Id==id ? _draft : Page.Edits.Annotations.FirstOrDefault(x=>x.Id==id);
                    if(selected!=null) dc.DrawRectangle(null,new Pen(Brushes.RoyalBlue,1.5/TransformScale()) { DashStyle=DashStyles.Dash },ToRect(Bounds(selected)));
                }
                dc.Pop();
                if(Tool!=EditorTool.Crop) dc.Pop();
            }
            dc.Pop();
            if(Page.IsImage && Tool==EditorTool.Crop) DrawCropOverlay(dc);
        }
        var safePen=new Pen(SafeBrush,1) { DashStyle=new DashStyle(new double[] { 5,4 },0) };
        if(Tool!=EditorTool.Crop) dc.DrawRectangle(null,safePen,ToRect(SafeArea));
        DrawToolFootprint(dc);
    }

    private RectD DisplayCrop() => Page!.IsImage ? Tool==EditorTool.Crop ? new(0,0,Page.SourceWidth,Page.SourceHeight) : Page.Edits.Crop : new(0,0,Page.SourceWidth,Page.SourceHeight);
    private double TransformScale() => Math.Max(0.001,Math.Sqrt(_transform.M11*_transform.M11+_transform.M12*_transform.M12));
    private PointD Map(Point screen)
    {
        var p=_transform.Inverse(new(screen.X,screen.Y));
        return new(Math.Clamp(p.X,0,Page!.SourceWidth),Math.Clamp(p.Y,0,Page.SourceHeight));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if(IsColorPicking) {e.Handled=RequestColorSampleAt(e.GetPosition(this));return;}
        if(IsPrintPreview || Page?.IsImage!=true || Page.IsComposed || Preview==null) return;
        var screen=e.GetPosition(this);
        if(Tool==EditorTool.Crop)
        {
            var handle=HitCropHandle(screen);
            if(handle==CropHandle.None) return;
            Focus(); BeginCropGesture(Map(screen),handle);
        }
        else
        {
            if(!SafeArea.Contains(new(screen.X,screen.Y))) return;
            Focus(); BeginGesture(Map(screen),e.ClickCount==2);
        }
        if(_dragging) CaptureMouse(); e.Handled=true;
    }
    internal void BeginGesture(PointD start,bool doubleClick=false)
    {
        if(!IsEnabled || IsPrintPreview || Page?.IsImage!=true || Page.IsComposed || Page.HasMissingSources || Page.Document.PrimaryContent?.Locked==true) return;
        if(Tool==EditorTool.Crop) { BeginCropGesture(start,CropHandle.Create); return; }
        BeforeGesture?.Invoke(this,EventArgs.Empty);
        _start=start; SelectAnnotation(null);
        if(Tool==EditorTool.Eraser){BeginInkErasure(start);InvalidateVisual();return;}
        if(Tool==EditorTool.Text)
        {
            TextRequested?.Invoke(this,new(_start,null)); return;
        }
        if(Tool is EditorTool.ObjectEraser or EditorTool.Select)
        {
            var hit=HitAnnotation(_start);
            if(Tool==EditorTool.ObjectEraser) { if(hit!=null) Page.Edits.Remove(hit.Id); return; }
            if(hit==null) { InvalidateVisual(); return; }
            SelectAnnotation(hit.Id);
            if(doubleClick && hit.Kind==AnnotationKind.Text) { TextRequested?.Invoke(this,new(hit.Start,hit)); return; }
            _movingOriginal=hit; _draft=hit;
        }
        else
        {
            _ink.Clear(); _ink.Add(_start);
            _draft=MakeDraft(_start);
        }
        _dragging=true; InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _toolPointer=e.GetPosition(this);InvalidateVisual();
        if(IsColorPicking) {Cursor=Cursors.Cross;return;}
        if(Tool==EditorTool.Crop && !_dragging) Cursor=CropCursor(HitCropHandle(e.GetPosition(this)));
        if(!_dragging || Page==null) return;
        UpdateGesture(Map(e.GetPosition(this))); e.Handled=true;
    }
    internal void UpdateGesture(PointD end)
    {
        if(!_dragging || Page==null) return;
        if(_erasedAnnotations!=null) UpdateInkErasure(end);
        else if(Tool==EditorTool.Crop)
        {
            UpdateCropGesture(end);
        }
        else if(_movingOriginal!=null) _draft=_movingOriginal.Move(end.X-_start.X,end.Y-_start.Y);
        else
        {
            if(Tool is EditorTool.Brush or EditorTool.Marker && (_ink.Count==0 || Distance(_ink[^1],end)>0.75)) _ink.Add(end);
            _draft=MakeDraft(end);
        }
        InvalidateVisual();
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e); if(!_dragging || Page==null) return;
        CommitGesture(); e.Handled=true;
    }
    internal void CommitGesture()
    {
        if(!_dragging || Page==null) return;
        if(_erasedAnnotations!=null) CommitInkErasure();
        else if(Tool==EditorTool.Crop && _cropDraft is RectD crop && crop.Width>0 && crop.Height>0)
        { Page.Edits.SetCrop(crop); CropCommitted?.Invoke(this,EventArgs.Empty); }
        else if(_movingOriginal!=null && _draft!=null && _draft!=_movingOriginal) Page.Edits.Replace(_draft);
        else if(_draft!=null && _movingOriginal==null && (_draft.Kind is AnnotationKind.Ink or AnnotationKind.Marker || Distance(_draft.Start,_draft.End)>2)) Page.Edits.Add(_draft);
        CancelGesture(); InvalidateVisual();
    }
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); if(_dragging) CancelGesture(); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if(e.Handled) return;
        base.OnKeyDown(e);
        if(e.Key==Key.Escape && _dragging) { CancelGesture(); InvalidateVisual(); e.Handled=true; return; }
        if(IsPrintPreview || Page?.IsImage!=true || Page.IsComposed) return;
        if(Tool==EditorTool.Crop && HandleCropKey(e)) return;
        if(Tool==EditorTool.Crop && Keyboard.Modifiers==(ModifierKeys.Control|ModifierKeys.Shift) && e.Key==Key.Z) {Page.Edits.Redo();e.Handled=true;return;}
        if(e.Key==Key.Delete && _selected is Guid id) { Page.Edits.Remove(id); SelectAnnotation(null); InvalidateVisual(); e.Handled=true; }
        if(Keyboard.Modifiers==ModifierKeys.Control && e.Key==Key.Z) { Page.Edits.Undo(); e.Handled=true; }
        if(Keyboard.Modifiers==ModifierKeys.Control && e.Key==Key.Y) { Page.Edits.Redo(); e.Handled=true; }
    }
    internal void CancelGesture()
    {
        _dragging=false; _draft=null; _movingOriginal=null; _cropDraft=null; _ink.Clear();
        _cropHandle=CropHandle.None;
        ResetInkErasure();
        if(IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
    }
    private Annotation MakeDraft(PointD end) => Annotation.Create(Tool switch
    { EditorTool.Arrow=>AnnotationKind.Arrow,EditorTool.Rectangle=>AnnotationKind.Rectangle,EditorTool.Ellipse=>AnnotationKind.Ellipse,
        EditorTool.Marker=>AnnotationKind.Marker,_=>AnnotationKind.Ink },_start,end,InkColor,StrokeThickness,points:_ink) with {Opacity=Tool is EditorTool.Brush or EditorTool.Marker ? InkOpacity : null};
    private Annotation? HitAnnotation(PointD p)
    {
        var tolerance=8/TransformScale();
        foreach(var a in Page!.Edits.Annotations.Reverse())
        {
            if(a.Kind is AnnotationKind.Ink or AnnotationKind.Marker)
            {
                if(a.Points.Count==1 && Distance(p,a.Points[0])<=tolerance+a.Thickness/2) return a;
                for(var i=1;i<a.Points.Count;i++) if(SegmentDistance(p,a.Points[i-1],a.Points[i])<=tolerance+a.Thickness/2) return a;
            }
            else if(a.Kind==AnnotationKind.Arrow) { if(SegmentDistance(p,a.Start,a.End)<=tolerance+a.Thickness/2) return a; }
            else
            {
                var b=Bounds(a); var expanded=new RectD(b.X-tolerance,b.Y-tolerance,b.Width+2*tolerance,b.Height+2*tolerance);
                if(expanded.Contains(p)) return a;
            }
        }
        return null;
    }
    private static double Distance(PointD a,PointD b)=>Math.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y));
    private static double SegmentDistance(PointD p,PointD a,PointD b)
    {
        var dx=b.X-a.X; var dy=b.Y-a.Y; var denominator=dx*dx+dy*dy;
        var t=denominator==0 ? 0 : Math.Clamp(((p.X-a.X)*dx+(p.Y-a.Y)*dy)/denominator,0,1);
        return Distance(p,new(a.X+t*dx,a.Y+t*dy));
    }
    private static RectD Bounds(Annotation a)
    {
        if(a.Kind is AnnotationKind.Ink or AnnotationKind.Marker && a.Points.Count!=0)
        {
            var minX=a.Points.Min(p=>p.X); var minY=a.Points.Min(p=>p.Y);
            return new(minX,minY,a.Points.Max(p=>p.X)-minX,a.Points.Max(p=>p.Y)-minY);
        }
        return RectD.FromPoints(a.Start,a.End);
    }
    private static Rect ToRect(RectD rect)=>new(rect.X,rect.Y,Math.Max(0,rect.Width),Math.Max(0,rect.Height));
    private static Matrix ToMatrix(Affine t)=>new(t.M11,t.M12,t.M21,t.M22,t.OffsetX,t.OffsetY);
    private void DrawAnnotation(DrawingContext dc,Annotation a)=>DrawStyledAnnotation(dc,a);
    private void DrawFormattedAnnotation(DrawingContext dc,Annotation a,Brush brush)
    {
        var logical=AnnotationTextLayout.LogicalText(a); var layout=TextLayoutService.Layout(logical); var style=logical.Style;
        var layoutScale=AnnotationTextLayout.LayoutScale(a);
        dc.PushClip(new RectangleGeometry(new Rect(a.Start.X,a.Start.Y,Math.Max(.01,a.End.X-a.Start.X),Math.Max(.01,a.End.Y-a.Start.Y))));
        foreach(var (line,index) in layout.Lines.Select((line,index)=>(line,index)))
        {
            var width=line.WidthMm/layoutScale;
            var x=a.Start.X+(style.Alignment switch { Models.TextAlignment.Center=>(a.End.X-a.Start.X-width)/2,Models.TextAlignment.Right=>a.End.X-a.Start.X-width,_=>0 });
            var text=new FormattedText(line.DisplayText,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,
                new Typeface(new FontFamily(layout.FontFamily),style.Italic ? FontStyles.Italic : FontStyles.Normal,style.Bold ? FontWeights.Bold : FontWeights.Normal,FontStretches.Normal),
                a.FontSize,brush,VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text,new(x,a.Start.Y+index*layout.LineHeightMm/layoutScale));
        }
        dc.Pop();
    }
}

public sealed class TextEditRequest(PointD location,Annotation? existing,Guid? contentId=null) : EventArgs
{
    public PointD Location { get; }=location;
    public Annotation? Existing { get; }=existing;
    public Guid? ContentId { get; }=contentId;
}

