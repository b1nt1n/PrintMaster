using System.Collections.Immutable;

namespace PrintMaster.Models;

public sealed record ProjectPrintSettings(string PrinterName,PaperPreset Paper,double CustomWidthMm,double CustomHeightMm,
    bool Landscape,ScaleMode Scaling,MonochromeMode Monochrome,int Dpi,int Threshold,
    NotebookProfile? Notebook=null,bool ManualDuplex=false,double PrintOffsetXmm=0,double PrintOffsetYmm=0)
{
    public static ProjectPrintSettings Capture(PrintSettings settings)=>new(settings.PrinterName,settings.Paper,settings.CustomWidthMm,
        settings.CustomHeightMm,settings.Landscape,settings.Scaling,settings.Monochrome,settings.Dpi,settings.Threshold,
        settings.Notebook,settings.ManualDuplex,settings.PrintOffsetXmm,settings.PrintOffsetYmm);
    public void Apply(PrintSettings settings)
    {
        settings.PrinterName=PrinterName; settings.Paper=Paper; settings.CustomWidthMm=CustomWidthMm; settings.CustomHeightMm=CustomHeightMm;
        settings.Landscape=Landscape; settings.Scaling=Scaling; settings.Monochrome=Monochrome; settings.Dpi=Dpi; settings.Threshold=Threshold;
        settings.Notebook=Notebook; settings.ManualDuplex=ManualDuplex; settings.PrintOffsetXmm=PrintOffsetXmm; settings.PrintOffsetYmm=PrintOffsetYmm;
    }
}
public sealed record ProjectEditorSettings(Guid? SelectedPageId,double Zoom,bool AutoFitZoom,bool ShowPrintPreview,
    string InkColor,double StrokeThickness,double TextSize,double CropRatio,int ExportDpi,TextStyle? SheetTextStyle=null)
{
    // Thickness remains in the original logical units so older projects preserve their brushes.
    public double BrushOpacity { get; init; }=1;
    public double MarkerThickness { get; init; }=Math.Min(1000,StrokeThickness*4);
    public double MarkerOpacity { get; init; }=85d/255;
    public double EraserDiameter { get; init; }=30;
}
public sealed record ProjectPage(SheetSnapshot Sheet,bool IsIncluded,int Copies)
{
    public PagePrintOptions? PrintOptions { get; init; }
}
public sealed record ProjectSession(ProjectPrintSettings Print,ProjectEditorSettings Editor,ImmutableArray<ProjectPage> Pages)
{
    public ImmutableArray<MarkdownDocument> MarkdownDocuments { get; init; }=[];
}
public sealed record ProjectResource(string Id,string Extension,string? ArchiveEntry,string? ReferencePath,string? Sha256,long Length);
public sealed record ProjectAssetLink(Guid SheetId,Guid AssetId,string ResourceId);
public sealed record ProjectManifest(int Version,string Format,DateTimeOffset SavedUtc,ProjectSession Session,
    ImmutableArray<ProjectResource> Resources,ImmutableArray<ProjectAssetLink> Links);
public sealed record MissingProjectSource(Guid SheetId,Guid AssetId,string DisplayName,string? OriginalPath,string ResourceId,SourceKind Kind);
public sealed record LoadedProject(ProjectSession Session,ImmutableArray<MissingProjectSource> Missing,string Path);
