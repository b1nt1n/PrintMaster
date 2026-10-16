using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.Collections.Immutable;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace PrintMaster.Tests;

public sealed class MarkdownEditorViewModelTests
{
    private static PreparedMarkdown Initial(string source="# Проверка\n\nТекст $x^2$.")=>new(new(Guid.NewGuid(),source,MarkdownStyle.Default,[],"Markdown") {SourceDirectory=@"C:\fixture-markdown"},[]);
    private static Task<PreparedMarkdown> Prepare(MarkdownDocument doc,CancellationToken token)=>Task.FromResult(new PreparedMarkdown(doc,[]));
    private static DrawingGroup Drawing()
    {
        var drawing=new DrawingGroup();drawing.Children.Add(new GeometryDrawing(Brushes.Black,null,new RectangleGeometry(new(20,20,30,30))));drawing.Freeze();return drawing;
    }
    private static MarkdownLayoutResult Layout(SheetPaper paper,int pages=1,params MarkdownDiagnostic[] diagnostics)
        =>new(Enumerable.Range(0,pages).Select(_=>new MarkdownRenderPage(Drawing(),paper.OrientedSizeMm,paper.SafeAreaMm,ImmutableDictionary<ImageSource,string>.Empty)).ToImmutableArray(),diagnostics.ToImmutableArray());
    private static Task<MarkdownLayoutResult> FakeLayout(MarkdownDocument doc,SheetPaper paper,ImmutableArray<SourceAsset> assets,CancellationToken token)
        =>Task.FromResult(Layout(paper));
    private static BitmapSource Pixel(byte value)
    {var image=BitmapSource.Create(1,1,96,96,PixelFormats.Bgra32,null,new byte[] {value,value,value,255},4);image.Freeze();return image;}

    [Fact]
    public async Task InitialSourceAndMetadataArePreservedInTheLatestPreparedDocument()
    {
        var initial=Initial();using var vm=new MarkdownEditorViewModel(initial,SheetPaper.A4,Prepare,false,FakeLayout);
        Assert.False(vm.CanApply);await vm.RefreshAsync();
        Assert.True(vm.CanApply);Assert.Equal(initial.Document,vm.Result!.Document);Assert.Equal(initial.Document.Source,vm.Source);
        Assert.Equal(initial.Document.Style,vm.Style);Assert.Equal(SheetPaper.A4,vm.Paper);Assert.Equal(1,vm.PageCount);Assert.False(vm.IsUpdating);
        Assert.True(vm.Layout!.Pages[0].Drawing.IsFrozen);Assert.Null(vm.Preview);Assert.False(vm.IsPreviewLoading);
    }

    [Fact]
    public async Task FormattingAndPaperChangesAreCapturedTogetherWithoutRewritingMarkdown()
    {
        var initial=Initial("### Формула\n\n$$\\frac{x^2}{x-2}$$");
        initial=initial with {Document=initial.Document with {Style=initial.Document.Style with {Body=initial.Document.Style.Body with {Bold=true,Italic=true,Alignment=PrintMaster.Models.TextAlignment.Right}}}};
        MarkdownDocument? captured=null;SheetPaper? capturedPaper=null;var prepareThreadWasWorker=false;
        using var vm=new MarkdownEditorViewModel(initial,SheetPaper.A4,(doc,_)=>{prepareThreadWasWorker=Thread.CurrentThread.IsThreadPoolThread;return Prepare(doc,default);},false,
            (doc,paper,_,_)=>{captured=doc;capturedPaper=paper;return Task.FromResult(Layout(paper));});
        vm.FontFamily="Segoe UI";vm.FontSizePt=17;vm.Color="#C62828";vm.LineSpacing=1.4;vm.ParagraphGapMm=3;
        vm.MarginLeftMm=12;vm.MarginTopMm=13;vm.MarginRightMm=14;vm.MarginBottomMm=15;vm.Title="Формулы";
        vm.PaperFormat="A5";vm.Landscape=true;await vm.RefreshAsync();
        Assert.True(vm.CanApply);Assert.True(prepareThreadWasWorker);Assert.Equal(initial.Document.Source,vm.Source);Assert.Equal(initial.Document.Source,captured!.Source);
        Assert.Equal(new SheetMargins(12,13,14,15),captured.Style.Margins);Assert.Equal(17,captured.Style.Body.FontSizePt);
        Assert.Equal("#C62828",captured.Style.Body.Color);Assert.Equal(1.4,captured.Style.Body.LineSpacing);Assert.Equal(3,captured.Style.ParagraphGapMm);
        Assert.True(captured.Style.Body.Bold);Assert.True(captured.Style.Body.Italic);Assert.Equal(PrintMaster.Models.TextAlignment.Right,captured.Style.Body.Alignment);
        Assert.Equal(initial.Document.Id,captured.Id);Assert.Equal(initial.Document.SourceDirectory,captured.SourceDirectory);Assert.Equal("Формулы",captured.Title);
        Assert.Equal(new SizeD(210,148),capturedPaper!.OrientedSizeMm);Assert.Equal(captured,vm.Result!.Document);
    }

