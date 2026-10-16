using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace PrintMaster.Models;

public sealed record SourceAsset(Guid Id,SourceKind Kind,string? OriginalPath,string? WorkingPath,int PageIndex,
    int PageCount,SizeD LogicalSize,SizeD PhysicalSizeMm)
{
    public string DisplayName => string.IsNullOrWhiteSpace(OriginalPath) ? "Изображение" : System.IO.Path.GetFileName(OriginalPath);
}

public sealed record SheetMargins(double LeftMm,double TopMm,double RightMm,double BottomMm)
{
    public static SheetMargins SafeDefault { get; }=new(5,5,5,5);
}

public sealed record SheetPaper(SizeD SizeMm,bool Landscape,SheetMargins Margins)
{
    public NotebookProfile? Notebook { get; init; }
    public static SheetPaper A4 { get; }=new(new(210,297),false,SheetMargins.SafeDefault);
    public SizeD OrientedSizeMm=>SizeMm.Rotated(Landscape ? 1 : 0);
    public RectD SafeAreaMm=>new(Margins.LeftMm,Margins.TopMm,
        OrientedSizeMm.Width-Margins.LeftMm-Margins.RightMm,OrientedSizeMm.Height-Margins.TopMm-Margins.BottomMm);
    internal void Validate()
    {
        SheetValidation.Size(SizeMm,nameof(SizeMm));
        if(Notebook is {} notebook)
        { notebook.Validate(); if(notebook.FeedSize!=SizeMm) throw new ArgumentException("Размер бумаги не совпадает с профилем тетради."); }
        foreach(var value in new[] { Margins.LeftMm,Margins.TopMm,Margins.RightMm,Margins.BottomMm })
            if(!double.IsFinite(value) || value<0) throw new ArgumentException("Поля должны быть конечными и неотрицательными.");
        if(SafeAreaMm.IsEmpty) throw new ArgumentException("Поля занимают весь лист.");
    }
}

public enum PlacementMode { Automatic,Fixed }

public sealed record ObjectPlacement(RectD BoundsMm,double RotationDegrees=0,PlacementMode Mode=PlacementMode.Fixed)
{
    public static ObjectPlacement Automatic { get; }=new(new(0,0,1,1),0,PlacementMode.Automatic);
}

public sealed record StrokeStyle(string Color="#111827",double WidthMm=0.4,double Opacity=1,string? FillColor=null);
public enum TextAlignment { Left,Center,Right }
public sealed record TextStyle(string FontFamily="Segoe UI",double FontSizePt=12,string Color="#111827",
    bool Bold=false,bool Italic=false,TextAlignment Alignment=TextAlignment.Left,double LineSpacing=1,double? FixedLineHeightMm=null);
public sealed record TextFlowInfo(Guid StoryId,int PartIndex,bool HasNext,bool Notebook=false);
public enum SheetShapeKind { Rectangle,Ellipse,Arrow }

public sealed record ContentEdits(RectD Crop,int QuarterTurns,ImmutableArray<Annotation> Annotations)
{
    public static ContentEdits Empty(SizeD size)=>new(new(0,0,size.Width,size.Height),0,ImmutableArray<Annotation>.Empty);
    public EditSnapshot ToLegacy()=>new(Crop,Annotations);
}

[JsonPolymorphic(TypeDiscriminatorPropertyName="$kind")]
[JsonDerivedType(typeof(ContentObject),"content")]
[JsonDerivedType(typeof(TextObject),"text")]
[JsonDerivedType(typeof(ShapeObject),"shape")]
[JsonDerivedType(typeof(InkObject),"ink")]
[JsonDerivedType(typeof(MarkdownObject),"markdown")]
public abstract record SheetObject(Guid Id,ObjectPlacement Placement,bool Visible=true,bool Locked=false);

public sealed record ContentObject(Guid Id,ObjectPlacement Placement,Guid AssetId,ContentEdits Edits,
    bool Visible=true,bool Locked=false) : SheetObject(Id,Placement,Visible,Locked)
{
    // Older projects omit this editor preference and retain proportional resizing.
    public bool KeepAspectRatio { get; init; }=true;
}

public sealed record TextObject(Guid Id,ObjectPlacement Placement,string Text,TextStyle Style,
    bool Visible=true,bool Locked=false,TextFlowInfo? Flow=null) : SheetObject(Id,Placement,Visible,Locked);

public sealed record ShapeObject(Guid Id,ObjectPlacement Placement,SheetShapeKind Shape,StrokeStyle Style,
    bool Visible=true,bool Locked=false) : SheetObject(Id,Placement,Visible,Locked);

// Points are normalized to BoundsMm (0..1). Move/resize does not rewrite source points.
public sealed record InkObject(Guid Id,ObjectPlacement Placement,ImmutableArray<PointD> Points,StrokeStyle Style,
    bool Visible=true,bool Locked=false) : SheetObject(Id,Placement,Visible,Locked);

public sealed record MarkdownObject(Guid Id,ObjectPlacement Placement,Guid DocumentId,int PageIndex,
    bool Visible=true,bool Locked=true) : SheetObject(Id,Placement,Visible,Locked)
{
    // Hydrated from ProjectSession.MarkdownDocuments; excluded from per-page JSON duplication.
    [JsonIgnore] public MarkdownDocument? Document {get;init;}
}

public sealed record SheetSnapshot(Guid Id,SheetPaper Paper,ImmutableArray<SourceAsset> Assets,
    ImmutableArray<SheetObject> Objects,Guid? PrimaryContentId,NotebookTextStory? NotebookText=null)
{
    public ContentObject? PrimaryContent => Objects.OfType<ContentObject>().FirstOrDefault(x=>x.Id==PrimaryContentId);
    public SourceAsset? PrimaryAsset => PrimaryContent is {} content ? Assets.FirstOrDefault(a=>a.Id==content.AssetId) : null;
    public bool IsComposed => PrimaryContentId==null || Objects.Length!=1 || PrimaryContent?.Placement.Mode==PlacementMode.Fixed
        || PrimaryContent?.Placement.RotationDegrees!=0 || PrimaryContent?.Visible==false;
    public bool HasImages=>Objects.OfType<ContentObject>().Any(o=>o.Visible && Assets.Any(a=>a.Id==o.AssetId && a.Kind==SourceKind.Image))
        || Objects.OfType<MarkdownObject>().Any(o=>o.Visible && o.Document?.Images.Length>0);
    public SizeD ContentSize=>PrimaryContent is {} content && PrimaryAsset is {} source
        ? (source.Kind==SourceKind.Image ? new SizeD(content.Edits.Crop.Width,content.Edits.Crop.Height) : source.LogicalSize).Rotated(content.Edits.QuarterTurns)
        : Paper.OrientedSizeMm;
}

internal static class SheetValidation
{
    public static void Size(SizeD size,string name)
    {
        if(!double.IsFinite(size.Width) || !double.IsFinite(size.Height) || size.Width<=0 || size.Height<=0)
            throw new ArgumentException("Размеры должны быть конечными и положительными.",name);
    }
    public static void Placement(ObjectPlacement placement)
    {
        Size(new(placement.BoundsMm.Width,placement.BoundsMm.Height),nameof(placement));
        if(!double.IsFinite(placement.BoundsMm.X) || !double.IsFinite(placement.BoundsMm.Y) || !double.IsFinite(placement.RotationDegrees))
            throw new ArgumentException("Недопустимое размещение объекта.");
    }
}
