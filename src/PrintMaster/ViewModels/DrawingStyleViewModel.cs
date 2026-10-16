using CommunityToolkit.Mvvm.ComponentModel;
using PrintMaster.Models;
using PrintMaster.Services;
using System.Globalization;

namespace PrintMaster.ViewModels;

public partial class PrintSessionViewModel
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasDrawingStyleError))] private string drawingStyleError="";
    public bool HasDrawingStyleError=>DrawingStyleError.Length!=0;

    // The creation defaults stay separate from selection properties. Resolve the live records
    // by ID because undo and document edits replace immutable objects and annotations.
    private IReadOnlyList<SheetObject> SelectedDrawingObjects
    {
        get
        {
            if(!IsSheetSelected) return [];
            var objects=SelectedObjects;
            return objects.Count>0 && objects.All(o=>o.Visible && !o.Locked && o is ShapeObject or InkObject) ? objects : [];
        }
    }
    private Annotation? SelectedDrawingAnnotation=>CanCropImage && !SourceImageSelected &&
        SelectedPage?.Document.PrimaryContent is { Visible:true,Locked:false } && SelectedSourceAnnotation is {} selected
        ? SelectedPage.Edits.Annotations.FirstOrDefault(a=>a.Id==selected.Id && a.Kind is AnnotationKind.Arrow or AnnotationKind.Rectangle or AnnotationKind.Ellipse or AnnotationKind.Ink or AnnotationKind.Marker)
        : null;
    private static StrokeStyle StrokeOf(SheetObject item)=>item switch
    {
        ShapeObject shape=>shape.Style,
        InkObject ink=>ink.Style,
        _=>throw new ArgumentException("У объекта нет контура.")
    };
    private IReadOnlyList<StrokeStyle> SelectedDrawingStyles=>SelectedDrawingObjects is { Count:>0 } objects
        ? objects.Select(StrokeOf).ToArray()
        : SelectedDrawingAnnotation is {} annotation ? [new(annotation.Color,annotation.Thickness,annotation.EffectiveOpacity,annotation.FillColor)] : [];
    private IReadOnlyList<StrokeStyle> SelectedFillStyles=>SelectedDrawingObjects is { Count:>0 } objects
        ? objects.OfType<ShapeObject>().Where(s=>s.Shape is SheetShapeKind.Rectangle or SheetShapeKind.Ellipse).Select(s=>s.Style).ToArray()
        : SelectedDrawingAnnotation is { Kind:AnnotationKind.Rectangle or AnnotationKind.Ellipse } annotation
            ? [new(annotation.Color,annotation.Thickness,annotation.EffectiveOpacity,annotation.FillColor)] : [];
    private static string Rgb(string color)=>TextColorService.FromColorRef(TextColorService.ToColorRef(color));
    public bool ShowSelectedDrawingProperties=>ShowEditorTools && Tool==EditorTool.Select && !IsEditingText && SelectedDrawingStyles.Count>0;
    public bool CanChangeDrawingStyle=>CanEdit && ShowSelectedDrawingProperties;
    public bool ShowSourceAnnotationDefaults=>CanCropImage && ShowEditorTools && ShowNewDrawingProperties;
    public string DrawingPropertiesTitle=>SelectedDrawingObjects.Count>1 ? "Выделенные фигуры и штрихи" : "Цвет и линия";
    // Imported images use a logical coordinate width of 1000, not native file pixels.
    public string DrawingWidthLabel=>IsSheetSelected ? "Толщина, мм" : "Толщина";
    public bool HasMixedDrawingColor=>SelectedDrawingStyles.Select(s=>Rgb(s.Color)).Distinct().Skip(1).Any();
    public bool HasMixedDrawingWidth=>SelectedDrawingStyles.Select(s=>s.WidthMm).Distinct().Skip(1).Any();
    public bool HasMixedDrawingOpacity=>SelectedDrawingStyles.Select(s=>s.Opacity).Distinct().Skip(1).Any();
    public bool ShowSelectedDrawingFillProperties=>ShowSelectedDrawingProperties && SelectedFillStyles.Count>0;
    public bool CanChangeDrawingFill=>CanChangeDrawingStyle && SelectedFillStyles.Count>0;
    public bool HasMixedDrawingFillColor=>SelectedFillStyles.Select(s=>s.FillColor==null ? null : Rgb(s.FillColor)).Distinct().Skip(1).Any();
    public bool HasSelectedDrawingFill=>SelectedDrawingHasFill!=false;
    public bool? SelectedDrawingHasFill
    {
        get
        {
            var styles=SelectedFillStyles;
            return styles.Count==0 || styles.All(s=>s.FillColor==null) ? false : styles.All(s=>s.FillColor!=null) ? true : null;
        }
        set
        {
            if(!CanChangeDrawingFill || value==null || value==SelectedDrawingHasFill) return;
            ChangeDrawingStyle(changeFill:true,fill:value==true ? SelectedDrawingFillColor : null);
        }
    }
    public string SelectedDrawingFillColor
    {
        get=>SelectedFillStyles.FirstOrDefault(s=>s.FillColor!=null)?.FillColor is {} fill ? Rgb(fill) : SelectedDrawingColor;
        set
        {
            if(!CanChangeDrawingFill) return;
            if(!TextColorService.TryNormalize(value,out var color)) { DrawingStyleError="Введите цвет RRGGBB или #RRGGBB."; return; }
            ChangeDrawingStyle(changeFill:true,fill:color);
        }
    }
    public string SelectedDrawingOpacityText
    {
        get=>HasMixedDrawingOpacity ? "" : SelectedDrawingStyles.FirstOrDefault() is {} style ? (style.Opacity*100).ToString("G",CultureInfo.CurrentCulture) : "";
        set
        {
            if(!CanChangeDrawingStyle) return;
            if(value.Trim()==SelectedDrawingOpacityText) { DrawingStyleError=""; return; }
            if(HasMixedDrawingOpacity && string.IsNullOrWhiteSpace(value)) return;
            if(!double.TryParse(value.Trim().Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out var percent) || !double.IsFinite(percent) || percent<0 || percent>100)
            { DrawingStyleError="Введите непрозрачность от 0 до 100 %."; return; }
            ChangeDrawingStyle(opacity:percent/100);
        }
    }
    public string DrawingSelectionHint
    {
        get
        {
            var mixed=new List<string>();
            if(HasMixedDrawingColor) mixed.Add("цвета"); if(HasMixedDrawingWidth) mixed.Add("толщины");
            if(HasMixedDrawingOpacity) mixed.Add("непрозрачности"); if(HasMixedDrawingFillColor) mixed.Add("заливки");
            return mixed.Count==0 ? "" : "Разные "+string.Join(", ",mixed)+". Новое значение — для выделения; заливка — только для прямоугольников и эллипсов.";
        }
    }
    public bool HasDrawingSelectionHint=>DrawingSelectionHint.Length!=0;
    public string SelectedDrawingColor
    {
        get=>SelectedDrawingStyles.FirstOrDefault() is {} style ? Rgb(style.Color) : "#111827";
        set
        {
            if(!CanChangeDrawingStyle) return;
            if(!TextColorService.TryNormalize(value,out var color)) { DrawingStyleError="Введите цвет RRGGBB или #RRGGBB."; return; }
            ChangeDrawingStyle(color:color);
        }
    }
    public string SelectedDrawingWidthText
    {
        get=>HasMixedDrawingWidth ? "" : SelectedDrawingStyles.FirstOrDefault()?.WidthMm.ToString("G",CultureInfo.CurrentCulture) ?? "";
        set
        {
            if(!CanChangeDrawingStyle) return;
            // Focusing then leaving the field must not round an existing physical width.
            if(value.Trim()==SelectedDrawingWidthText) { DrawingStyleError=""; return; }
            // An untouched mixed field is not an instruction to reset the group.
            if(HasMixedDrawingWidth && string.IsNullOrWhiteSpace(value)) return;
            var min=IsSheetSelected ? 0.05 : 0.1; var max=IsSheetSelected ? 20 : 200;
            if(!double.TryParse(value.Trim().Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out var width) ||
                !double.IsFinite(width) || width<min || width>max)
            {
                DrawingStyleError=$"Введите толщину от {min.ToString(CultureInfo.CurrentCulture)} до {max}{(IsSheetSelected ? " мм" : "")}.";
                return;
            }
            ChangeDrawingStyle(width:width);
        }
    }
    private void ChangeDrawingStyle(string? color=null,double? width=null,double? opacity=null,bool changeFill=false,string? fill=null)
    {
        if(!CanChangeDrawingStyle) return;
        DrawingStyleError="";
        var action=changeFill ? "Заливка фигур" : opacity!=null ? "Непрозрачность фигур и штрихов" : color!=null ? "Цвет фигур и штрихов" : "Толщина фигур и штрихов";
        StrokeStyle Change(StrokeStyle style)=>style with { Color=color ?? style.Color,WidthMm=width ?? style.WidthMm,Opacity=opacity ?? style.Opacity,FillColor=changeFill ? fill : style.FillColor };
        bool Changed(StrokeStyle style)=>color!=null && Rgb(style.Color)!=color || width!=null && style.WidthMm!=width || opacity!=null && style.Opacity!=opacity || changeFill && (style.FillColor==null ? null : Rgb(style.FillColor))!=fill;
        static bool Fillable(SheetObject item)=>item is ShapeObject { Shape:SheetShapeKind.Rectangle or SheetShapeKind.Ellipse };
        if(SelectedDrawingObjects is { Count:>0 } objects)
        {
            var replacements=objects.Where(o=>(!changeFill || Fillable(o)) && Changed(StrokeOf(o))).Select(o=>o switch
            {
                ShapeObject shape=>(SheetObject)(shape with { Style=Change(shape.Style) }),
                InkObject ink=>ink with { Style=Change(ink.Style) },
                _=>throw new ArgumentException("У объекта нет контура.")
            }).ToArray();
            if(replacements.Length==0) return;
            using var history=BeginHistoryEdit(action);
            SelectedPage!.Document.ReplaceObjects(replacements,action);
        }
        else if(SelectedDrawingAnnotation is {} annotation)
        {
            if(changeFill && annotation.Kind is not (AnnotationKind.Rectangle or AnnotationKind.Ellipse) || !Changed(new(annotation.Color,annotation.Thickness,annotation.EffectiveOpacity,annotation.FillColor))) return;
            using var history=BeginHistoryEdit(action);
            SelectedPage!.Edits.Replace(annotation with { Color=color ?? annotation.Color,Thickness=width ?? annotation.Thickness,Opacity=opacity ?? annotation.Opacity,FillColor=changeFill ? fill : annotation.FillColor });
        }
        Status="Свойства выделения изменены. Ctrl+Z — отменить.";
        NotifyDrawingStyleProperties();
    }
    private void NotifyDrawingStyleProperties()
    {
        OnPropertyChanged(nameof(DrawingContextKey));
        foreach(var name in new[] { nameof(ShowSelectedDrawingProperties),nameof(CanChangeDrawingStyle),nameof(ShowSourceAnnotationDefaults),nameof(DrawingPropertiesTitle),nameof(DrawingWidthLabel),nameof(DrawingWidthMinimum),nameof(DrawingWidthMaximum),nameof(DrawingWidthUnit),nameof(HasMixedDrawingColor),nameof(HasMixedDrawingWidth),nameof(DrawingSelectionHint),nameof(HasDrawingSelectionHint),nameof(SelectedDrawingColor),nameof(SelectedDrawingWidthText),nameof(HasMixedDrawingOpacity),nameof(SelectedDrawingOpacityText),nameof(ShowSelectedDrawingFillProperties),nameof(CanChangeDrawingFill),nameof(HasMixedDrawingFillColor),nameof(HasSelectedDrawingFill),nameof(SelectedDrawingHasFill),nameof(SelectedDrawingFillColor) }) OnPropertyChanged(name);
    }
}
