using Markdig.Extensions.Footnotes;
using Markdig.Syntax;
using PrintMaster.Models;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace PrintMaster.Services;

public sealed partial class MarkdownLayoutService
{
    private sealed partial class LayoutBuilder
    {
        private readonly List<FootnoteReference> _footnoteReferences=[];
        private readonly Dictionary<Footnote,int> _footnoteNumbers=[];
        private bool _insideFootnote;
        private sealed record FootnoteReference(FootnoteReferenceVisual Marker,Footnote Definition);
        private sealed record FooterDraft(double Height,DrawingGroup? Drawing,string Text);
        private const double FootnoteSeparatorSpace=8;

        private void PrepareFootnotes(Markdig.Syntax.MarkdownDocument parsed)
        {
            foreach(var note in parsed.Descendants<Footnote>())
                if(note.Order>0) _footnoteNumbers[note]=note.Order;
        }

        private void AddFootnoteReference(InlineCollection target,FootnoteLink link,double available,double em,int depth)
        {
            if(link.IsBackLink) return;
            if(_insideFootnote) throw new MarkdownLayoutException("Вложенные ссылки на сноски пока не поддерживаются. Перенесите ссылку в основной текст.",link.Line+1);
            if(!_footnoteNumbers.TryGetValue(link.Footnote,out var number))
                throw new MarkdownLayoutException("Не найдено определение сноски.",link.Line+1);
            // A retained visual identifies the exact page and notebook column without requiring a viewer's ITextView.
            var marker=new FootnoteReferenceVisual(number,_footnoteReferences.Count,_font,em*.72,_foreground,
                _source.Style.Body.Bold ? FontWeights.Bold : FontWeights.Normal,_source.Style.Body.Italic ? FontStyles.Italic : FontStyles.Normal);
            target.Add(new InlineUIContainer(marker) {BaselineAlignment=BaselineAlignment.Superscript});_footnoteReferences.Add(new(marker,link.Footnote));
        }

