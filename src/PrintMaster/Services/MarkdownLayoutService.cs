using PrintMaster.Models;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using WpfMath.Parsers;
using WpfMath.Rendering;
using XamlMath;
using MdTable=Markdig.Extensions.Tables.Table;
using MdRow=Markdig.Extensions.Tables.TableRow;
using MdCell=Markdig.Extensions.Tables.TableCell;
using WpfTable=System.Windows.Documents.Table;
using WpfRow=System.Windows.Documents.TableRow;
using WpfCell=System.Windows.Documents.TableCell;
using WpfBlock=System.Windows.Documents.Block;
using MarkdownDocument=PrintMaster.Models.MarkdownDocument;

namespace PrintMaster.Services;

public sealed record MarkdownRenderPage(DrawingGroup Drawing,SizeD PaperMm,RectD SafeAreaMm,
    ImmutableDictionary<ImageSource,string> ImageFiles)
{
    // Evidence from actual retained glyphs before outlining, rather than from the parser's source.
    internal string DiagnosticText {get;init;}="";
}
public sealed record MarkdownDiagnostic(string Message,int? Line=null,string Code="layout");
public sealed record MarkdownLayoutResult(ImmutableArray<MarkdownRenderPage> Pages,ImmutableArray<MarkdownDiagnostic> Diagnostics);

public sealed class MarkdownLayoutException : InvalidOperationException
{
    public int? Line {get;}
    public MarkdownLayoutException(string message,int? line=null,Exception? inner=null)
        : base(line is {} n ? $"Строка {n}: {message}" : message,inner)=>Line=line;
}

