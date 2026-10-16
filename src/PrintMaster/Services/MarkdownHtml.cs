using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace PrintMaster.Services;

public sealed partial class MarkdownLayoutService
{
    private sealed partial class LayoutBuilder
    {
        private readonly Dictionary<HtmlInline,PrintHtmlTag> _htmlTags=[];
        private sealed record PrintHtmlTag(string Name,bool Closing);

        // This is deliberately not an HTML renderer: no attributes, CSS, URLs, browser,
        // scripts or DOM are interpreted. Markdig has already excluded escaped text,
        // code spans/fences and math before any whitelist token reaches this method.
        private void PrepareHtml(Markdig.Syntax.MarkdownDocument document)
        {
            var pending=new Stack<(HtmlInline Node,PrintHtmlTag Tag)>();
            var warned=new HashSet<HtmlInline>();
            void Warn(HtmlInline node,string reason)
            {
                if(warned.Add(node))_diagnostics.Add(new($"HTML «{ShortTag(node.Tag)}» оставлен обычным текстом: {reason}",node.Line+1,"html-literal"));
            }
            foreach(var node in document.Descendants<HtmlInline>().OrderBy(n=>n.Span.Start))
            {
                _token.ThrowIfCancellationRequested();
                if(!TryHtmlTag(node.Tag,out var tag))
                {Warn(node,"поддерживаются только парные sub, sup, kbd, details и summary без атрибутов.");continue;}
                if(!tag.Closing)
                {
                    if(pending.Count>=32)throw new MarkdownLayoutException("Слишком глубокая вложенность HTML-оформления.",node.Line+1);
                    pending.Push((node,tag));continue;
                }
                if(pending.Count==0 || pending.Peek().Tag.Name!=tag.Name)
                {Warn(node,"нет соответствующего открывающего тега.");continue;}
                var opening=pending.Pop();
                if(tag.Name!="details" && !ReferenceEquals(opening.Node.Parent,node.Parent))
                {Warn(opening.Node,"оформление должно целиком находиться в одном абзаце.");Warn(node,"оформление пересекает границу абзаца.");continue;}
                if(tag.Name=="summary" && !pending.Any(t=>t.Tag.Name=="details"))
                {Warn(opening.Node,"summary должен находиться внутри details.");Warn(node,"summary должен находиться внутри details.");continue;}
                _htmlTags[opening.Node]=opening.Tag;_htmlTags[node]=tag;
            }
            foreach(var opening in pending)Warn(opening.Node,"нет соответствующего закрывающего тега.");
        }

        private static string ShortTag(string source)=>source.Length<=80 ? source : source[..77]+"…";

        private static bool TryHtmlTag(string source,out PrintHtmlTag tag)
        {
            tag=null!;
            if(source.Length<5 || source[0]!='<' || source[^1]!='>')return false;
            var body=source.AsSpan(1,source.Length-2);var closing=body[0]=='/';if(closing)body=body[1..];
            body=body.TrimEnd();var name=body.ToString().ToLowerInvariant();
            if(name is not ("sub" or "sup" or "kbd" or "details" or "summary"))return false;
            tag=new(name,closing);return true;
        }

        private bool IsHtmlScaffolding(Markdig.Syntax.Block block)
            =>block is ParagraphBlock {Inline:{} content} && content.All(inline=>inline switch
            {
                HtmlInline html=>_htmlTags.TryGetValue(html,out var tag) && tag.Name=="details",
                LiteralInline text=>string.IsNullOrWhiteSpace(text.Content.ToString()),
                LineBreakInline=>true,
                _=>false
            });

        private bool HasHtmlSummary(ParagraphBlock block)
            =>block.Inline?.Descendants<HtmlInline>().Any(h=>_htmlTags.TryGetValue(h,out var tag) && tag.Name=="summary" && !tag.Closing)==true;

        private bool AddHtmlInline(HtmlInline node,ref InlineCollection destination,Stack<InlineCollection> parents,double em)
        {
            if(!_htmlTags.TryGetValue(node,out var tag))return false;
            if(tag.Name=="details")return true; // Always expanded in a printed document.
            if(tag.Closing){destination=parents.Pop();return true;}
            Span span=tag.Name switch
            {
                "sub"=>new Span {BaselineAlignment=BaselineAlignment.Subscript,FontSize=em*.75},
                "sup"=>new Span {BaselineAlignment=BaselineAlignment.Superscript,FontSize=em*.75},
                "kbd"=>new Span {FontFamily=new FontFamily("Consolas"),FontWeight=FontWeights.Normal,FontStyle=FontStyles.Normal,Background=new SolidColorBrush(Color.FromRgb(245,245,245))},
                "summary"=>new Bold(),
                _=>throw new InvalidOperationException("Неизвестный разрешённый HTML-тег.")
            };
            destination.Add(span);parents.Push(destination);destination=span.Inlines;return true;
        }
    }
}