    [Fact]
    public async Task FormulaFailureKeepsRawSourceAndReportsItsLineInsteadOfAllowingAnOldResult()
    {
        var initial=Initial();using var vm=new MarkdownEditorViewModel(initial,SheetPaper.A4,Prepare,false,
            (doc,paper,_,_)=>doc.Source.Contains("unknown",StringComparison.Ordinal) ? throw new MarkdownLayoutException("Неизвестная команда unknown",3) : Task.FromResult(Layout(paper)));
        await vm.RefreshAsync();Assert.True(vm.CanApply);
        var source="# Исходник\n\n$$\\unknown{x}$$";vm.Source=source;Assert.False(vm.CanApply);await vm.RefreshAsync();
        Assert.Equal(source,vm.Source);Assert.Equal(source,vm.DraftDocument.Source);Assert.False(vm.CanApply);Assert.Null(vm.Result);Assert.Null(vm.Layout);
        Assert.True(vm.HasError);Assert.True(vm.HasErrorLine);Assert.Equal(3,vm.ErrorLine);Assert.Contains("Строка 3",vm.Error);Assert.Contains(".md",vm.ApplyHint);
        vm.Source=initial.Document.Source;await vm.RefreshAsync();Assert.True(vm.CanApply);Assert.Empty(vm.Error);Assert.Null(vm.ErrorLine);
    }

    [Theory]
    [InlineData("font",0)] [InlineData("font",double.NaN)] [InlineData("spacing",0.5)] [InlineData("spacing",4)]
    [InlineData("margin",-1)] [InlineData("margin",double.PositiveInfinity)] [InlineData("margin",210)] [InlineData("gap",101)]
    public async Task InvalidFormattingBlocksApplyAndKeepsEditableSource(string option,double value)
    {
        using var vm=new MarkdownEditorViewModel(Initial(),SheetPaper.A4,Prepare,false,FakeLayout);await vm.RefreshAsync();var source=vm.Source;
        switch(option) {case "font":vm.FontSizePt=value;break;case "spacing":vm.LineSpacing=value;break;case "margin":vm.MarginLeftMm=value;break;default:vm.ParagraphGapMm=value;break;}
        await vm.UpdateTask;Assert.False(vm.CanApply);Assert.True(vm.HasError);Assert.Equal(source,vm.Source);Assert.Null(vm.Result);
    }

    [Fact]
    public async Task EmptyAndOversizedSourceAreNotSilentlyTruncated()
    {
        using var vm=new MarkdownEditorViewModel(Initial(),SheetPaper.A4,Prepare,false,FakeLayout);await vm.RefreshAsync();
        vm.Source=" \n ";await vm.UpdateTask;Assert.False(vm.CanApply);Assert.Equal(" \n ",vm.Source);
        var large=new string('a',TextLimits.MaxCharacters+1);vm.Source=large;await vm.UpdateTask;
        Assert.False(vm.CanApply);Assert.Same(large,vm.Source);Assert.Contains("Markdown",vm.Error);
        vm.Source="Исправленный документ";await vm.RefreshAsync();Assert.True(vm.CanApply);Assert.Equal("Исправленный документ",vm.Result!.Document.Source);
    }

