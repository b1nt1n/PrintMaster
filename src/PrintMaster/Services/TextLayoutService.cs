using PrintMaster.Models;
using System.Collections.Immutable;
using System.Drawing;
using System.Drawing.Text;
using System.Globalization;

namespace PrintMaster.Services;

public sealed record TextLine(int Start,int Length,string DisplayText,double WidthMm);
public sealed record TextLayout(ImmutableArray<TextLine> Lines,double LineHeightMm,double HeightMm,bool WidthOverflow,
    string FontFamily,bool FontFallback);
public sealed record TextPageFragment(string Text,double HeightMm);

// Fixed 96-DPI metrics define the line breaks; output DPI only controls raster detail.
public static class TextLayoutService
{
    private sealed record Key(string Text,TextStyle Style,double Width,bool Continued);
    private static readonly Dictionary<Key,TextLayout> Cache=[];
    private static readonly Queue<Key> Order=[];
    private static readonly object CacheGate=new();
    private static int _cachedCharacters;
    private const double Units=96/25.4;
    public const double VerticalPaddingMm=0.4;
    public static IReadOnlyList<string> FontNames { get; }=ReadFonts();
    private static IReadOnlyList<string> ReadFonts()
    {
        using var fonts=new InstalledFontCollection(); var families=fonts.Families;
        try { return families.Select(f=>f.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n=>n,StringComparer.CurrentCultureIgnoreCase).ToArray(); }
        finally { foreach(var family in families) family.Dispose(); }
    }
    public static void ValidateStyle(TextStyle style)
    {
        if(string.IsNullOrWhiteSpace(style.FontFamily) || string.IsNullOrWhiteSpace(style.Color) || !double.IsFinite(style.FontSizePt) || style.FontSizePt<=0 || style.FontSizePt>1000
            || !double.IsFinite(style.LineSpacing) || style.LineSpacing<1 || style.LineSpacing>3 || !Enum.IsDefined(style.Alignment)
            || style.FixedLineHeightMm is {} fixedHeight && (!double.IsFinite(fixedHeight) || fixedHeight<=0 || fixedHeight>1000)) throw new ArgumentException("Недопустимое оформление текста.");
        _=ColorTranslator.FromHtml(style.Color);
    }
    public static Font CreateFont(TextStyle style,out bool fallback,double pixelsPerPoint=96d/72)
    {
        var requested=(style.Bold ? FontStyle.Bold : FontStyle.Regular) | (style.Italic ? FontStyle.Italic : FontStyle.Regular);
        fallback=false;
        try
        {
            using var family=new FontFamily(style.FontFamily);
            if(!family.IsStyleAvailable(requested)) throw new ArgumentException("Начертание недоступно.");
            return new(family,(float)(style.FontSizePt*pixelsPerPoint),requested,GraphicsUnit.Pixel);
        }
        catch(ArgumentException)
        {
            fallback=true; return new("Segoe UI",(float)(style.FontSizePt*pixelsPerPoint),requested,GraphicsUnit.Pixel);
        }
    }
    internal static StringFormat CreateFormat()
    {
        var format=(StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags|=StringFormatFlags.NoWrap|StringFormatFlags.MeasureTrailingSpaces|StringFormatFlags.NoClip;
        return format;
    }
    public static TextLayout Layout(string text,TextStyle style,double widthMm,bool continued=false)
    {
        ValidateStyle(style);
        if(text.Length>TextLimits.MaxCharacters || !double.IsFinite(widthMm) || widthMm<=0 || widthMm>10000) throw new ArgumentException("Недопустимая ширина или слишком большой текст.");
        var key=new Key(text,style,widthMm,continued);
        lock(CacheGate) { if(Cache.TryGetValue(key,out var found)) return found; }
        var result=Build(text,style,widthMm,continued);
        lock(CacheGate)
        {
            if(Cache.TryAdd(key,result)) { Order.Enqueue(key); _cachedCharacters+=text.Length; }
            while(Order.Count>64 || _cachedCharacters>4_000_000) { var old=Order.Dequeue(); if(Cache.Remove(old)) _cachedCharacters-=old.Text.Length; }
        }
        return result;
    }
    private static TextLayout Build(string text,TextStyle style,double widthMm,bool continued)
    {
        using var bitmap=new Bitmap(1,1); bitmap.SetResolution(96,96); using var g=Graphics.FromImage(bitmap); g.TextRenderingHint=TextRenderingHint.AntiAlias;
        using var font=CreateFont(style,out var fallback); using var format=CreateFormat();
        var lineHeight=style.FixedLineHeightMm ?? font.GetHeight(g)/Units*style.LineSpacing;
        var lines=ImmutableArray.CreateBuilder<TextLine>(); var offset=0; var overflow=false;
        double Measure(string value)=>value.Length==0 ? 0 : g.MeasureString(value.Replace("\t","    "),font,PointF.Empty,format).Width/Units;
        do
        {
            var end=offset; while(end<text.Length && text[end] is not ('\r' or '\n')) end++;
            var newline=end<text.Length ? text[end]=='\r' && end+1<text.Length && text[end+1]=='\n' ? 2 : 1 : 0;
            if(end==offset) lines.Add(new(offset,newline,"",0));
            else
            {
                var paragraph=text[offset..end]; var elements=StringInfo.ParseCombiningCharacters(paragraph); var element=0;
                while(element<elements.Length)
                {
                    var from=elements[element]; var low=1; var cap=Math.Min(elements.Length-element,16000); var fitting=0;
                    while(cap>1 && (element+cap==elements.Length ? paragraph.Length : elements[element+cap])-from>16000) cap/=2;
                    var high=Math.Min(32,cap);
                    while(high<cap)
                    {
                        var to=element+high==elements.Length ? paragraph.Length : elements[element+high];
                        if(Measure(paragraph[from..to])>widthMm-0.05) break;
                        fitting=high; low=high+1; high=Math.Min(cap,high*2);
                    }
                    while(low<=high)
                    {
                        var count=(low+high)/2; var to=element+count==elements.Length ? paragraph.Length : elements[element+count];
                        if(Measure(paragraph[from..to])<=widthMm-0.05) { fitting=count; low=count+1; } else high=count-1;
                    }
                    var countToTake=Math.Max(1,fitting); var next=element+countToTake;
                    if(next<elements.Length)
                    {
                        // Prefer a word boundary, retaining its whitespace in the source range.
                        for(var candidate=next-1;candidate>element;candidate--)
                            if(char.IsWhiteSpace(paragraph[elements[candidate]])) { next=candidate+1; break; }
                    }
                    var toIndex=next==elements.Length ? paragraph.Length : elements[next]; var value=paragraph[from..toIndex]; var measured=Measure(value);
                    overflow|=measured>widthMm+0.01;
                    lines.Add(new(offset+from,toIndex-from+(next==elements.Length ? newline : 0),value.Replace("\t","    "),measured));
                    element=next;
                }
            }
            offset=end+newline;
            if(newline==0 || offset==text.Length && continued) break;
        } while(offset<=text.Length);
        return new(lines.ToImmutable(),lineHeight,lines.Count*lineHeight+VerticalPaddingMm,overflow,font.FontFamily.Name,fallback);
    }
    public static IReadOnlyList<TextPageFragment> Paginate(string text,TextStyle style,RectD safe)
    {
        var layout=Layout(text,style,safe.Width);
        if(layout.WidthOverflow) throw new InvalidOperationException("При этой ширине не помещается даже один символ. Увеличьте ширину или выберите меньший шрифт.");
        var linesPerPage=(int)Math.Floor((safe.Height-VerticalPaddingMm)/layout.LineHeightMm);
        if(linesPerPage<1) throw new InvalidOperationException("Строка этого размера не помещается на листе. Выберите другой формат или меньший шрифт.");
        var fragments=new List<TextPageFragment>(); var first=0;
        while(first<layout.Lines.Length)
        {
            var take=Math.Min(linesPerPage,layout.Lines.Length-first); var last=layout.Lines[first+take-1];
            var start=layout.Lines[first].Start; var end=last.Start+last.Length;
            fragments.Add(new(text[start..end],take*layout.LineHeightMm+VerticalPaddingMm)); first+=take;
            if(fragments.Count>TextLimits.MaxPages) throw new InvalidOperationException("Текст создает более 2000 страниц. Разделите документ.");
        }
        return fragments;
    }
    public static double BaselineMm(TextStyle style)
    {
        using var font=CreateFont(style,out _); using var family=font.FontFamily;
        return family.GetCellAscent(font.Style)/(double)family.GetEmHeight(font.Style)*font.Size/Units;
    }
    // Notebook fragments are allocated without an extra empty line after their final newline.
    // Rendering and preflight must use the same convention as NotebookTextLayout.
    public static TextLayout Layout(TextObject text)=>Layout(text.Text,text.Style,text.Placement.BoundsMm.Width,
        text.Flow is { Notebook:true } or { HasNext:true });
    public static bool Overflows(TextObject text)
    {
        if(text.Text.Length==0) return false;
        var layout=Layout(text);
        return layout.WidthOverflow || layout.HeightMm>text.Placement.BoundsMm.Height+0.05;
    }
}
