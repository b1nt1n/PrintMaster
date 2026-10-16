using PrintMaster.Models;
using PrintMaster.Services;
using System.Runtime.CompilerServices;

namespace PrintMaster.ViewModels;

public partial class PrintSessionViewModel
{
    private double _brushOpacity=1,_markerThickness=16,_markerOpacity=85d/255,_eraserDiameter=30;

    // Source annotations use the imported photo's normalized coordinates (width 1000).
    // Sheet gestures use millimetres, as before, via 96-DPI logical-unit conversion.
    public double BrushThickness
    {
        get=>StrokeThickness;
        set { if(ValidInkValue(value,0,1000,false)) StrokeThickness=value; }
    }
    public double BrushOpacity { get=>_brushOpacity; set=>SetInkOption(ref _brushOpacity,value,0,1,true); }
    public double MarkerThickness { get=>_markerThickness; set=>SetInkOption(ref _markerThickness,value,0,1000,false); }
    public double MarkerOpacity { get=>_markerOpacity; set=>SetInkOption(ref _markerOpacity,value,0,1,true); }
    public double EraserDiameter { get=>_eraserDiameter; set=>SetInkOption(ref _eraserDiameter,value,0,1000,false); }
    public double InkStrokeThickness=>Tool==EditorTool.Marker ? MarkerThickness : StrokeThickness;
    public double SheetInkStrokeWidthMm=>InkStrokeThickness/SheetEditing.DipPerMm;
    public double InkOpacity=>Tool switch { EditorTool.Brush=>BrushOpacity,EditorTool.Marker=>MarkerOpacity,_=>1 };
    public double SheetEraserDiameterMm=>EraserDiameter/SheetEditing.DipPerMm;
    public bool ShowInkToolSettings=>ShowEditorTools && Tool is EditorTool.Brush or EditorTool.Marker or EditorTool.Eraser or EditorTool.ObjectEraser;
    public bool ShowInkPaintSettings=>ShowInkToolSettings && Tool is EditorTool.Brush or EditorTool.Marker;
    public bool ShowInkSizeSettings=>ShowInkToolSettings && Tool!=EditorTool.ObjectEraser;
    public bool ShowShapeStrokeDefaults=>ShowNewDrawingProperties && Tool is not (EditorTool.Brush or EditorTool.Marker);
    public string InkToolTitle=>Tool switch { EditorTool.Brush=>"Кисть",EditorTool.Marker=>"Маркер",EditorTool.ObjectEraser=>"Удаление объектов",_=>"Частичный ластик" };
    public string InkToolSizeUnit=>IsSheetSelected ? "мм" : "ед.";
    public string InkToolSizeAutomationName=>$"Диаметр: {InkToolTitle}, {InkToolSizeUnit}";
    public double InkToolSizeMinimum=>0;
    public double InkToolSizeMaximum=>IsSheetSelected ? 1000/SheetEditing.DipPerMm : 1000;
    public string InkToolContextKey=>$"{SelectedPage?.Id}|{Tool}|{IsSheetSelected}";
    public string InkToolHint=>Tool switch
    {
        EditorTool.ObjectEraser=>"Щёлкните по объекту, чтобы удалить его целиком. Ctrl+Z — вернуть.",
        EditorTool.Eraser=>"Стирает только части кисти и маркера. Фото, текст и фигуры сохраняются. Ctrl+Z — вернуть жест.",
        _=>IsSheetSelected ? "Размер задан в миллиметрах на листе." : "Размер в единицах исходного фото: его ширина — 1000 ед."
    };
    public bool IsInvisibleInk=>ShowInkPaintSettings && InkOpacity==0;
    public double InkToolSize
    {
        get=>(Tool==EditorTool.Eraser ? EraserDiameter : InkStrokeThickness)/(IsSheetSelected ? SheetEditing.DipPerMm : 1);
        set
        {
            var logical=value*(IsSheetSelected ? SheetEditing.DipPerMm : 1);
            if(!ValidInkValue(logical,0,1000,false)) return;
            if(Tool==EditorTool.Eraser) EraserDiameter=logical;
            else if(Tool==EditorTool.Marker) MarkerThickness=logical;
            else if(Tool==EditorTool.Brush) BrushThickness=logical;
        }
    }
    public double InkOpacityPercent
    {
        get=>InkOpacity*100;
        set
        {
            if(!ValidInkValue(value,0,100,true)) return;
            if(Tool==EditorTool.Brush) BrushOpacity=value/100;
            else if(Tool==EditorTool.Marker) MarkerOpacity=value/100;
        }
    }
    private static bool ValidInkValue(double value,double minimum,double maximum,bool includeMinimum)
        =>double.IsFinite(value) && (includeMinimum ? value>=minimum : value>minimum) && value<=maximum;
    private void SetInkOption(ref double field,double value,double minimum,double maximum,bool includeMinimum,[CallerMemberName] string? property=null)
    {
        if(!ValidInkValue(value,minimum,maximum,includeMinimum)) return;
        if(SetProperty(ref field,value,property)) NotifyInkToolSettings();
    }
    private void InitializeInkToolSettingsNotifications()
    {
        PropertyChanged+=(_,e)=>
        {
            if(e.PropertyName is nameof(Tool) or nameof(IsSheetSelected) or nameof(SelectedPage) or nameof(StrokeThickness)
                or nameof(ShowEditorTools) or nameof(ShowNewDrawingProperties) or nameof(CanEdit)) NotifyInkToolSettings();
        };
    }
    private void NotifyInkToolSettings()
    {
        foreach(var name in new[] { nameof(BrushThickness),nameof(InkStrokeThickness),nameof(SheetInkStrokeWidthMm),nameof(InkOpacity),nameof(SheetEraserDiameterMm),
            nameof(ShowInkToolSettings),nameof(ShowInkPaintSettings),nameof(ShowInkSizeSettings),nameof(ShowShapeStrokeDefaults),nameof(InkToolTitle),nameof(InkToolSizeUnit),
            nameof(InkToolSizeAutomationName),nameof(InkToolSizeMinimum),nameof(InkToolSizeMaximum),nameof(InkToolContextKey),nameof(InkToolHint),nameof(InkToolSize),
            nameof(InkOpacityPercent),nameof(IsInvisibleInk) }) OnPropertyChanged(name);
    }
}