    [Fact]
    public async Task SuccessfulLayoutWarningsDoNotPreventApplyingAndKeepLineInformation()
    {
        using var vm=new MarkdownEditorViewModel(Initial(),SheetPaper.A4,Prepare,false,
            (_,paper,_,_)=>Task.FromResult(Layout(paper,1,new MarkdownDiagnostic("Предупреждение шрифта",2,"font-fallback"))));
        await vm.RefreshAsync();Assert.True(vm.CanApply);Assert.True(vm.HasDiagnostics);Assert.Contains("Строка 2",vm.Diagnostics);Assert.Empty(vm.Error);
        vm.FontFamily="PrintMaster Missing Font Fixture";Assert.True(vm.HasFontWarning);Assert.Contains("Segoe UI",vm.FontWarning);
    }

    [Fact]
    public async Task PageCommandsHaveBoundariesAndSelectionClampsAfterRepagination()
    {
        using var vm=new MarkdownEditorViewModel(Initial(),SheetPaper.A4,Prepare,false,
            (doc,paper,_,_)=>Task.FromResult(Layout(paper,doc.Source=="короткий" ? 1 : 3)));
        await vm.RefreshAsync();Assert.Equal(3,vm.PageCount);Assert.Equal(0,vm.SelectedPageIndex);Assert.False(vm.CanGoPrevious);Assert.True(vm.CanGoNext);
        vm.NextPageCommand.Execute(null);vm.NextPageCommand.Execute(null);Assert.Equal(2,vm.SelectedPageIndex);Assert.False(vm.CanGoNext);Assert.True(vm.CanGoPrevious);
        vm.NextPageCommand.Execute(null);Assert.Equal(2,vm.SelectedPageIndex);vm.PreviousPageCommand.Execute(null);Assert.Equal(1,vm.SelectedPageIndex);
        vm.SelectedPageIndex=100;Assert.Equal(2,vm.SelectedPageIndex);vm.Source="короткий";await vm.RefreshAsync();
        Assert.Equal(1,vm.PageCount);Assert.Equal(0,vm.SelectedPageIndex);Assert.False(vm.CanGoNext);Assert.False(vm.CanGoPrevious);Assert.Contains("1 из 1",vm.PageLabel);
    }

    [Fact]
    public async Task CurrentNotebookProfileAndItsMeasuredDimensionsSurviveEditing()
    {
        var profile=NotebookProfiles.Default(NotebookVariant.GridSpread) with {Name="Измеренная тетрадь",WidthMm=332,HeightMm=207,FoldMm=166,LeftMarginMm=19,RightMarginMm=21};
        var paper=profile.ToPaper();using var vm=new MarkdownEditorViewModel(Initial(),paper,Prepare,false,FakeLayout);
        await vm.RefreshAsync();Assert.Equal(paper,vm.Paper);Assert.True(vm.IsNotebookPaper);Assert.False(vm.CanChangeNotebookVariant);
        vm.Source="Обновлённый текст";await vm.RefreshAsync();Assert.Equal(profile,vm.Paper.Notebook);Assert.Equal(new SizeD(332,207),vm.Layout!.Pages[0].PaperMm);
        vm.PaperFormat="Notebook";await vm.RefreshAsync();Assert.Equal(profile,vm.Paper.Notebook);
        vm.NotebookVariant=NotebookVariant.RuledSingle;await vm.RefreshAsync();Assert.Equal(NotebookVariant.RuledSingle,vm.Paper.Notebook!.Variant);Assert.False(vm.Paper.Landscape);
        vm.PaperFormat="A4";await vm.RefreshAsync();Assert.Null(vm.Paper.Notebook);Assert.Equal(new SizeD(210,297),vm.Paper.OrientedSizeMm);
    }

