using System.Collections.Immutable;
using System.Windows.Media.Imaging;

namespace PrintMaster.Models;

public enum PasteDestination { Auto,Queue }
public enum ClipboardSelectionKind { Pages,Objects }
public sealed record ClipboardSnapshot(ImmutableArray<string> Files,BitmapSource? Image=null,string? Text=null,string? ObjectJson=null,
    byte[]? SelectionArchive=null,ClipboardSelectionKind SelectionKind=ClipboardSelectionKind.Objects)
{
    public static ClipboardSnapshot Empty { get; }=new([]);
}
public sealed record ClipboardMaterial(SourceAsset Asset,string Extension,byte[] Bytes);
public sealed record ClipboardObjectPackage(int Version,SheetObject? Object,ImmutableArray<ClipboardMaterial> Materials,
    Annotation? Annotation=null,SizeD? AnnotationSourceSize=null)
{
    public ImmutableArray<MarkdownDocument> MarkdownDocuments { get; init; }=[];
}