        // A bounded monotone allocation prevents the body and footnotes from ever sharing ink space.
        // The same bottom reserve applies to both notebook columns; only columns with references get a footer.
        private ImmutableArray<MarkdownRenderPage> PaginateWithFootnotes(FlowDocument document)
        {
            if(_footnoteReferences.Count==0) return CaptureMarkdownPages(document,new Dictionary<(int Page,int Column),FooterDraft>());
            var paginator=(DynamicDocumentPaginator)((IDocumentPaginatorSource)document).DocumentPaginator;
            paginator.IsBackgroundPaginationEnabled=false;
            paginator.PageSize=new(_nativePaper.Width*DipPerMm,_nativePaper.Height*DipPerMm);
            var originalPadding=document.PagePadding;
            var minimumBody=Math.Max(_em*1.2*_source.Style.Body.LineSpacing,_source.Style.Body.FixedLineHeightMm is {} line ? line*DipPerMm : 0)+_gap+2;
            var maximumReserve=_columnHeight-minimumBody;
            if(maximumReserve<FootnoteSeparatorSpace+_em)
                throw new MarkdownLayoutException("На листе недостаточно места одновременно для текста и сносок. Уменьшите шрифт или поля.");
            var drafts=new Dictionary<string,FooterDraft>(StringComparer.Ordinal);
            Dictionary<(int Page,int Column),FooterDraft> footers=[];
            var reserve=0d;
            for(var iteration=0;iteration<40;iteration++)
            {
                _token.ThrowIfCancellationRequested();
                document.PagePadding=new(originalPadding.Left,originalPadding.Top,originalPadding.Right,originalPadding.Bottom+reserve);
                var grouped=new Dictionary<(int Page,int Column),List<Footnote>>();
                var referenceSeen=new HashSet<int>();
                CountMarkdownPages(paginator,(pageIndex,page)=>VisitFootnoteReferences(page.Visual,page.Visual,(marker,x)=>
                {
                    _token.ThrowIfCancellationRequested();
                    if(!referenceSeen.Add(marker.Ordinal)) throw new MarkdownLayoutException("Ссылка на сноску оказалась на нескольких страницах.");
                    var reference=_footnoteReferences[marker.Ordinal];
                    var column=0;
                    if(_nativeColumns.Length>1)
                        for(var i=1;i<_nativeColumns.Length;i++)
                            if(x>=(_nativeColumns[i].X*DipPerMm)-.5) column=i;
                    var key=(pageIndex,column);
                    if(!grouped.TryGetValue(key,out var notes)) grouped[key]=notes=[];
                    if(!notes.Contains(reference.Definition)) notes.Add(reference.Definition);
                }));
                if(referenceSeen.Count!=_footnoteReferences.Count) throw new MarkdownLayoutException("Не удалось сохранить все ссылки на сноски при разбиении на страницы.");
                footers=[];var required=0d;
                foreach(var group in grouped)
                {
                    var notes=group.Value.OrderBy(n=>_footnoteNumbers[n]).ToArray();
                    var key=string.Join(",",notes.Select(n=>_footnoteNumbers[n]));
                    if(!drafts.TryGetValue(key,out var draft)) drafts[key]=draft=CreateFooterDraft(notes);
                    footers[group.Key]=draft;required=Math.Max(required,draft.Height+FootnoteSeparatorSpace+2);
                }
                if(required<=reserve+.1)
                {
                    if(footers.Values.Any(f=>f.Drawing==null))
                        throw new MarkdownLayoutException("Сноски одной страницы не помещаются целиком. Сократите сноски, уменьшите шрифт или увеличьте лист.");
                    return CaptureMarkdownPages(document,footers);
                }
                var next=Math.Min(maximumReserve,required);
                if(next<=reserve+.1)
                    throw new MarkdownLayoutException("Сноски одной страницы не помещаются целиком вместе с основным текстом. Сократите сноски, уменьшите шрифт или увеличьте лист.");
                reserve=next;
            }
            throw new MarkdownLayoutException("Не удалось устойчиво разместить сноски за 40 проходов. Упростите сноски или измените размер листа.");
        }

        private int CountMarkdownPages(DocumentPaginator paginator,Action<int,DocumentPage> inspect)
        {
            for(var index=0;;index++)
            {
                _token.ThrowIfCancellationRequested();
                if(paginator.IsPageCountValid && index>=paginator.PageCount) return index;
                if(index>=TextLimits.MaxPages) throw new MarkdownLayoutException("Markdown создает более 2000 страниц. Разделите документ.");
                using var page=paginator.GetPage(index);if(page==DocumentPage.Missing) return index;inspect(index,page);
            }
        }

        private void VisitFootnoteReferences(Visual root,Visual current,Action<FootnoteReferenceVisual,double> found)
        {
            _token.ThrowIfCancellationRequested();
            if(current is FootnoteReferenceVisual marker) found(marker,marker.TransformToAncestor(root).Transform(new Point()).X);
            for(var i=0;i<VisualTreeHelper.GetChildrenCount(current);i++)
                if(VisualTreeHelper.GetChild(current,i) is Visual child) VisitFootnoteReferences(root,child,found);
        }

        private sealed class FootnoteReferenceVisual : FrameworkElement
        {
            private readonly FormattedText _text;
            public int Ordinal {get;}
            public FootnoteReferenceVisual(int number,int ordinal,FontFamily font,double em,Brush foreground,FontWeight weight,FontStyle style)
            {
                Ordinal=ordinal;
                _text=new(number.ToString(CultureInfo.InvariantCulture),CultureInfo.CurrentCulture,FlowDirection.LeftToRight,
                    new Typeface(font,style,weight,FontStretches.Normal),em,foreground,1);
            }
            protected override Size MeasureOverride(Size availableSize)=>new(_text.WidthIncludingTrailingWhitespace,_text.Height);
            protected override void OnRender(DrawingContext drawingContext)=>drawingContext.DrawText(_text,new Point());
        }