    [Fact]
    public async Task CanceledOlderLayoutCannotOverwriteLatestSourcePaperOrPreparedImages()
    {
        var entered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var vm=new MarkdownEditorViewModel(Initial("старый"),SheetPaper.A4,Prepare,false,async (doc,paper,_,_)=>{
            if(doc.Source=="старый") {entered.TrySetResult(true);await release.Task;}
            return Layout(paper,doc.Source=="старый" ? 4 : 2);
        });
        var old=vm.RefreshAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));vm.Source="новый";vm.PaperFormat="A5";await vm.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(vm.CanApply);Assert.Equal("новый",vm.Result!.Document.Source);Assert.Equal(2,vm.PageCount);Assert.Equal(new SizeD(148,210),vm.Layout!.Pages[0].PaperMm);
            var latest=vm.Result;release.TrySetResult(true);await old.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(latest,vm.Result);Assert.Equal(2,vm.PageCount);Assert.Empty(vm.Error);
        }
        finally {release.TrySetResult(true);}
    }

    [Fact]
    public async Task ImagePreparationMayResolveMaterialsButCannotRewriteSourceOrStyle()
    {
        using var vm=new MarkdownEditorViewModel(Initial(),SheetPaper.A4,(doc,_)=>Task.FromResult(new PreparedMarkdown(doc with {Source="изменён без участия автора"},[])),false,FakeLayout);
        var original=vm.Source;await vm.RefreshAsync();Assert.False(vm.CanApply);Assert.Equal(original,vm.Source);Assert.Contains("изменила исходник",vm.Error);
    }

    [Fact]
    public async Task DisposalCancelsPendingWorkAndPreservesAlreadyAcceptedResult()
    {
        var entered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm=new MarkdownEditorViewModel(Initial(),SheetPaper.A4,Prepare,false,async (doc,paper,_,_)=>{
            if(doc.Source=="поздний") {entered.TrySetResult(true);await release.Task;}
            return Layout(paper);
        });
        try
        {
            await vm.RefreshAsync();var accepted=vm.Result;var acceptedLayout=vm.Layout;vm.Source="поздний";var late=vm.RefreshAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));vm.Dispose();vm.Dispose();release.TrySetResult(true);await late.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(accepted,vm.Result);Assert.Same(acceptedLayout,vm.Layout);Assert.False(vm.CanApply);Assert.False(vm.IsUpdating);Assert.Equal("поздний",vm.Source);
        }
        finally {release.TrySetResult(true);vm.Dispose();}
    }

    [Fact]
    public async Task LatePreviewForPreviousPageCannotReplaceTheSelectedPage()
    {
        var entered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);var calls=0;
        using var vm=new MarkdownEditorViewModel(Initial(),SheetPaper.A4,Prepare,true,(_,paper,_,_)=>Task.FromResult(Layout(paper,2)),(_,_)=>{
            if(Interlocked.Increment(ref calls)==1) {entered.TrySetResult(true);release.Task.GetAwaiter().GetResult();return Pixel(10);}
            return Pixel(200);
        });
        try
        {
            await vm.RefreshAsync();await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));var old=vm.PreviewTask;
            vm.NextPageCommand.Execute(null);await vm.PreviewTask.WaitAsync(TimeSpan.FromSeconds(5));var latest=vm.Preview;
            Assert.True(vm.CanApply);Assert.NotNull(latest);var pixels=new byte[4];latest.CopyPixels(pixels,4,0);Assert.Equal(200,pixels[0]);
            release.TrySetResult(true);await old.WaitAsync(TimeSpan.FromSeconds(5));Assert.Same(latest,vm.Preview);Assert.Equal(1,vm.SelectedPageIndex);
        }
        finally {release.TrySetResult(true);}
    }

    [Fact]
    public async Task RealMathLayoutAndPreviewKeepSourceAndStayBelowPrintResolution()
    {
        var initial=Initial("# Формулы\n\nРусский текст $x^2$.\n\n$$\\frac{1}{2}$$");
        using var vm=new MarkdownEditorViewModel(initial,SheetPaper.A4,Prepare);
        await vm.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(20));await vm.PreviewTask.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Empty(vm.Error);Assert.Empty(vm.PreviewError);Assert.True(vm.CanApply);Assert.Equal(initial.Document.Source,vm.Result!.Document.Source);
        var preview=Assert.IsAssignableFrom<BitmapSource>(vm.Preview);Assert.True(preview.IsFrozen);Assert.InRange(preview.PixelWidth,1,1000);Assert.InRange(preview.PixelHeight,1,1000);
        Assert.All(vm.Layout!.Pages,p=>Assert.True(p.Drawing.IsFrozen));Assert.Equal(1,vm.PageCount);
    }

    [Fact]
    public async Task FailedPreviewCannotBeAppliedAndSuccessfulRetryClearsTheVisibleError()
    {
        var fail=true;var source=Initial("# Сохранить исходник\n\n$$x^2$$");
        using var vm=new MarkdownEditorViewModel(source,SheetPaper.A4,Prepare,true,FakeLayout,(_,_)=>
        {if(fail)throw new InvalidOperationException("Не удалось нарисовать формулу");return Pixel(90);});
        await vm.RefreshAsync();await vm.PreviewTask;
        Assert.False(vm.CanApply);Assert.True(vm.HasPreviewError);Assert.True(vm.HasBlockingError);Assert.Null(vm.Preview);
        Assert.Contains("формулу",vm.BlockingError);Assert.Contains("не готов",vm.PreviewStatus);Assert.Contains(".md",vm.ApplyHint);
        Assert.Equal(source.Document.Source,vm.Source);Assert.Equal(source.Document.Source,vm.DraftDocument.Source);
        fail=false;await vm.RefreshAsync();await vm.PreviewTask;
        Assert.True(vm.CanApply);Assert.False(vm.HasBlockingError);Assert.Empty(vm.BlockingError);Assert.NotNull(vm.Preview);
    }

    [Fact]
    public async Task PendingPreviewCannotBeAppliedBeforeItsImageIsReady()
    {
        var entered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var vm=new MarkdownEditorViewModel(Initial(),SheetPaper.A4,Prepare,true,FakeLayout,(_,_)=>
        {entered.TrySetResult(true);release.Task.GetAwaiter().GetResult();return Pixel(70);});
        try
        {
            await vm.RefreshAsync();await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(vm.IsPreviewLoading);Assert.False(vm.CanApply);Assert.Contains("предварительного",vm.ApplyHint);
            release.TrySetResult(true);await vm.PreviewTask.WaitAsync(TimeSpan.FromSeconds(5));Assert.True(vm.CanApply);
        }
        finally {release.TrySetResult(true);}
    }

    [Fact]
    public async Task PageNavigationDuringNewLayoutCannotStartAPreviewOfThePreviousDocument()
    {
        var entered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);var renders=0;
        using var vm=new MarkdownEditorViewModel(Initial(),SheetPaper.A4,Prepare,true,async(doc,paper,_,_)=>
        {
            if(doc.Source=="ошибка") {entered.TrySetResult(true);await release.Task;throw new MarkdownLayoutException("Ошибка формулы",1);}
            return Layout(paper,2);
        },(_,_)=>{Interlocked.Increment(ref renders);return Pixel(100);});
        try
        {
            await vm.RefreshAsync();await vm.PreviewTask;Assert.Equal(1,renders);Assert.True(vm.CanGoNext);
            vm.Source="ошибка";var update=vm.RefreshAsync();await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(vm.Preview);Assert.False(vm.CanGoNext);Assert.False(vm.NextPageCommand.CanExecute(null));
            vm.NextPageCommand.Execute(null);vm.SelectedPageIndex=1;await vm.PreviewTask;
            Assert.Equal(1,renders);Assert.Null(vm.Preview);
            release.TrySetResult(true);await update.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(vm.HasBlockingError);Assert.False(vm.CanApply);Assert.Null(vm.Preview);Assert.Null(vm.Layout);Assert.Equal("ошибка",vm.Source);
        }
        finally {release.TrySetResult(true);}
    }
}
