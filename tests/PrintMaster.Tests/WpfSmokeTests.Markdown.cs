using PrintMaster.Controls;
using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using PrintMaster.Views;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace PrintMaster.Tests;

public sealed partial class WpfSmokeTests
{
    private static Rect MarkdownClientBounds(FrameworkElement element,Window window)
        =>element.TransformToAncestor((FrameworkElement)window.Content).TransformBounds(new Rect(element.RenderSize));
    private static bool WithinMarkdownClient(FrameworkElement element,Window window)
    {
        var client=(FrameworkElement)window.Content;var bounds=MarkdownClientBounds(element,window);
        return element.IsVisible && element.ActualWidth>0 && element.ActualHeight>0 && bounds.X>=-0.5 && bounds.Y>=-0.5
            && bounds.Right<=client.ActualWidth+0.5 && bounds.Bottom<=client.ActualHeight+0.5;
    }
    private static void CaptureMarkdownClient(Window window,string name)
    {
        var root=(FrameworkElement)window.Content;root.UpdateLayout();var rect=new Rect(0,0,root.ActualWidth,root.ActualHeight);
        var visual=new DrawingVisual();
        using(var context=visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White,null,rect);
            // A VisualBrush samples local content; root.Margin's ancestor offset is not added to the target bitmap.
            context.DrawRectangle(new VisualBrush(root){Stretch=Stretch.Fill,AlignmentX=AlignmentX.Left,AlignmentY=AlignmentY.Top},null,rect);
        }
        var image=new RenderTargetBitmap((int)Math.Ceiling(rect.Width),(int)Math.Ceiling(rect.Height),96,96,PixelFormats.Pbgra32);image.Render(visual);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(Path.Combine(Fixtures.Root,name));encoder.Save(file);
    }
    private static string MarkdownEditorExample()
    {
        var source=new StringBuilder("Встроенная формула $a^2+b^2=c^2$ и русский текст.\n\n");
        var labels=new[] {"Интеграл степенной функции","Производная произведения функций","Бином Ньютона","Корни квадратного уравнения","Скобки, степени и факториалы"};
        var formulas=MarkdownLayoutTests.UserFormulas().Select(values=>(string)values[0]).ToArray();
        for(var i=0;i<formulas.Length;i++) source.Append($"### {i+1}. {labels[i]}\n\n$$\n{formulas[i]}\n$$\n\n");
        for(var i=1;i<=24;i++) source.Append($"Абзац {i:00}. Русский текст для проверки переноса на следующие страницы. Исходник должен сохраняться полностью, а формат бумаги определяет ширину строки. Дополнительное предложение помогает сравнить число страниц A4 и A5.\n\n");
        source.Append("**Конец документа — исходник сохранён.**\n");return source.ToString();
    }
    private static async Task VerifyMarkdownEditor(MainWindow window,PrintSessionViewModel vm)
    {
        vm.FinishTextEditing();vm.ShowPrintPreview=false;window.Width=1460;window.Height=940;
        var anchor=PrintPageItem.CreateBlank(SheetPaper.A4);
        anchor.Document.AddObject(new TextObject(Guid.NewGuid(),new(new(20,20,100,20)),"Страница до Markdown",new()));vm.AddPage(anchor);vm.SelectedPage=anchor;
        await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading);window.UpdateLayout();
        var initial=new PreparedMarkdown(new(Guid.NewGuid(),"# Черновик\n\nТекст $x^2$.",MarkdownStyle.Default,[],"WPF Markdown"),[]);
        var beforePages=vm.Pages.ToArray();var beforeSnapshots=beforePages.Select(p=>p.Document.Snapshot()).ToArray();var undo=vm.UndoCount;
        var model=new MarkdownEditorViewModel(initial,SheetPaper.A4,(document,_)=>Task.FromResult(new PreparedMarkdown(document,[])));
        var dialog=new MarkdownEditorDialog(model){Owner=window};var pending=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        MarkdownSourceSaveEventArgs? saved=null;dialog.SaveSourceRequested+=(_,e)=>saved=e;var layoutEvidence=new List<object>();
        dialog.Loaded+=async(_,_)=>
        {
            try
            {
                await model.RefreshAsync();await model.PreviewTask;Assert.True(model.CanApply);Assert.NotNull(model.Preview);Assert.True(model.Preview.IsFrozen);
                var source=(TextBox)dialog.FindName("SourceEditor");dialog.Activate();Assert.True(source.Focus());await Dispatcher.Yield(DispatcherPriority.Input);
                Assert.True(source.IsKeyboardFocusWithin);
                Assert.Equal(VerticalAlignment.Top,source.VerticalContentAlignment);
                foreach(var key in new[] {Key.A,Key.C,Key.V,Key.Z,Key.Delete})
                    Assert.False(window.HandleShortcut(key,key==Key.Delete ? ModifierKeys.None : ModifierKeys.Control));
                ApplicationCommands.SelectAll.Execute(null,source);Assert.Equal(source.Text.Replace("\r\n","\n"),source.SelectedText.Replace("\r\n","\n"));
                string? copied=null;
                DataObjectCopyingEventHandler copying=(_,e)=>
                {
                    copied=e.DataObject.GetData(DataFormats.UnicodeText) as string ?? e.DataObject.GetData(DataFormats.Text) as string;
                    e.CancelCommand(); // Exercise native copy text creation without changing the system clipboard.
                };
                DataObject.AddCopyingHandler(source,copying);
                try {Assert.True(ApplicationCommands.Copy.CanExecute(null,source));ApplicationCommands.Copy.Execute(null,source);}
                finally {DataObject.RemoveCopyingHandler(source,copying);}
                Assert.NotNull(copied);Assert.Equal(source.SelectedText.Replace("\r\n","\n"),copied.Replace("\r\n","\n"));
                source.Select(source.Text.Length,0);source.SelectedText="\n\nДобавление для локальной отмены";
                Assert.Contains("локальной отмены",model.Source);ApplicationCommands.Undo.Execute(null,source);Assert.DoesNotContain("локальной отмены",model.Source);
                ApplicationCommands.Redo.Execute(null,source);Assert.Contains("локальной отмены",model.Source);Assert.Equal(undo,vm.UndoCount);

                // A source edit intentionally clears the previous image. Measure the panels only after this revision has its own preview.
                await model.RefreshAsync();await model.PreviewTask;
                Assert.True(model.CanApply,model.BlockingError);Assert.NotNull(model.Preview);Assert.Equal(model.Source,model.Result!.Document.Source);

                foreach(var size in new[]{(1120d,800d),(760d,600d),(620d,500d)})
                {
                    dialog.Width=size.Item1;dialog.Height=size.Item2;dialog.UpdateLayout();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var client=(FrameworkElement)dialog.Content;var apply=(Button)dialog.FindName("ApplyMarkdown");var saveSource=(Button)dialog.FindName("SaveMarkdownSource");var preview=(Image)dialog.FindName("MarkdownPreview");
                    layoutEvidence.Add(new {RequestedWidth=size.Item1,RequestedHeight=size.Item2,WindowWidth=dialog.ActualWidth,WindowHeight=dialog.ActualHeight,
                        ClientWidth=client.ActualWidth,ClientHeight=client.ActualHeight,ClientMargin=client.Margin.ToString(),VisualOffset=VisualTreeHelper.GetOffset(client).ToString(),
                        RootArrangeSlot=LayoutInformation.GetLayoutSlot(client).ToString(),ApplyBounds=MarkdownClientBounds(apply,dialog).ToString(),ApplyArrangeSlot=LayoutInformation.GetLayoutSlot(apply).ToString(),
                        SaveBounds=MarkdownClientBounds(saveSource,dialog).ToString(),SourceBounds=MarkdownClientBounds(source,dialog).ToString(),PreviewBounds=MarkdownClientBounds(preview,dialog).ToString()});
                    File.WriteAllText(Path.Combine(Fixtures.Root,"ui-markdown-client-layout.json"),JsonSerializer.Serialize(layoutEvidence,new JsonSerializerOptions {WriteIndented=true}));
                    Assert.True(WithinMarkdownClient(apply,dialog),$"Markdown Apply leaves client at {size}: {MarkdownClientBounds(apply,dialog)} / {client.RenderSize}.");
                    Assert.True(WithinMarkdownClient(saveSource,dialog),$"Markdown Save leaves client at {size}.");
                    Assert.True(WithinMarkdownClient(source,dialog),$"Markdown source leaves client at {size}.");
                    Assert.True(WithinMarkdownClient(preview,dialog),$"Markdown preview leaves client at {size}.");
                    Assert.True(source.ActualHeight>25);Assert.True(((FrameworkElement)dialog.FindName("PreviewPanel")).ActualHeight>25);
                    CaptureMarkdownClient(dialog,$"ui-markdown-{size.Item1:0}.png");
                }
                dialog.Width=1120;dialog.Height=800;dialog.UpdateLayout();
                var broken="# Ошибка\n\n$$\\unknownPrintMasterCommand{x}$$";source.Text=broken;
                await model.RefreshAsync();Assert.False(model.CanApply);Assert.True(model.HasError);Assert.True(model.HasErrorLine);
                Assert.Equal(broken.Replace("\r\n","\n"),model.Source.Replace("\r\n","\n"));Assert.Contains("unknownPrintMasterCommand",model.Error);
                var save=(Button)dialog.FindName("SaveMarkdownSource");Assert.True(save.IsEnabled);save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.NotNull(saved);Assert.Equal(model.Source,saved.Source);Assert.Equal(model.Title,saved.Title);Assert.Null(model.Result);
                CaptureMarkdownClient(dialog,"ui-markdown-error.png");
                Assert.Equal(beforePages,vm.Pages);Assert.Equal(undo,vm.UndoCount);Assert.Equal(beforeSnapshots,vm.Pages.Select(p=>p.Document.Snapshot()));

                source.Text=MarkdownEditorExample();
                var formatting=(Expander)dialog.FindName("FormattingExpander");formatting.IsExpanded=true;dialog.UpdateLayout();
                var sizeField=(NumericField)dialog.FindName("FontSizeInput");Assert.True(sizeField.Input.Focus());
                sizeField.Input.Text="0";NumberKey(sizeField,dialog,Key.Enter);Assert.True(sizeField.HasError);Assert.Equal(12,model.FontSizePt);
                NumberKey(sizeField,dialog,Key.Escape);Assert.False(sizeField.HasError);Assert.Equal("12",sizeField.Input.Text);
                sizeField.Input.Text="10";NumberKey(sizeField,dialog,Key.Enter);Assert.False(sizeField.HasError);Assert.Equal(10,model.FontSizePt);
                var margin=(NumericField)dialog.FindName("LeftMarginInput");margin.Input.Text="12,5";NumberKey(margin,dialog,Key.Enter);Assert.Equal(12.5,model.MarginLeftMm);
                margin.Input.Text="1001";NumberKey(margin,dialog,Key.Enter);Assert.True(margin.HasError);NumberKey(margin,dialog,Key.Escape);Assert.False(margin.HasError);Assert.Equal(12.5,model.MarginLeftMm);
                formatting.IsExpanded=false;
                await model.RefreshAsync();await model.PreviewTask;Assert.True(model.CanApply,model.Error);var a4Pages=model.PageCount;
                Assert.True(a4Pages>1);Assert.All(model.Layout!.Pages,p=>Assert.True(p.Drawing.IsFrozen));
                ((ComboBox)dialog.FindName("PaperSelector")).SelectedValue="A5";
                await model.RefreshAsync();await model.PreviewTask;Assert.True(model.CanApply,model.Error);Assert.True(model.PageCount>a4Pages);
                Assert.True(model.Preview!.IsFrozen);Assert.InRange(model.Preview.PixelWidth,1,1000);Assert.InRange(model.Preview.PixelHeight,1,1000);
                var raw=source.Text;Assert.Equal(raw,model.Result!.Document.Source);
                model.NextPageCommand.Execute(null);await model.PreviewTask;Assert.Equal(1,model.SelectedPageIndex);
                Assert.Contains("2 из",model.PageLabel);model.PreviousPageCommand.Execute(null);await model.PreviewTask;Assert.Equal(0,model.SelectedPageIndex);
                Assert.Equal(beforePages,vm.Pages);Assert.Equal(undo,vm.UndoCount);Assert.Equal(beforeSnapshots,vm.Pages.Select(p=>p.Document.Snapshot()));
                Assert.True(WithinMarkdownClient((Button)dialog.FindName("ApplyMarkdown"),dialog));
                CaptureMarkdownClient(dialog,"ui-markdown.png");((Button)dialog.FindName("ApplyMarkdown")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));pending.TrySetResult(true);
            }
            catch(Exception ex) {pending.TrySetException(ex);if(dialog.IsVisible)dialog.Close();}
        };
        try {var accepted=dialog.ShowDialog()==true;await pending.Task;Assert.True(accepted);}
        finally {if(dialog.IsVisible)dialog.Close();}
        Assert.False(model.CanApply);var prepared=Assert.IsType<PreparedMarkdown>(model.Result);var paper=model.Paper;var pageCount=model.PageCount;
        Assert.Equal(beforePages,vm.Pages);Assert.Equal(beforeSnapshots,vm.Pages.Select(p=>p.Document.Snapshot()));
        await vm.ApplyMarkdownAsync(prepared,paper,null,default);var generated=vm.Pages.Skip(beforePages.Length).ToArray();
        // The shared smoke session can already have filled the bounded 100-entry history.
        Assert.Equal(pageCount,generated.Length);Assert.Equal(Math.Min(undo+1,100),vm.UndoCount);
        Assert.All(generated,p=>{var markdown=Assert.IsType<MarkdownObject>(Assert.Single(p.Document.Objects));Assert.Equal(prepared.Document.Id,markdown.DocumentId);Assert.Equal(prepared.Document.Source,markdown.Document!.Source);Assert.Equal(paper,p.Document.Paper);});
        Assert.Equal(beforeSnapshots,vm.Pages.Take(beforePages.Length).Select(p=>p.Document.Snapshot()));
        vm.UndoCommand.Execute(null);Assert.Equal(beforePages,vm.Pages);vm.RedoCommand.Execute(null);Assert.Equal(generated,vm.Pages.Skip(beforePages.Length));
        var changed=prepared with {Document=prepared.Document with {Source="# Изменённый исходник\n\n"+prepared.Document.Source}};
        var beforeEdit=vm.Pages.ToArray();var editUndo=vm.UndoCount;await vm.ApplyMarkdownAsync(changed,paper,prepared.Document.Id,default);
        var edited=vm.Pages.Skip(beforePages.Length).ToArray();Assert.Equal(Math.Min(editUndo+1,100),vm.UndoCount);
        Assert.All(edited,p=>Assert.Equal(changed.Document.Source,p.Document.Objects.OfType<MarkdownObject>().Single().Document!.Source));
        vm.UndoCommand.Execute(null);Assert.Equal(beforeEdit,vm.Pages);Assert.All(generated,p=>Assert.Equal(prepared.Document.Source,p.Document.Objects.OfType<MarkdownObject>().Single().Document!.Source));
        vm.RedoCommand.Execute(null);Assert.Equal(edited,vm.Pages.Skip(beforePages.Length));
        await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading);window.UpdateLayout();Capture(window,"ui-markdown-applied.png");
    }
}
