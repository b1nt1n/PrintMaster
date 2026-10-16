using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Windows.Media.Imaging;

namespace PrintMaster.Models;

public partial class PrintPageItem : ObservableObject
{
    public Guid Id=>Document.Id;
    public SheetDocument Document { get; }
    public ImageEditState Edits { get; }
    public string OriginalPath=>Document.PrimaryAsset?.OriginalPath ?? "";
    public string WorkingPath=>Document.PrimaryAsset?.WorkingPath ?? "";
    public SourceKind Kind=>Document.PrimaryAsset?.Kind ?? SourceKind.Blank;
    public int PageIndex=>Document.PrimaryAsset?.PageIndex ?? 0;
    public int SourcePageCount=>Document.PrimaryAsset?.PageCount ?? 1;
    public double SourceWidth=>Document.PrimaryAsset?.LogicalSize.Width ?? Document.Paper.OrientedSizeMm.Width;
    public double SourceHeight=>Document.PrimaryAsset?.LogicalSize.Height ?? Document.Paper.OrientedSizeMm.Height;
    public double WidthMm=>Document.PrimaryAsset?.PhysicalSizeMm.Width ?? Document.Paper.OrientedSizeMm.Width;
    public double HeightMm=>Document.PrimaryAsset?.PhysicalSizeMm.Height ?? Document.Paper.OrientedSizeMm.Height;
    public bool IsImage=>Kind==SourceKind.Image;
    public bool IsBlank=>Kind==SourceKind.Blank;
    public bool IsComposed=>Document.Snapshot().IsComposed;
    public bool IsSheetLayout=>IsBlank || IsImage && IsComposed;
    public bool HasMissingSources=>Document.Snapshot().Assets.Any(a=>string.IsNullOrEmpty(a.WorkingPath) || !System.IO.File.Exists(a.WorkingPath));
    public string FileName=>Document.Objects.OfType<MarkdownObject>().FirstOrDefault()?.Document?.Title ?? Document.PrimaryAsset?.DisplayName ?? (Document.NotebookText!=null ? "Текст тетрадного листа" : Document.Objects.OfType<TextObject>().Any(t=>t.Flow!=null) ? "Текст на страницах" : "Новый лист");
    public string Description=>HasMissingSources ? "Исходник отсутствует — переподключите файл" : IsBlank
        ? (Document.Objects.OfType<TextObject>().FirstOrDefault(t=>t.Flow is { Notebook:false })?.Flow is {} flow ? $"Страница {flow.PartIndex+1} · " : "")+$"{WidthMm:0.###} × {HeightMm:0.###} мм"
        : IsImage ? "Изображение" : $"Страница {PageIndex+1} из {SourcePageCount}";
    public string FormatLabel=>IsBlank ? "ЛИСТ" : System.IO.Path.GetExtension(OriginalPath).TrimStart('.').ToUpperInvariant();
    public int RotationQuarterTurns
    {
        get=>Document.PrimaryContent?.Edits.QuarterTurns ?? 0;
        set=>Document.SetContentRotation(value);
    }
    private int _lastRotation;
    private bool _lastMissingSources;
    public PrintPageItem(SheetDocument document)
    {
        Document=document; Edits=new(document); _lastRotation=RotationQuarterTurns; _lastMissingSources=HasMissingSources; document.PropertyChanged+=DocumentChanged;
    }
    public static PrintPageItem FromSource(SourceAsset source,SheetPaper? paper=null)=>new(SheetDocument.FromSource(source,paper));
    public static PrintPageItem CreateBlank(SheetPaper paper)=>new(SheetDocument.CreateBlank(paper));

    [ObservableProperty] private bool isIncluded = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanIncreaseCopies),nameof(CanDecreaseCopies))] private int copies = 1;
    [ObservableProperty] private PagePrintOptions? printOptions;
    [ObservableProperty] private BitmapSource? thumbnail;
    [ObservableProperty] private bool isLoading;

    public bool CanIncreaseCopies=>Copies<999;
    public bool CanDecreaseCopies=>Copies>1;

    partial void OnCopiesChanging(int value)
    { if(value is <1 or >999) throw new ArgumentOutOfRangeException(nameof(Copies),"Количество копий: целое число от 1 до 999."); }
    partial void OnPrintOptionsChanging(PagePrintOptions? value)=>value?.Validate();
    private void DocumentChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName!=nameof(SheetDocument.Revision)) return;
        if(_lastRotation!=RotationQuarterTurns) { _lastRotation=RotationQuarterTurns; OnPropertyChanged(nameof(RotationQuarterTurns)); }
        if(_lastMissingSources!=HasMissingSources) { _lastMissingSources=HasMissingSources; OnPropertyChanged(nameof(HasMissingSources)); }
        foreach(var property in new[] { nameof(IsComposed),nameof(IsSheetLayout),nameof(IsBlank),nameof(Kind),nameof(Description),nameof(FileName),nameof(WidthMm),nameof(HeightMm),nameof(FormatLabel) }) OnPropertyChanged(property);
    }

    public PageSnapshot Snapshot() => new(Id,OriginalPath,WorkingPath,Kind,PageIndex,SourceWidth,SourceHeight,
        WidthMm,HeightMm,RotationQuarterTurns,Copies,Edits.Snapshot()) { Sheet=Document.Snapshot(),PrintOptions=PrintOptions };
}

public sealed record PageSnapshot(Guid Id,string OriginalPath,string WorkingPath,SourceKind Kind,int PageIndex,
    double SourceWidth,double SourceHeight,double WidthMm,double HeightMm,int RotationQuarterTurns,int Copies,EditSnapshot Edits)
{
    public SheetSnapshot? Sheet { get; init; }
    public PagePrintOptions? PrintOptions { get; init; }
    public bool IsComposed=>Sheet?.IsComposed ?? Kind==SourceKind.Blank;
    public bool HasImages=>Sheet?.HasImages ?? Kind==SourceKind.Image;
    public bool UsesAutomaticContentLayout=>Sheet!=null ? Sheet.PrimaryContent is { Visible:true,Placement.Mode:PlacementMode.Automatic } : Kind!=SourceKind.Blank;
    public SizeD ContentSize => Sheet?.ContentSize ?? (Kind == SourceKind.Image ? new SizeD(Edits.Crop.Width,Edits.Crop.Height)
        : new SizeD(SourceWidth,SourceHeight)).Rotated(RotationQuarterTurns);
    public SheetSnapshot GetSheet()
    {
        if(Sheet!=null) return Sheet;
        if(Kind==SourceKind.Blank) return SheetDocument.CreateBlank(new(new(WidthMm,HeightMm),false,SheetMargins.SafeDefault)).Snapshot();
        var asset=new SourceAsset(Id,Kind,OriginalPath,WorkingPath,PageIndex,PageIndex+1,new(SourceWidth,SourceHeight),new(WidthMm,HeightMm));
        return SheetDocument.FromSource(asset,SheetPaper.A4,new(Edits.Crop,RotationQuarterTurns,Edits.Annotations.ToImmutableArray())).Snapshot();
    }
}