        private FooterDraft CreateFooterDraft(IReadOnlyList<Footnote> notes)
        {
            var originalEm=_em;var originalGap=_gap;var formulaStart=_formulas.Count;
            _insideFootnote=true;
            try
            {
                _em=Math.Max(8,originalEm*.85);_gap=Math.Min(3,originalGap*.6);
                var footer=new FlowDocument {PageWidth=_columnWidth,PageHeight=_columnHeight,PagePadding=new Thickness(0),
                    ColumnWidth=_columnWidth,ColumnGap=0,IsColumnWidthFlexible=false,FontFamily=_font,FontSize=_em,Foreground=_foreground,
                    FontWeight=_source.Style.Body.Bold ? FontWeights.Bold : FontWeights.Normal,
                    FontStyle=_source.Style.Body.Italic ? FontStyles.Italic : FontStyles.Normal,
                    TextAlignment=System.Windows.TextAlignment.Left};
                foreach(var note in notes)
                {
                    _token.ThrowIfCancellationRequested();var section=new Section {Margin=new Thickness(0,0,0,_gap)};
                    foreach(var block in note) section.Blocks.Add(ConvertBlock(block,_columnWidth,0));
                    var prefix=new Run(_footnoteNumbers[note].ToString(CultureInfo.InvariantCulture)+". ");
                    if(section.Blocks.FirstBlock is Paragraph paragraph)
                    {if(paragraph.Inlines.FirstInline is {} first) paragraph.Inlines.InsertBefore(first,prefix);else paragraph.Inlines.Add(prefix);}
                    else section.Blocks.InsertBefore(section.Blocks.FirstBlock ?? AddEmptyFootnote(section),new Paragraph(prefix) {Margin=new Thickness(0),KeepWithNext=true});
                    footer.Blocks.Add(section);
                }
                var paginator=((IDocumentPaginatorSource)footer).DocumentPaginator;paginator.PageSize=new(_columnWidth,_columnHeight);
                using var page=paginator.GetPage(0);using var extra=paginator.GetPage(1);
                if(page==DocumentPage.Missing || extra!=DocumentPage.Missing) return new(_columnHeight+1,null,"");
                var seen=new HashSet<int>();CheckFormulas(page.Visual,page.Size,seen);
                if(seen.Count!=_formulas.Count-formulaStart) throw new MarkdownLayoutException("Не удалось сохранить формулу в сноске.");
                var text=new StringBuilder();var drawing=MarkdownVectors.Capture(page.Visual,_token,text);
                // TextPointer geometry requires a FlowDocument viewer. Printing has none; measure the retained ink instead.
                var ink=FootnoteInkBounds(drawing,Matrix.Identity);
                var height=ink.IsEmpty ? 0 : ink.Bottom+_gap;
                if(!double.IsFinite(height) || height<0) throw new MarkdownLayoutException("Не удалось измерить высоту сноски.");
                if(!drawing.CanFreeze) throw new MarkdownLayoutException("Не удалось подготовить векторную сноску.");drawing.Freeze();
                return new(height,drawing,text.ToString());
            }
            finally
            {
                _insideFootnote=false;_em=originalEm;_gap=originalGap;
                // Intermediate footer measurements own their formulas. Body ordinals remain stable across repagination.
                if(_formulas.Count>formulaStart) _formulas.RemoveRange(formulaStart,_formulas.Count-formulaStart);
            }
        }

        private static Paragraph AddEmptyFootnote(Section section)
        {var p=new Paragraph();section.Blocks.Add(p);return p;}

