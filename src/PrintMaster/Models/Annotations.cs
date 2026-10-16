using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace PrintMaster.Models;

// Immutable geometry: snapshots can safely cross the UI/worker boundary.
public sealed record Annotation(Guid Id, AnnotationKind Kind, PointD Start, PointD End,
    string Color, double Thickness, string Text, double FontSize, IReadOnlyList<PointD> Points,TextAnnotationFormat? TextFormat=null)
{
    // Missing values retain the exact appearance of annotations authored before PM-010.
    public double? Opacity { get; init; }
    public string? FillColor { get; init; }
    [JsonIgnore] public double EffectiveOpacity=>Opacity ?? (Kind==AnnotationKind.Marker ? 85d/255 : 1);

    public static Annotation Create(AnnotationKind kind, PointD start, PointD end, string color,
        double thickness, string text = "", double fontSize = 36, IEnumerable<PointD>? points = null) =>
        new(Guid.NewGuid(),kind,start,end,color,thickness,text,fontSize,Array.AsReadOnly(points?.ToArray() ?? []));

    public Annotation Move(double dx, double dy) => this with
    {
        Start = new(Start.X+dx,Start.Y+dy), End = new(End.X+dx,End.Y+dy),
        Points = Array.AsReadOnly(Points.Select(p => new PointD(p.X+dx,p.Y+dy)).ToArray())
    };
}

// Size/color remain in the original annotation fields; this adds only block formatting.
public sealed record TextAnnotationFormat(string FontFamily="Segoe UI",bool Bold=false,bool Italic=false,
    TextAlignment Alignment=TextAlignment.Left,double LineSpacing=1,double? LayoutMmPerUnit=null);

public sealed record EditSnapshot(RectD Crop, IReadOnlyList<Annotation> Annotations);

public sealed class ImageEditState : ObservableObject
{
    private readonly SheetDocument _document;
    public ImageEditState(double width,double height) : this(SheetDocument.FromSource(new(Guid.NewGuid(),SourceKind.Image,null,null,0,1,new(width,height),new(width,height)))) { }
    public ImageEditState(SheetDocument document)
    {
        _document=document; _document.PropertyChanged+=DocumentChanged;
    }
    public SheetDocument Document=>_document;
    public RectD Crop => _document.PrimaryContent?.Edits.Crop ?? new(0,0,_document.Paper.OrientedSizeMm.Width,_document.Paper.OrientedSizeMm.Height);
    public IReadOnlyList<Annotation> Annotations => _document.PrimaryContent?.Edits.Annotations ?? ImmutableArray<Annotation>.Empty;
    public bool CanUndo => _document.CanUndo;
    public bool CanRedo => _document.CanRedo;
    public int Revision => _document.Revision;
    public EditSnapshot Snapshot() => new(Crop,Annotations);

    public void SetCrop(RectD crop)
    {
        if (crop.IsEmpty || crop == Crop) return;
        Update(edits=>edits with { Crop=crop },"Кроп");
    }
    public void Add(Annotation annotation)=>Update(edits=>edits with { Annotations=edits.Annotations.Add(annotation) },"Добавить аннотацию");
    public void Remove(Guid id)
    {
        if(!Annotations.Any(a=>a.Id==id)) return;
        Update(edits=>edits with { Annotations=edits.Annotations.Where(x=>x.Id!=id).ToImmutableArray() },"Удалить аннотацию");
    }
    public void Replace(Annotation annotation)
    {
        if(!Annotations.Any(a=>a.Id==annotation.Id)) throw new KeyNotFoundException("Аннотация не найдена.");
        Update(edits=>edits with { Annotations=edits.Annotations.Select(x=>x.Id==annotation.Id ? annotation : x).ToImmutableArray() },"Изменить аннотацию");
    }
    public void Undo()=>_document.Undo();
    public void Redo()=>_document.Redo();
    private void Update(Func<ContentEdits,ContentEdits> update,string action)
    {
        var content=_document.PrimaryContent ?? throw new InvalidOperationException("У листа нет исходного изображения для этой правки.");
        _document.SetContentEdits(content.Id,update(content.Edits),action);
    }
    private void DocumentChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName!=nameof(SheetDocument.Revision)) return;
        OnPropertyChanged(nameof(Crop)); OnPropertyChanged(nameof(Annotations));
        OnPropertyChanged(nameof(CanUndo)); OnPropertyChanged(nameof(CanRedo)); OnPropertyChanged(nameof(Revision));
    }
}
