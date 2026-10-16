using PrintMaster.Models;
using PrintMaster.Services;
using System.Collections.Immutable;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace PrintMaster.Controls;

public sealed partial class AnnotationCanvas
{
    public static readonly DependencyProperty InkOpacityProperty=DependencyProperty.Register(nameof(InkOpacity),typeof(double),typeof(AnnotationCanvas),new PropertyMetadata(1d));
    public static readonly DependencyProperty EraserDiameterProperty=DependencyProperty.Register(nameof(EraserDiameter),typeof(double),typeof(AnnotationCanvas),new FrameworkPropertyMetadata(30d,FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ViewScaleProperty=DependencyProperty.Register(nameof(ViewScale),typeof(double),typeof(AnnotationCanvas),new FrameworkPropertyMetadata(1d,FrameworkPropertyMetadataOptions.AffectsRender));
    public double InkOpacity {get=>(double)GetValue(InkOpacityProperty);set=>SetValue(InkOpacityProperty,value);}
    public double EraserDiameter {get=>(double)GetValue(EraserDiameterProperty);set=>SetValue(EraserDiameterProperty,value);}
    public double ViewScale {get=>(double)GetValue(ViewScaleProperty);set=>SetValue(ViewScaleProperty,value);}
    private ImmutableArray<Annotation>? _erasedAnnotations;
    private int _eraseRevision;
    private PointD _eraseLast;
    private Point? _toolPointer;
    private bool _eraseChanged;

    private void BeginInkErasure(PointD point)
    {
        if(Page?.Document.PrimaryContent is not {Locked:false} || !Page.Edits.Crop.Contains(point))return;
        _erasedAnnotations=Page!.Edits.Annotations.ToImmutableArray();
        _eraseRevision=Page.Document.Revision;_eraseLast=point;_eraseChanged=false;_dragging=true;
        UpdateInkErasure(point);
    }
    private void UpdateInkErasure(PointD point)
    {
        if(_erasedAnnotations is not {} items) return;
        var next=ImmutableArray.CreateBuilder<Annotation>();
        foreach(var item in items)
        {
            var result=InkStrokeEraser.Erase(item,new Affine(1,0,0,1,0,0),_eraseLast,point,Math.Max(.01,EraserDiameter)/2,Page!.Edits.Crop);
            _eraseChanged|=result.Changed;next.AddRange(result.Fragments);
        }
        _erasedAnnotations=next.ToImmutable();_eraseLast=point;
    }
    private void CommitInkErasure()
    {
        if(_eraseChanged && Page?.Document.Revision==_eraseRevision && _erasedAnnotations is {} items && Page.Document.PrimaryContent is {} photo)
            Page.Document.SetContentEdits(photo.Id,photo.Edits with {Annotations=items},"Стереть часть рисунка");
    }
    private void ResetInkErasure(){_erasedAnnotations=null;_eraseChanged=false;}
    private void DrawToolFootprint(DrawingContext dc)
    {
        if(IsPrintPreview || IsColorPicking || !IsEnabled || Page?.IsImage!=true || Page.IsComposed || Preview==null
            || Tool is not (EditorTool.Brush or EditorTool.Marker or EditorTool.Eraser) || _toolPointer is not {} point) return;
        var source=_transform.Inverse(new(point.X,point.Y));
        var radius=(Tool==EditorTool.Eraser ? EraserDiameter : StrokeThickness)/2;
        if(!double.IsFinite(radius) || radius<=0) return;
        dc.PushTransform(new MatrixTransform(ToMatrix(_transform)));
        var width=1/Math.Max(.001,TransformScale()*ViewScale);
        dc.DrawEllipse(null,new Pen(Brushes.White,width*3),new(source.X,source.Y),radius,radius);
        dc.DrawEllipse(null,new Pen(Brushes.DimGray,width),new(source.X,source.Y),radius,radius);
        dc.Pop();
    }
    protected override void OnMouseLeave(MouseEventArgs e){base.OnMouseLeave(e);_toolPointer=null;InvalidateVisual();}
}
