using PrintMaster.Controls;
using PrintMaster.Models;
using PrintMaster.Services;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace PrintMaster.Tests;

public sealed class ControlReviewRegressionTests
{
    [Fact]
    public async Task AnnotationSafeFrameRendersAfterInitializationOnAnotherStaThread()
    {
        var firstThread=0;
        await OnSta(()=>
        {
            firstThread=Thread.CurrentThread.ManagedThreadId;
            _=new AnnotationCanvas {Tool=EditorTool.Brush};
        });
        await OnSta(()=>
        {
            Assert.NotEqual(firstThread,Thread.CurrentThread.ManagedThreadId);
            var canvas=new AnnotationCanvas {Tool=EditorTool.Brush,Width=100,Height=120,SafeArea=new(5,5,80,100)};
            canvas.Measure(new Size(100,120)); canvas.Arrange(new Rect(0,0,100,120));
            var raster=new RenderTargetBitmap(100,120,96,96,PixelFormats.Pbgra32); raster.Render(canvas);
            var pixels=new byte[100*120*4]; raster.CopyPixels(pixels,100*4,0);
            var framePixels=Enumerable.Range(0,100*120).Count(index=>pixels[index*4]<240 && pixels[index*4+1]<240 && pixels[index*4+2]<240);
            Assert.True(framePixels>30,$"Safe area frame must remain visible; found {framePixels} dark pixels.");
            Assert.Equal(255,pixels[3]); Assert.Equal(255,pixels[0]);
        });
    }

    [Fact]
    public Task SinglePointBrushStrokeCanBeSelectedAndErased()=>OnSta(()=>
    {
        var page=DrawingStyleTests.ImagePage(); var canvas=new AnnotationCanvas {Page=page,Tool=EditorTool.Brush};
        canvas.BeginGesture(new(100,100)); canvas.CommitGesture();
        var dot=Assert.Single(page.Edits.Annotations); Assert.Single(dot.Points);
        canvas.Tool=EditorTool.Select; canvas.BeginGesture(new(100,100));
        Assert.Equal(dot.Id,canvas.SelectedAnnotation?.Id); canvas.CancelGesture();
        canvas.Tool=EditorTool.ObjectEraser; canvas.BeginGesture(new(100,100));
        Assert.Empty(page.Edits.Annotations); Assert.True(page.Edits.CanUndo);
        page.Edits.Undo(); Assert.Equal(dot,Assert.Single(page.Edits.Annotations));
    });

    [Theory]
    [InlineData(600_000,600_000,600_000,true)]
    [InlineData(600_000,300_000,600_000,true)]
    [InlineData(600_000,0,600_000,false)]
    [InlineData(600_000,100_000,600_000,false)]
    public void PlainTextPasteLimitCountsTheReplacementRatherThanTheOldSelection(int existing,int selected,int pasted,bool accepted)
        =>Assert.Equal(accepted,InlineTextEditor.FitsPlainTextPaste(new('a',existing),new('a',selected),new('b',pasted)));

    [Fact]
    public void PlainTextPasteNormalizesLineEndingsAndIgnoresTheSelectedFinalParagraphTerminator()
    {
        var current=new string('a',600_000);
        Assert.True(InlineTextEditor.FitsPlainTextPaste(current,current+"\r\n",new('b',TextLimits.MaxCharacters)));
        Assert.True(InlineTextEditor.FitsPlainTextPaste("","",string.Concat(Enumerable.Repeat("\r\n",600_000))));
        Assert.False(InlineTextEditor.FitsPlainTextPaste("kept", "",new('b',TextLimits.MaxCharacters)));
    }

    private static Task OnSta(Action action)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{try{action();completion.SetResult();}catch(Exception error){completion.SetException(error);}}){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();return completion.Task;
    }
}