// WPF layout belongs to one private STA thread. PrintPage consumes only frozen vector data on its own worker.
public sealed partial class MarkdownLayoutService : IDisposable
{
    public static MarkdownLayoutService Shared {get;}=new();
    internal const double DipPerMm=96d/25.4;
    private readonly BlockingCollection<Action> _jobs=new();
    private readonly CancellationTokenSource _shutdown=new();
    private readonly Thread _worker;
    private readonly Dictionary<string,LinkedListNode<CacheEntry>> _cache=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,MarkdownLayoutResult> _ready=new(StringComparer.Ordinal);
    private readonly LinkedList<CacheEntry> _lru=new();
    private readonly int _entryLimit;
    private readonly long _byteLimit;
    private long _cacheBytes;
    private int _cachedEntryCount;
    private int _disposed;
    private sealed record CacheEntry(string Key,MarkdownLayoutResult Result,long Bytes);
    internal int CachedEntryCount=>Volatile.Read(ref _cachedEntryCount);
    internal Func<bool> UiThreadAccess {get;set;}=static()=>Application.Current?.Dispatcher.CheckAccess()==true;
    private static MarkdownPipeline CreatePipeline()
    {
        var builder=new MarkdownPipelineBuilder().UseMathematics().UsePipeTables()
            .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough).UseTaskLists().UseFootnotes().UsePreciseSourceLocation();
        // Inline HTML is only a token source for our small WPF whitelist. An HTML block
        // parser would hide the Markdown, image and footnote AST inside <details>.
        foreach(var parser in builder.BlockParsers.OfType<Markdig.Parsers.HtmlBlockParser>().ToArray())builder.BlockParsers.Remove(parser);
        return builder.Build();
    }
    public static ImmutableArray<string> FindImageUrls(string source)
    {
        if(source==null || source.Length>TextLimits.MaxCharacters)throw new MarkdownLayoutException("Markdown превышает допустимый размер.");
        var pipeline=CreatePipeline();
        return Markdig.Markdown.Parse(source,pipeline).Descendants<LinkInline>()
            .Where(link=>link.IsImage && !string.IsNullOrWhiteSpace(link.Url)).Select(link=>link.Url!)
            .Distinct(StringComparer.Ordinal).ToImmutableArray();
    }

    public MarkdownLayoutService(int cacheEntries=12,long cacheByteLimit=32L*1024*1024)
    {
        if(cacheEntries<1 || cacheByteLimit<1024) throw new ArgumentOutOfRangeException(nameof(cacheEntries));
        _entryLimit=cacheEntries; _byteLimit=cacheByteLimit;
        _worker=new Thread(Run) {IsBackground=true,Name="PrintMaster Markdown layout"};
        _worker.SetApartmentState(ApartmentState.STA); _worker.Start();
    }
    public Task<MarkdownLayoutResult> LayoutAsync(MarkdownDocument document,SheetPaper paper,
        IReadOnlyList<SourceAsset> assets,CancellationToken token)
        =>LayoutAsync(document,paper,assets.ToImmutableArray(),token);
    public Task<MarkdownLayoutResult> LayoutAsync(MarkdownDocument document,SheetPaper paper,
        ImmutableArray<SourceAsset> assets,CancellationToken token)
    {
        if(token.IsCancellationRequested) return Task.FromCanceled<MarkdownLayoutResult>(token);
        if(Volatile.Read(ref _disposed)!=0) return Task.FromException<MarkdownLayoutResult>(new ObjectDisposedException(nameof(MarkdownLayoutService)));
        if(assets.IsDefault) assets=[];
        var completion=new TaskCompletionSource<MarkdownLayoutResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var linked=CancellationTokenSource.CreateLinkedTokenSource(token,_shutdown.Token);
        var registration=linked.Token.Register(()=>completion.TrySetCanceled(token.IsCancellationRequested ? token : _shutdown.Token));
        try
        {
            _jobs.Add(()=>
            {
                try
                {
                    if(completion.Task.IsCompleted) return;
                    linked.Token.ThrowIfCancellationRequested(); Validate(document,paper,assets);
                    var key=CacheKey(document,paper,assets);
                    if(_cache.TryGetValue(key,out var node))
                    { _lru.Remove(node); _lru.AddFirst(node); completion.TrySetResult(node.Value.Result); return; }
                    var result=new LayoutBuilder(document,paper,assets,linked.Token).Build();
                    linked.Token.ThrowIfCancellationRequested(); AddCache(key,result,document.Source.Length);
                    completion.TrySetResult(result);
                }
                catch(OperationCanceledException) {completion.TrySetCanceled(token.IsCancellationRequested ? token : linked.Token);}
                catch(Exception ex) {completion.TrySetException(ex is MarkdownLayoutException ? ex : new MarkdownLayoutException("Не удалось сверстать Markdown: "+ex.Message,inner:ex));}
                finally {registration.Dispose();linked.Dispose();}
            });
        }
        catch(InvalidOperationException) {registration.Dispose();linked.Dispose();completion.TrySetException(new ObjectDisposedException(nameof(MarkdownLayoutService)));}
        return completion.Task;
    }
    public MarkdownLayoutResult Layout(MarkdownDocument document,SheetPaper paper,IReadOnlyList<SourceAsset> assets,CancellationToken token)
        =>Layout(document,paper,assets.ToImmutableArray(),token);
    public MarkdownLayoutResult Layout(MarkdownDocument document,SheetPaper paper,ImmutableArray<SourceAsset> assets,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(assets.IsDefault)assets=[];
        if(Volatile.Read(ref _disposed)!=0)throw new ObjectDisposedException(nameof(MarkdownLayoutService));
        Validate(document,paper,assets);
        var key=CacheKey(document,paper,assets);
        if(_ready.TryGetValue(key,out var ready)) {token.ThrowIfCancellationRequested();return ready;}
        if(Thread.CurrentThread.ManagedThreadId==_worker.ManagedThreadId)
            return new LayoutBuilder(document,paper,assets,token).Build();
        // A synchronous facade is required by GDI PrintPage; UI callers must use LayoutAsync.
        if(UiThreadAccess())
            throw new InvalidOperationException("На потоке интерфейса используйте асинхронную верстку Markdown.");
        return LayoutAsync(document,paper,assets,token).GetAwaiter().GetResult();
    }
    private void Run()
    {
        foreach(var work in _jobs.GetConsumingEnumerable()) work();
        _cache.Clear();_ready.Clear();_lru.Clear();Volatile.Write(ref _cachedEntryCount,0);
    }
    public void Dispose()
    {
        if(Interlocked.Exchange(ref _disposed,1)!=0) return;
        _ready.Clear();_shutdown.Cancel();_jobs.CompleteAdding();
    }
    private static void Validate(MarkdownDocument document,SheetPaper paper,ImmutableArray<SourceAsset> assets)
    {
        if(document==null || document.Id==Guid.Empty || document.Source==null || document.Source.Length>TextLimits.MaxCharacters)
            throw new MarkdownLayoutException("Markdown пуст, поврежден или превышает 1 000 000 символов.");
        if(document.Style==null || document.Style.Body==null || document.Style.Margins==null)
            throw new MarkdownLayoutException("Не задано оформление Markdown.");
        try {TextLayoutService.ValidateStyle(document.Style.Body);paper.Validate();}
        catch(ArgumentException ex) {throw new MarkdownLayoutException(ex.Message,inner:ex);}
        if(!double.IsFinite(document.Style.ParagraphGapMm) || document.Style.ParagraphGapMm<0 || document.Style.ParagraphGapMm>100)
            throw new MarkdownLayoutException("Интервал между абзацами должен быть от 0 до 100 мм.");
        foreach(var margin in new[]{document.Style.Margins.LeftMm,document.Style.Margins.TopMm,document.Style.Margins.RightMm,document.Style.Margins.BottomMm})
            if(!double.IsFinite(margin) || margin<0 || margin>1000) throw new MarkdownLayoutException("Поля Markdown должны быть конечными и неотрицательными.");
        if(document.Images.IsDefault) throw new MarkdownLayoutException("Поврежден список изображений Markdown.");
        if(document.Images.Any(i=>i.AssetId==Guid.Empty || string.IsNullOrWhiteSpace(i.Url)) || document.Images.Select(i=>i.Url).Distinct(StringComparer.Ordinal).Count()!=document.Images.Length)
            throw new MarkdownLayoutException("Ссылки изображений Markdown должны быть уникальными и иметь материал.");
        if(assets.Any(a=>a.Id==Guid.Empty) || assets.Select(a=>a.Id).Distinct().Count()!=assets.Length)
            throw new MarkdownLayoutException("Поврежден список материалов Markdown.");
    }
    private static string CacheKey(MarkdownDocument document,SheetPaper paper,ImmutableArray<SourceAsset> assets)
    {
        var b=new StringBuilder(JsonSerializer.Serialize(new {document.Source,document.Style,document.Images,Paper=paper}));
        foreach(var asset in assets.OrderBy(a=>a.Id))
        {
            b.Append('|').Append(asset.Id).Append('|').Append(asset.Kind).Append('|').Append(asset.WorkingPath).Append('|').Append(asset.LogicalSize).Append('|').Append(asset.PhysicalSizeMm);
            if(asset.WorkingPath is {} path && File.Exists(path)) {var info=new FileInfo(path);b.Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks);}
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(b.ToString())));
    }
    private void AddCache(string key,MarkdownLayoutResult result,int characters)
    {
        var bytes=characters*4L+result.Pages.Sum(p=>MarkdownVectors.EstimateBytes(p.Drawing)+p.DiagnosticText.Length*2L);
        if(bytes>_byteLimit) return;
        var entry=new CacheEntry(key,result,bytes);var node=_lru.AddFirst(entry);_cache.Add(key,node);_cacheBytes+=bytes;
        while(_cache.Count>_entryLimit || _cacheBytes>_byteLimit)
        {var last=_lru.Last!;_cache.Remove(last.Value.Key);_ready.TryRemove(last.Value.Key,out _);_cacheBytes-=last.Value.Bytes;_lru.RemoveLast();}
        _ready[key]=result;
        Volatile.Write(ref _cachedEntryCount,_cache.Count);
    }

    private sealed partial class LayoutBuilder
    {
        private readonly MarkdownDocument _source;
        private readonly SheetPaper _paper;
        private readonly ImmutableArray<SourceAsset> _assets;
        private readonly CancellationToken _token;
        private readonly List<MarkdownDiagnostic> _diagnostics=[];
        private readonly Dictionary<ImageSource,string> _imageFiles=[];
        private readonly List<FormulaVisual> _formulas=[];
        private readonly Dictionary<string,MarkdownFormula> _formulaCache=new(StringComparer.Ordinal);
        private FontFamily _font=null!;
        private Brush _foreground=null!;
        private double _em;
        private double _gap;
        private double _columnWidth;
        private double _columnHeight;
        private RectD _safe;
        private RectD[] _nativeColumns=[];
        private SizeD _nativePaper;
        private bool _rotate;
        public LayoutBuilder(MarkdownDocument source,SheetPaper paper,ImmutableArray<SourceAsset> assets,CancellationToken token)
        {_source=source;_paper=paper;_assets=assets;_token=token;}
        public MarkdownLayoutResult Build()
        {
            _token.ThrowIfCancellationRequested(); ConfigurePaper();
            var body=_source.Style.Body;_em=body.FontSizePt*96/72;_gap=_source.Style.ParagraphGapMm*DipPerMm;
            var selected=Fonts.SystemFontFamilies.FirstOrDefault(f=>f.ToString().Equals(body.FontFamily,StringComparison.OrdinalIgnoreCase) || f.FamilyNames.Values.Any(n=>n.Equals(body.FontFamily,StringComparison.OrdinalIgnoreCase)));
            _font=selected ?? new FontFamily("Segoe UI");
            if(selected==null) _diagnostics.Add(new($"Шрифт «{body.FontFamily}» не найден; используется Segoe UI.",Code:"font-fallback"));
            _foreground=new SolidColorBrush((Color)ColorConverter.ConvertFromString(body.Color));_foreground.Freeze();
            if(Measure("W",_font,_em)>_columnWidth || (body.FixedLineHeightMm is {} fixedLine ? Math.Max(_em*1.2,fixedLine*DipPerMm) : _em*1.2*body.LineSpacing)>_columnHeight-_gap)
                throw new MarkdownLayoutException("Строка такого размера не помещается на листе. Уменьшите шрифт или поля.");
            var first=_nativeColumns[0];var last=_nativeColumns[^1];
            var document=new FlowDocument {PageWidth=_nativePaper.Width*DipPerMm,PageHeight=_nativePaper.Height*DipPerMm,
                PagePadding=new Thickness(first.X*DipPerMm,first.Y*DipPerMm,(_nativePaper.Width-last.Right)*DipPerMm,(_nativePaper.Height-first.Bottom)*DipPerMm),
                ColumnWidth=_columnWidth,ColumnGap=_nativeColumns.Length==2 ? (_nativeColumns[1].X-_nativeColumns[0].Right)*DipPerMm : 0,
                IsColumnWidthFlexible=false,FontFamily=_font,FontSize=_em,FontWeight=body.Bold ? FontWeights.Bold : FontWeights.Normal,
                FontStyle=body.Italic ? FontStyles.Italic : FontStyles.Normal,Foreground=_foreground,TextAlignment=body.Alignment switch {Models.TextAlignment.Center=>System.Windows.TextAlignment.Center,Models.TextAlignment.Right=>System.Windows.TextAlignment.Right,_=>System.Windows.TextAlignment.Left},
                Background=Brushes.Transparent};
            var pipeline=CreatePipeline();
            var parsed=Markdig.Markdown.Parse(_source.Source,pipeline);
            PrepareHtml(parsed);
            PrepareFootnotes(parsed);
            AddBlocks(document.Blocks,parsed,_columnWidth,0);
            if(document.Blocks.Count==0) document.Blocks.Add(new Paragraph(new Run("")));
            return new(PaginateWithFootnotes(document),_diagnostics.ToImmutableArray());
        }
        private void ConfigurePaper()
        {
            var size=_paper.OrientedSizeMm;var margins=_source.Style.Margins;
            var requested=new RectD(margins.LeftMm,margins.TopMm,size.Width-margins.LeftMm-margins.RightMm,size.Height-margins.TopMm-margins.BottomMm);
            _safe=requested.Intersect(_paper.SafeAreaMm);
            if(_safe.Width<2 || _safe.Height<2) throw new MarkdownLayoutException("Поля не оставляют места для Markdown. Уменьшите поля или выберите другой лист.");
            if(_paper.Notebook is {} notebook)
            {
                _nativePaper=notebook.NativeSize;_rotate=NotebookGeometry.IsRotated(_paper);
                _nativeColumns=NotebookGeometry.TextRegions(_paper,_safe).Select(r=>r.NativeBounds).ToArray();
                if(_nativeColumns.Length==2)
                {
                    var width=Math.Min(_nativeColumns[0].Width,_nativeColumns[1].Width);
                    _nativeColumns=_nativeColumns.Select(r=>r with {Width=width}).ToArray();
                }
            }
            else {_nativePaper=size;_nativeColumns=[_safe];}
            _columnWidth=_nativeColumns[0].Width*DipPerMm;_columnHeight=_nativeColumns[0].Height*DipPerMm;
        }
        private Paragraph NewParagraph(double em,bool keepNext=false)
        {
            var body=_source.Style.Body;
            return new() {Margin=new Thickness(0,0,0,_gap),KeepWithNext=keepNext,MinOrphanLines=2,MinWidowLines=2,
                LineHeight=body.FixedLineHeightMm is {} fixedHeight ? Math.Max(em*1.2,fixedHeight*DipPerMm) : em*1.2*body.LineSpacing};
        }
        private void AddBlocks(BlockCollection target,IEnumerable<Markdig.Syntax.Block> blocks,double available,int depth)
        {
            var items=blocks.Where(b=>b is not LinkReferenceDefinitionGroup and not FootnoteGroup && !IsHtmlScaffolding(b)).ToArray();
            for(var i=0;i<items.Length;i++)
            {
                var converted=ConvertBlock(items[i],available,depth);
                if(converted is Paragraph p && items[i] is ParagraphBlock introductory && i+1<items.Length
                    && items[i+1] is CodeBlock && introductory.Lines.ToString().Length<=400)p.KeepWithNext=true;
                target.Add(converted);
            }
        }
        private WpfBlock DisplayFormula(string source,double available,int line)
        {
            var visual=CreateFormula(source,TexStyle.Display,available,line,_em);visual.CenterInBlock=true;visual.Width=available;
            return new BlockUIContainer(visual){Margin=new Thickness(0,_gap/2,0,_gap),TextAlignment=System.Windows.TextAlignment.Center};
        }
        private WpfBlock ConvertBlock(Markdig.Syntax.Block block,double available,int depth)
        {
            _token.ThrowIfCancellationRequested();if(depth>32) throw new MarkdownLayoutException("Слишком глубокая вложенность Markdown.",block.Line+1);
            switch(block)
            {
                case HeadingBlock heading:
                    var size=_em*(heading.Level switch {1=>1.6,2=>1.35,3=>1.2,_=>1.05});
                    if(size*1.2*_source.Style.Body.LineSpacing>_columnHeight-_gap || Measure("W",_font,size)>available)
                        throw new MarkdownLayoutException("Заголовок не помещается. Уменьшите шрифт или поля.",heading.Line+1);
                    var hp=NewParagraph(size,true);hp.FontSize=size;hp.FontWeight=FontWeights.Bold;
                    if(heading.Inline!=null) AddInlines(hp.Inlines,heading.Inline,available,size,depth);return hp;
                case MathBlock math:
                    return DisplayFormula(math.Lines.ToString(),available,math.Line+1);
                case ParagraphBlock paragraph:
                    if(paragraph.Inline!=null)
                    {
                        var visible=paragraph.Inline.Where(i=>i is not LiteralInline literal || !string.IsNullOrWhiteSpace(literal.Content.ToString())).ToArray();
                        if(visible is [MathInline {DelimiterCount:>=2} display])return DisplayFormula(display.Content.ToString(),available,display.Line+1);
                    }
                    var p=NewParagraph(_em,HasHtmlSummary(paragraph));if(paragraph.Inline!=null) AddInlines(p.Inlines,paragraph.Inline,available,_em,depth);return p;
                case MdTable table:return ConvertTable(table,available,depth);
                case FencedCodeBlock code:return ConvertCode(code.Lines.ToString(),available,code.Line+1);
                case CodeBlock code:return ConvertCode(code.Lines.ToString(),available,code.Line+1);
                case QuoteBlock quote:
                    if(available<20) throw new MarkdownLayoutException("Цитата не помещается при этих полях и вложенности.",quote.Line+1);
                    var section=new Section {BorderBrush=_foreground,BorderThickness=new Thickness(1,0,0,0),Padding=new Thickness(7,0,0,0),Margin=new Thickness(0,0,0,_gap)};
                    AddBlocks(section.Blocks,quote,available-8,depth+1);return section;
                case ListBlock list:
                    if(available<30) throw new MarkdownLayoutException("Список не помещается при этих полях и вложенности.",list.Line+1);
                    var tasks=list.OfType<ListItemBlock>().Any(i=>i.OfType<ParagraphBlock>().FirstOrDefault()?.Inline?.Descendants<Markdig.Extensions.TaskLists.TaskList>().Any()==true);
                    var result=new System.Windows.Documents.List {MarkerStyle=tasks ? TextMarkerStyle.None : list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,Margin=new Thickness(18,0,0,_gap),Padding=new Thickness(0)};
                    if(list.IsOrdered && int.TryParse(list.OrderedStart,out var number) && number is >0 and <1000000) result.StartIndex=number;
                    var ordinal=result.StartIndex;
                    foreach(var item in list.OfType<ListItemBlock>())
                    {
                        var wi=new ListItem();AddBlocks(wi.Blocks,item,available-18,depth+1);
                        if(tasks && item.OfType<ParagraphBlock>().FirstOrDefault()?.Inline?.Descendants<Markdig.Extensions.TaskLists.TaskList>().Any()!=true && wi.Blocks.FirstBlock is Paragraph plain)
                            plain.Inlines.InsertBefore(plain.Inlines.FirstInline,new Run(list.IsOrdered ? $"{ordinal}. " : "• "));
                        result.ListItems.Add(wi);ordinal++;
                    }return result;
                case ThematicBreakBlock:
                    return new Paragraph {BorderBrush=_foreground,BorderThickness=new Thickness(0,0,0,.7),Margin=new Thickness(0,1,0,_gap),FontSize=1};
                case LinkReferenceDefinitionGroup:return NewParagraph(_em);
                default:throw new MarkdownLayoutException("Этот элемент Markdown пока не поддерживается: "+block.GetType().Name,block.Line+1);
            }
        }
        private WpfTable ConvertTable(MdTable table,double available,int depth)
        {
            var count=Math.Max(1,table.ColumnDefinitions.Count);var cellWidth=available/count-8;
            if(cellWidth<Measure("W",_font,_em)+1) throw new MarkdownLayoutException("Таблица слишком широкая для листа: уменьшите шрифт или число столбцов.",table.Line+1);
            var result=new WpfTable {CellSpacing=0,Margin=new Thickness(0,0,0,_gap)};
            for(var i=0;i<count;i++) result.Columns.Add(new TableColumn {Width=new GridLength(available/count)});
            var rows=new TableRowGroup();result.RowGroups.Add(rows);
            foreach(var row in table.OfType<MdRow>())
            {
                _token.ThrowIfCancellationRequested();var wr=new WpfRow();rows.Rows.Add(wr);
                var column=0;
                foreach(var cell in row.OfType<MdCell>())
                {
                    var wc=new WpfCell {BorderBrush=Brushes.Gray,BorderThickness=new Thickness(.5),Padding=new Thickness(3),ColumnSpan=cell.ColumnSpan,RowSpan=cell.RowSpan};
                    wc.TextAlignment=column<table.ColumnDefinitions.Count ? table.ColumnDefinitions[column].Alignment?.ToString() switch {"Center"=>System.Windows.TextAlignment.Center,"Right"=>System.Windows.TextAlignment.Right,_=>System.Windows.TextAlignment.Left} : System.Windows.TextAlignment.Left;
                    AddBlocks(wc.Blocks,cell,cellWidth*Math.Max(1,cell.ColumnSpan),depth+1);
                    foreach(var paragraph in wc.Blocks.OfType<Paragraph>()) paragraph.Margin=new Thickness(0);
                    if(row.IsHeader){wc.FontWeight=FontWeights.Bold;wc.Background=new SolidColorBrush(Color.FromRgb(240,240,240));}
                    wr.Cells.Add(wc);
                    column+=Math.Max(1,cell.ColumnSpan);
                }
            }
            return result;
        }
        private Paragraph ConvertCode(string code,double available,int sourceLine)
        {
            var em=Math.Max(8,_em*.85);var font=new FontFamily("Consolas");var p=NewParagraph(em);
            p.FontFamily=font;p.FontSize=em;p.LineHeight=em*1.25;p.Background=new SolidColorBrush(Color.FromRgb(245,245,245));
            p.TextAlignment=System.Windows.TextAlignment.Left;p.FlowDirection=FlowDirection.LeftToRight;p.FontWeight=FontWeights.Normal;p.FontStyle=FontStyles.Normal;p.TextIndent=0;
            var lineCount=0;
            foreach(var (line,index) in ExpandCodeTabs(code).Split('\n').Select((line,index)=>(line,index)))
            {
                _token.ThrowIfCancellationRequested();if(index>0)p.Inlines.Add(new LineBreak());lineCount++;
                var offsets=StringInfo.ParseCombiningCharacters(line);var first=0;
                while(first<offsets.Length)
                {
                    _token.ThrowIfCancellationRequested();var low=1;var high=Math.Min(2048,offsets.Length-first);var fit=0;var from=offsets[first];
                    while(low<=high)
                    {
                        var take=(low+high)/2;var end=first+take==offsets.Length ? line.Length : offsets[first+take];
                        if(Measure(line[from..end],font,em)<=available-1){fit=take;low=take+1;}else high=take-1;
                    }
                    if(fit==0) throw new MarkdownLayoutException("Даже один символ кода не помещается. Уменьшите шрифт или поля.",sourceLine+index);
                    var stop=first+fit==offsets.Length ? line.Length : offsets[first+fit];p.Inlines.Add(new Run(line[from..stop]));first+=fit;
                    if(first<offsets.Length){p.Inlines.Add(new LineBreak());lineCount++;}
                }
            }
            p.KeepTogether=lineCount*p.LineHeight+_gap<=_columnHeight;
            return p;
        }
        private double Measure(string value,FontFamily font,double em)
            =>new FormattedText(value,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface(font,FontStyles.Normal,FontWeights.Normal,FontStretches.Normal),em,_foreground,1).WidthIncludingTrailingWhitespace;
        private void AddInlines(InlineCollection target,ContainerInline parent,double available,double em,int depth)
        {
            if(depth>32) throw new MarkdownLayoutException("Слишком глубокая вложенность оформления Markdown.",parent.Line+1);
            var htmlTargets=new Stack<InlineCollection>();
            foreach(var inline in parent)
            {
                _token.ThrowIfCancellationRequested();
                if(inline is HtmlInline html && AddHtmlInline(html,ref target,htmlTargets,em))continue;
                switch(inline)
                {
                    case LiteralInline literal:target.Add(new Run(BreakLongTokens(literal.Content.ToString())));break;
                    case HtmlInline literalHtml:target.Add(new Run(BreakLongTokens(literalHtml.Tag)));break;
                    case HtmlEntityInline entity:target.Add(new Run(BreakLongTokens(entity.Transcoded.ToString())));break;
                    case CodeInline code:target.Add(new Run(BreakLongTokens(code.Content)) {FontFamily=new FontFamily("Consolas"),Background=new SolidColorBrush(Color.FromRgb(245,245,245))});break;
                    case MathInline math:
                        target.Add(new InlineUIContainer(CreateFormula(math.Content.ToString(),math.DelimiterCount>=2 ? TexStyle.Display : TexStyle.Text,available,math.Line+1,em)) {BaselineAlignment=BaselineAlignment.Center});break;
                    case EmphasisInline emphasis:
                        Span span=emphasis.DelimiterChar=='~' ? new Span{TextDecorations=TextDecorations.Strikethrough} : emphasis.DelimiterCount>=2 ? new Bold() : new Italic();AddInlines(span.Inlines,emphasis,available,em,depth+1);target.Add(span);break;
                    case Markdig.Extensions.TaskLists.TaskList task:
                        target.Add(new InlineUIContainer(new TaskCheckboxVisual(task.Checked,em,_foreground)){BaselineAlignment=BaselineAlignment.Center});target.Add(new Run(" "));break;
                    case FootnoteLink footnote:
                        AddFootnoteReference(target,footnote,available,em,depth);break;
                    case LinkInline {IsImage:true} image:target.Add(new InlineUIContainer(CreateImage(image,available)) {BaselineAlignment=BaselineAlignment.Bottom});break;
                    case LinkInline link:
                        var linked=new Span();AddInlines(linked.Inlines,link,available,em,depth+1);target.Add(linked);break;
                    case AutolinkInline auto:target.Add(new Run(BreakLongTokens(auto.Url)));break;
                    case LineBreakInline lineBreak:
                        if(lineBreak.IsHard)target.Add(new LineBreak());else target.Add(new Run(" "));break;
                    case ContainerInline child:
                        var group=new Span();AddInlines(group.Inlines,child,available,em,depth+1);target.Add(group);break;
                    default:throw new MarkdownLayoutException("Не поддерживается оформление: "+inline.GetType().Name,inline.Line+1);
                }
            }
        }
        private FormulaVisual CreateFormula(string source,TexStyle style,double available,int line,double em)
        {
            try
            {
                var key=$"{(int)style}|{em:R}|{_font}|{source}";
                if(!_formulaCache.TryGetValue(key,out var formula))
                {formula=MarkdownFormula.Make(source,style,em,_font.ToString(),_foreground);_formulaCache[key]=formula;}
                if(formula.Size.Width>available+.01 || formula.Size.Height>_columnHeight-_gap+.01)
                    throw new MarkdownLayoutException("Формула не помещается целиком. Уменьшите размер шрифта, измените поля или выберите более широкий лист.",line);
                var visual=new FormulaVisual(formula,_formulas.Count,line);_formulas.Add(visual);return visual;
            }
            catch(MarkdownLayoutException) {throw;}
            catch(Exception ex) {throw new MarkdownLayoutException("Не удалось оформить формулу: "+ex.Message,line,ex);}
        }
        private ImageVisual CreateImage(LinkInline image,double available)
        {
            var url=image.Url ?? "";var reference=_source.Images.FirstOrDefault(r=>r.Url==url);
            if(reference==null) throw new MarkdownLayoutException($"Картинка «{url}» не подключена. Добавьте локальный PNG, JPG или BMP.",image.Line+1);
            var asset=_assets.FirstOrDefault(a=>a.Id==reference.AssetId);
            if(asset is not {Kind:SourceKind.Image,WorkingPath:{} path} || !File.Exists(path)) throw new MarkdownLayoutException($"Файл картинки «{url}» не найден.",image.Line+1);
            try
            {
                _token.ThrowIfCancellationRequested();int pixelsWidth,pixelsHeight;
                using(var stream=File.OpenRead(path)) {var frame=BitmapDecoder.Create(stream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None).Frames[0];pixelsWidth=frame.PixelWidth;pixelsHeight=frame.PixelHeight;}
                if(pixelsWidth<=0 || pixelsHeight<=0) throw new InvalidDataException("Пустая картинка.");
                var naturalWidth=double.IsFinite(asset.PhysicalSizeMm.Width) && asset.PhysicalSizeMm.Width>0 ? asset.PhysicalSizeMm.Width*DipPerMm : pixelsWidth;
                var w=Math.Min(available,naturalWidth);var h=w*pixelsHeight/pixelsWidth;var maxHeight=Math.Max(1,_columnHeight-_gap-_em*1.5);
                if(h>maxHeight){w*=maxHeight/h;h=maxHeight;}
                var bitmap=new BitmapImage();
                using(var input=File.OpenRead(path))
                {
                    bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=input;
                    if(h>w)bitmap.DecodePixelHeight=Math.Max(1,(int)Math.Min(1600,Math.Ceiling(h*1.25)));else bitmap.DecodePixelWidth=Math.Max(1,(int)Math.Min(1600,Math.Ceiling(w*1.25)));
                    bitmap.EndInit();
                }
                bitmap.Freeze();_imageFiles[bitmap]=Path.GetFullPath(path);
                return new(bitmap,new Size(Math.Max(.1,w),Math.Max(.1,h)));
            }
            catch(OperationCanceledException) {throw;}
            catch(Exception ex) {throw new MarkdownLayoutException($"Не удалось прочитать картинку «{url}»: {ex.Message}",image.Line+1,ex);}
        }
        private static string BreakLongTokens(string text)
        {
            var b=new StringBuilder();var offsets=StringInfo.ParseCombiningCharacters(text);var start=0;
            while(start<offsets.Length)
            {
                var stop=start;while(stop<offsets.Length && !char.IsWhiteSpace(text[offsets[stop]]))stop++;
                if(stop==start){var end=start+1==offsets.Length ? text.Length : offsets[start+1];b.Append(text.AsSpan(offsets[start],end-offsets[start]));start++;continue;}
                for(var i=start;i<stop;i++)
                {var end=i+1==offsets.Length ? text.Length : offsets[i+1];b.Append(text.AsSpan(offsets[i],end-offsets[i]));if(stop-start>48 && i+1<stop && (i-start+1)%8==0)b.Append('\u200b');}
                start=stop;
            }
            return b.ToString();
        }
        private void CheckFormulas(Visual root,Size size,HashSet<int> seen)
        {
            void Visit(Visual visual)
            {
                _token.ThrowIfCancellationRequested();
                if(visual is FormulaVisual formula)
                {
                    var bounds=formula.TransformToAncestor(root).TransformBounds(new Rect(new Point(),formula.RenderSize));
                    if(!seen.Add(formula.Ordinal) || bounds.Top<-.1 || bounds.Bottom>size.Height+.1 || bounds.Left<-.1 || bounds.Right>size.Width+.1)
                        throw new MarkdownLayoutException("Формула пересекает границу страницы. Измените шрифт или поля.",formula.Line);
                }
                for(var i=0;i<VisualTreeHelper.GetChildrenCount(visual);i++)if(VisualTreeHelper.GetChild(visual,i) is Visual child)Visit(child);
            }
            Visit(root);
        }
    }
    private sealed class FormulaVisual(MarkdownFormula formula,int ordinal,int line) : FrameworkElement
    {
        public bool CenterInBlock {get;set;}
        public int Ordinal {get;}=ordinal;
        public int Line {get;}=line;
        protected override Size MeasureOverride(Size availableSize)=>formula.Size;
        protected override void OnRender(DrawingContext dc)
        {if(CenterInBlock)dc.PushTransform(new TranslateTransform(Math.Max(0,(RenderSize.Width-formula.Size.Width)/2),0));dc.DrawDrawing(formula.Drawing);if(CenterInBlock)dc.Pop();}
    }
    private sealed class TaskCheckboxVisual(bool isChecked,double em,Brush brush) : FrameworkElement
    {
        protected override Size MeasureOverride(Size availableSize)=>new(em*.8,em*.8);
        protected override void OnRender(DrawingContext dc)
        {
            var size=em*.8;var pen=new Pen(brush,Math.Max(.7,em*.065));dc.DrawRectangle(null,pen,new Rect(1,1,size-2,size-2));
            if(isChecked){dc.DrawLine(pen,new(size*.22,size*.5),new(size*.43,size*.72));dc.DrawLine(pen,new(size*.43,size*.72),new(size*.8,size*.25));}
        }
    }
    internal static string ExpandCodeTabs(string source)
    {
        var result=new StringBuilder();var column=0;
        foreach(var c in source.Replace("\r\n","\n").Replace('\r','\n'))
        {if(c=='\n'){result.Append(c);column=0;}else if(c=='\t'){var spaces=4-column%4;result.Append(' ',spaces);column+=spaces;}else{result.Append(c);if(!char.IsLowSurrogate(c))column++;}}
        return result.ToString();
    }
    private sealed class ImageVisual(ImageSource image,Size size) : FrameworkElement
    {
        protected override Size MeasureOverride(Size availableSize)=>size;
        protected override void OnRender(DrawingContext dc)=>dc.DrawImage(image,new Rect(new Point(),size));
    }
}

