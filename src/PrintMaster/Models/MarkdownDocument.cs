using System.Collections.Immutable;

namespace PrintMaster.Models;

public sealed record MarkdownStyle(TextStyle Body,SheetMargins Margins,double ParagraphGapMm=2)
{
    public static MarkdownStyle Default {get;}=new(new(FontSizePt:12,LineSpacing:1.15),new(10,10,10,10));
}
public sealed record MarkdownImageReference(string Url,Guid AssetId);
// The original source is stored once per document; generated pages reference its identity.
public sealed record MarkdownDocument(Guid Id,string Source,MarkdownStyle Style,
    ImmutableArray<MarkdownImageReference> Images,string Title="Markdown")
{
    public const int CurrentLayoutEngineVersion=3;
    public const string LayoutUpgradeMessage="Верстка Markdown обновилась. Откройте редактор и нажмите Применить. Исходный текст сохранён";
    // A page index identifies one generation of pagination, not an enduring source range.
    // Old projects retain version 1 until the user reviews and reapplies the full document.
    public int LayoutEngineVersion {get;init;}=CurrentLayoutEngineVersion;
    public string? SourceDirectory {get;init;}
}
public sealed record PreparedMarkdown(MarkdownDocument Document,ImmutableArray<SourceAsset> Assets);