        private static Rect FootnoteInkBounds(Drawing drawing,Matrix parentTransform)
        {
            static bool Visible(Brush? brush)=>brush is not null && brush.Opacity>0 && (brush is not SolidColorBrush solid || solid.Color.A>0);
            if(drawing is DrawingGroup group)
            {
                if(group.Opacity==0)return Rect.Empty;
                var transform=group.Transform?.Value ?? Matrix.Identity;transform.Append(parentTransform);var result=Rect.Empty;
                foreach(var child in group.Children)result.Union(FootnoteInkBounds(child,transform));return result;
            }
            if(drawing is GeometryDrawing geometry && !Visible(geometry.Brush) && !Visible(geometry.Pen?.Brush))return Rect.Empty;
            var bounds=drawing.Bounds;
            return bounds.IsEmpty ? Rect.Empty : new MatrixTransform(parentTransform).TransformBounds(bounds);
        }

        private ImmutableArray<MarkdownRenderPage> CaptureMarkdownPages(FlowDocument document,IReadOnlyDictionary<(int Page,int Column),FooterDraft> footers)
        {
            var paginator=((IDocumentPaginatorSource)document).DocumentPaginator;paginator.PageSize=new(_nativePaper.Width*DipPerMm,_nativePaper.Height*DipPerMm);
            var pages=ImmutableArray.CreateBuilder<MarkdownRenderPage>();var seen=new HashSet<int>();
            for(var index=0;;index++)
            {
                _token.ThrowIfCancellationRequested();
                if(paginator.IsPageCountValid && index>=paginator.PageCount) break;
                if(index>=TextLimits.MaxPages) throw new MarkdownLayoutException("Markdown создает более 2000 страниц. Разделите документ.");
                using var page=paginator.GetPage(index);if(page==DocumentPage.Missing) break;
                CheckFormulas(page.Visual,page.Size,seen);
                var actualText=new StringBuilder();var drawing=MarkdownVectors.Capture(page.Visual,_token,actualText);
                foreach(var pair in footers.Where(pair=>pair.Key.Page==index).OrderBy(pair=>pair.Key.Column))
                {
                    var footer=pair.Value;var column=_nativeColumns[pair.Key.Column];
                    var group=new DrawingGroup {Transform=new TranslateTransform(column.X*DipPerMm,column.Bottom*DipPerMm-footer.Height-FootnoteSeparatorSpace)};
                    group.Children.Add(new GeometryDrawing(null,new Pen(_foreground,.6),new LineGeometry(new(0,0),new(Math.Min(80,_columnWidth*.35),0))));
                    var textGroup=new DrawingGroup {Transform=new TranslateTransform(0,FootnoteSeparatorSpace)};textGroup.Children.Add(footer.Drawing!);group.Children.Add(textGroup);
                    var combined=new DrawingGroup();combined.Children.Add(drawing);combined.Children.Add(group);drawing=combined;actualText.Append(footer.Text);
                }
                if(_rotate)
                {var rotated=new DrawingGroup {Transform=new MatrixTransform(0,1,-1,0,_nativePaper.Height*DipPerMm,0)};rotated.Children.Add(drawing);drawing=rotated;}
                if(!drawing.CanFreeze) throw new MarkdownLayoutException("Не удалось подготовить независимую векторную страницу.");drawing.Freeze();
                var images=ImmutableDictionary.CreateBuilder<ImageSource,string>();
                foreach(var image in MarkdownVectors.Images(drawing))
                {if(!_imageFiles.TryGetValue(image,out var path)) throw new MarkdownLayoutException("Потеряна связь картинки с исходным файлом.");images[image]=path;}
                pages.Add(new(drawing,_paper.OrientedSizeMm,_safe,images.ToImmutable()) {DiagnosticText=actualText.ToString()});
            }
            if(pages.Count==0) throw new MarkdownLayoutException("Не удалось создать страницу Markdown.");
            if(seen.Count!=_formulas.Count) throw new MarkdownLayoutException("Не удалось сохранить все формулы при разбиении на страницы.");
            return pages.ToImmutable();
        }
    }
}