internal static class MarkdownVectors
{
    public static DrawingGroup Capture(Visual visual,CancellationToken token,StringBuilder? text=null)
    {
        token.ThrowIfCancellationRequested();var result=new DrawingGroup();var transform=VisualTreeHelper.GetTransform(visual);var offset=VisualTreeHelper.GetOffset(visual);
        var combined=new TransformGroup();if(transform!=null)combined.Children.Add(transform.CloneCurrentValue());combined.Children.Add(new TranslateTransform(offset.X,offset.Y));result.Transform=combined;
        result.Opacity=VisualTreeHelper.GetOpacity(visual);result.ClipGeometry=VisualTreeHelper.GetClip(visual)?.CloneCurrentValue();
        if(VisualTreeHelper.GetDrawing(visual) is {} drawing) result.Children.Add(Outline(drawing,token,text));
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(visual);i++)if(VisualTreeHelper.GetChild(visual,i) is Visual child)result.Children.Add(Capture(child,token,text));
        return result;
    }
    private static Drawing Outline(Drawing drawing,CancellationToken token,StringBuilder? text)
    {
        token.ThrowIfCancellationRequested();
        if(drawing is GlyphRunDrawing glyph)
        {if(glyph.GlyphRun.Characters!=null)text?.Append(new string(glyph.GlyphRun.Characters.ToArray()));return new GeometryDrawing(glyph.ForegroundBrush?.CloneCurrentValue(),null,glyph.GlyphRun.BuildGeometry());}
        // Preserve the frozen source identity, which resolves to an owned full-resolution file during printing.
        if(drawing is ImageDrawing image) return new ImageDrawing(image.ImageSource,image.Rect);
        if(drawing is DrawingGroup nested)
        {
            var group=new DrawingGroup {Transform=nested.Transform?.CloneCurrentValue() ?? Transform.Identity,Opacity=nested.Opacity,
                ClipGeometry=nested.ClipGeometry?.CloneCurrentValue(),OpacityMask=nested.OpacityMask?.CloneCurrentValue()};
            foreach(var child in nested.Children)group.Children.Add(Outline(child,token,text));return group;
        }
        return drawing.CloneCurrentValue();
    }
    public static IEnumerable<ImageSource> Images(Drawing drawing)
    {
        if(drawing is ImageDrawing image)yield return image.ImageSource;
        if(drawing is DrawingGroup group)foreach(var child in group.Children)foreach(var source in Images(child))yield return source;
    }
    public static long EstimateBytes(Drawing drawing)
    {
        return drawing switch
        {
            DrawingGroup group=>128+group.Children.Sum(EstimateBytes),
            ImageDrawing {ImageSource:BitmapSource bitmap}=>128+(long)bitmap.PixelWidth*bitmap.PixelHeight*4,
            GeometryDrawing geometry=>128+GeometryBytes(geometry.Geometry),
            _=>128
        };
    }
    private static long GeometryBytes(Geometry geometry)=>geometry switch
    {
        GeometryGroup group=>128+group.Children.Sum(GeometryBytes),
        PathGeometry path=>128+path.Figures.Sum(f=>64+f.Segments.Sum(s=>s switch {PolyLineSegment p=>48+p.Points.Count*16,PolyBezierSegment p=>48+p.Points.Count*16,PolyQuadraticBezierSegment p=>48+p.Points.Count*16,_=>96L})),
        _=>128
    };
}
