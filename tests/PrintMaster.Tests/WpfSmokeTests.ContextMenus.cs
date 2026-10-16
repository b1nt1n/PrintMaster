using PrintMaster.Controls;
using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using PrintMaster.Views;
using System.IO;
using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

using Xunit;

namespace PrintMaster.Tests;

public sealed partial class WpfSmokeTests
{
    private static MenuItem MenuEntry(ItemsControl menu,string name)=>menu.Items.OfType<MenuItem>().Single(x=>x.Name==name);
    private static void InvokeMenu(ItemsControl menu,string name)
    {
        var item=MenuEntry(menu,name); Assert.True(item.Command.CanExecute(item.CommandParameter)); item.Command.Execute(item.CommandParameter);
    }
    private static async Task InteractWithModal<T>(MainWindow owner,Action launch,Func<T,Task> interaction) where T:Window
    {
        var work=owner.Dispatcher.InvokeAsync(async ()=>
        {
            await WaitUntil(()=>owner.OwnedWindows.OfType<T>().Any());
            var dialog=owner.OwnedWindows.OfType<T>().Single();
            try { await interaction(dialog); } finally { if(dialog.IsVisible) dialog.Close(); }
        }).Task.Unwrap();
        launch(); await work;
    }
    private static void CaptureMenu(ContextMenu menu,string name)
    {
        menu.UpdateLayout(); var bitmap=new RenderTargetBitmap(Math.Max(1,(int)menu.ActualWidth),Math.Max(1,(int)menu.ActualHeight),96,96,System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(menu); var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output=File.Create(Path.Combine(Fixtures.Root,name)); encoder.Save(output);
    }
    private static async Task VerifyContextMenus(MainWindow window,PrintSessionViewModel vm,FakeClipboard clipboard)
    {
        var queue=(ListBox)window.FindName("QueueList"); var sheet=(SheetCanvas)window.FindName("SheetEditor"); var editor=(AnnotationCanvas)window.FindName("Editor");
        var pages=Enumerable.Range(0,3).Select(_=>PrintPageItem.CreateBlank(SheetPaper.A4)).ToArray(); foreach(var p in pages) vm.AddPage(p);
        var a=new ShapeObject(Guid.NewGuid(),new(new(25,35,50,30)),SheetShapeKind.Rectangle,new());
        var b=new TextObject(Guid.NewGuid(),new(new(105,95,60,30)),"Контекстное меню",new("Arial",16));
        var c=new ShapeObject(Guid.NewGuid(),new(new(150,180,30,40)),SheetShapeKind.Ellipse,new()); pages[0].Document.AddObjects([a,b,c]);
        vm.SetQueueSelection([pages[0],pages[1]],pages[0]); window.UpdateLayout(); queue.ScrollIntoView(pages[1]); await Task.Delay(50);
        var container=(ListBoxItem)queue.ItemContainerGenerator.ContainerFromItem(pages[1]);
        Assert.Same(pages[1],container.DataContext);
        var rightClick=new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Right) { RoutedEvent=Mouse.PreviewMouseDownEvent };
        container.RaiseEvent(rightClick); Assert.True(rightClick.Handled);
        Assert.Equal(new[] {pages[0],pages[1]},vm.HighlightedPages); Assert.Same(pages[1],vm.SelectedPage);
        window.SelectQueueContextTarget(pages[2]); Assert.Equal(new[] {pages[2]},vm.HighlightedPages);
        Assert.True(queue.Focus()); Assert.True(window.HandleShortcut(Key.F10,ModifierKeys.Shift));
        var menu=queue.ContextMenu!; Assert.True(menu.IsOpen); Assert.Same(queue,menu.PlacementTarget);
        var move=MenuEntry(menu,"Move"); Assert.False(MenuEntry(move,"MoveDown").IsEnabled); Assert.True(MenuEntry(move,"MoveUp").IsEnabled);
        var selected=vm.HighlightedPages.ToArray(); Assert.False(window.HandleShortcut(Key.Escape,ModifierKeys.None)); Assert.Equal(selected,vm.HighlightedPages); menu.IsOpen=false;
        vm.SetQueueSelection([pages[0],pages[1]],pages[0]); sheet.Focus(); window.PrepareQueueContextMenu(); menu.IsOpen=true; await Task.Delay(40);
        Assert.True(((RoutedCommand)MenuEntry(menu,"Copy").Command).CanExecute(null,queue),$"Queue copy: scope={window.ClipboardCommandScope(queue)}, idle={vm.IsIdle}, count={vm.HighlightedPageCount}");
        Assert.True(MenuEntry(menu,"Copy").IsEnabled,$"Menu copy disabled: open={menu.IsOpen}, scope={window.CurrentShortcutScope}, target={MenuEntry(menu,"Copy").CommandTarget}");
        CaptureMenu(menu,"ux006-queue-menu.png"); menu.IsOpen=false;
        InvokeMenu(menu,"Exclude"); Assert.False(pages[0].IsIncluded); Assert.False(pages[1].IsIncluded); Assert.True(pages[2].IsIncluded);
        vm.UndoCommand.Execute(null); Assert.True(pages[0].IsIncluded); Assert.True(pages[1].IsIncluded);
        await InteractWithModal<QueueCopiesDialog>(window,()=>InvokeMenu(window.PrepareQueueContextMenu(),"Copies"),dialog=>
        {
            var input=Descendants<TextBox>((DependencyObject)dialog.Content).Single(); var apply=Descendants<Button>((DependencyObject)dialog.Content).Single(x=>x.Name=="ApplyCopies");
            input.Text="0"; apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.True(dialog.IsVisible); Assert.Equal(1,pages[0].Copies);
            Assert.True(Descendants<TextBlock>((DependencyObject)dialog.Content).Single(x=>x.Name=="CopiesError").IsVisible);
            input.Text="4"; apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return Task.CompletedTask;
        });
        Assert.Equal(new[] {4,4,1},pages.Select(p=>p.Copies)); vm.UndoCommand.Execute(null); Assert.Equal(new[] {1,1,1},pages.Select(p=>p.Copies));
        vm.SelectedPage=pages[0]; vm.SetObjectSelection([a.Id,b.Id]); await WaitUntil(()=>!vm.IsPreviewLoading && vm.EditorPreview!=null); window.UpdateLayout();
        menu=window.PrepareCanvasContextMenu(sheet,new Point(35*SheetEditing.DipPerMm,45*SheetEditing.DipPerMm)); Assert.Equal(new[] {a.Id,b.Id},vm.SelectedObjectIds);
        menu.IsOpen=true; await Task.Delay(40); CaptureMenu(menu,"ux006-object-menu.png"); menu.IsOpen=false;
        queue.Focus(); // The popup's explicit target must win over previous keyboard focus.
        var delete=MenuEntry(menu,"Delete"); var pageCount=vm.Pages.Count; ((RoutedCommand)delete.Command).Execute(null,delete.CommandTarget);
        Assert.Equal(pageCount,vm.Pages.Count); Assert.Equal(new SheetObject[] {c},pages[0].Document.Objects); vm.UndoCommand.Execute(null);
        Assert.Equal(new SheetObject[] {a,b,c},pages[0].Document.Objects);
        vm.SetObjectSelection([a.Id,b.Id]); InvokeMenu(MenuEntry(window.PrepareCanvasContextMenu(sheet),"Order"),"OrderFront");
        Assert.Equal(new SheetObject[] {c,a,b},pages[0].Document.Objects); vm.UndoCommand.Execute(null);
        window.PrepareCanvasContextMenu(sheet,new Point(160*SheetEditing.DipPerMm,190*SheetEditing.DipPerMm)); Assert.Equal(new[] {c.Id},vm.SelectedObjectIds);
        menu=window.PrepareCanvasContextMenu(sheet,new Point(185*SheetEditing.DipPerMm,260*SheetEditing.DipPerMm)); Assert.Empty(vm.SelectedObjectIds);
        Assert.DoesNotContain(menu.Items.OfType<MenuItem>(),x=>x.Name is "Delete" or "Copy" or "Cut"); Assert.Contains(menu.Items.OfType<MenuItem>(),x=>x.Name=="AddImage");
        menu.IsOpen=true; await Task.Delay(40); CaptureMenu(menu,"ux006-empty-menu.png"); menu.IsOpen=false;
        InvokeMenu(menu,"AddText"); Assert.True(vm.IsEditingText); vm.EditingText="Добавлено через ПКМ"; vm.FinishTextEditing();
        var insertedText=pages[0].Document.Objects.OfType<TextObject>().Single(t=>t.Id!=b.Id); Assert.Equal("Добавлено через ПКМ",insertedText.Text);
        vm.UndoCommand.Execute(null); Assert.Equal(new SheetObject[] {a,b,c},pages[0].Document.Objects);
        vm.SetObjectSelection([b.Id]); InvokeMenu(window.PrepareCanvasContextMenu(sheet),"EditText"); Assert.True(vm.IsEditingText); Assert.Equal(b.Text,vm.EditingText); vm.FinishTextEditing(false);
        await vm.InsertSheetImagesFromAsync([Fixtures.CreateImage("ux006-modal-photo.png")]); var photo=Assert.IsType<ContentObject>(vm.SelectedSheetObject);
        var beforeCrop=pages[0].Document.Snapshot();
        await InteractWithModal<ImageCropDialog>(window,()=>InvokeMenu(window.PrepareCanvasContextMenu(sheet),"Crop"),async dialog=>
        {
            await dialog.InitializeAsync(); Assert.NotNull(dialog.CropEditor.Preview); window.UpdateLayout(); dialog.UpdateLayout();
            dialog.CropEditor.BeginGesture(new(120,90)); dialog.CropEditor.UpdateGesture(new(650,500)); dialog.CropEditor.CommitGesture();
            Assert.Equal(new RectD(120,90,530,410),dialog.ResultCrop); Capture(dialog,"ux006-crop-dialog.png");
            Descendants<Button>((DependencyObject)dialog.Content).Single(x=>x.Name=="ApplyCrop").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        Assert.Equal(new RectD(120,90,530,410),((ContentObject)pages[0].Document.Objects.Single(o=>o.Id==photo.Id)).Edits.Crop); vm.UndoCommand.Execute(null); Assert.Equal(beforeCrop,pages[0].Document.Snapshot());
        await InteractWithModal<ImageCropDialog>(window,()=>InvokeMenu(window.PrepareCanvasContextMenu(sheet),"Crop"),async dialog=>
        { await dialog.InitializeAsync(); dialog.CropEditor.BeginGesture(new(50,50)); dialog.CropEditor.UpdateGesture(new(700,550)); dialog.CropEditor.CommitGesture(); });
        Assert.Equal(beforeCrop,pages[0].Document.Snapshot());
        sheet.Focus(); Assert.True(window.HandleShortcut(Key.Apps,ModifierKeys.None)); Assert.True(sheet.ContextMenu!.IsOpen); sheet.ContextMenu.IsOpen=false;
        var batch=((NumericField)window.FindName("BatchCopiesInput")).Input; vm.SelectQueuePagesCommand.Execute(null); window.UpdateLayout(); batch.Focus();
        Assert.Equal(ShortcutScope.Text,window.CurrentShortcutScope); Assert.False(window.HandleShortcut(Key.F10,ModifierKeys.Shift));
        window.SelectQueueContextTarget(null); menu=window.PrepareQueueContextMenu(); Assert.Empty(vm.HighlightedPages);
        Assert.DoesNotContain(menu.Items.OfType<MenuItem>(),x=>x.Name=="Delete"); Assert.Contains(menu.Items.OfType<MenuItem>(),x=>x.Name=="AddFiles");
        await vm.ImportFilesAsync([Fixtures.CreatePdf("ux006-read-only.pdf",1)]); vm.SelectedPage=vm.Pages.Last(); Assert.True(vm.IsDocumentSelected);
        await WaitUntil(()=>!vm.IsPreviewLoading && vm.EditorPreview!=null);
        menu=window.PrepareCanvasContextMenu(editor); Assert.DoesNotContain(menu.Items.OfType<MenuItem>(),x=>x.Name is "Delete" or "Crop" or "Paste" or "AddText");
        await vm.ImportFilesAsync([Fixtures.CreateImage("ux006-source-menu.png")]); vm.SelectedPage=vm.Pages.Last();
        await WaitUntil(()=>!vm.IsPreviewLoading && vm.EditorPreview!=null); window.UpdateLayout(); editor.SelectEverything();
        menu=window.PrepareCanvasContextMenu(editor); Assert.Contains(menu.Items.OfType<MenuItem>(),x=>x.Name=="Crop"); Assert.Same(editor,MenuEntry(menu,"Delete").CommandTarget);
        vm.ShowPrintPreview=true; await WaitUntil(()=>!vm.IsPreviewLoading && vm.EditorPreview!=null);
        menu=window.PrepareCanvasContextMenu(editor); Assert.DoesNotContain(menu.Items.OfType<MenuItem>(),x=>x.Name is "Delete" or "Cut" or "Paste"); Assert.Contains(menu.Items.OfType<MenuItem>(),x=>x.Name=="EditMode");
        InvokeMenu(menu,"EditMode"); Assert.False(vm.ShowPrintPreview);
        Capture(window,"ux006-final-workspace.png");
        var demo=vm.CaptureProject(); var demoIds=pages.Select(p=>p.Id).ToHashSet();
        demo=demo with { Pages=demo.Pages.Where(p=>demoIds.Contains(p.Sheet.Id)).ToImmutableArray(),
            Editor=demo.Editor with { SelectedPageId=pages[0].Id,Zoom=55,AutoFitZoom=true,ShowPrintPreview=false } };
        await new ProjectFileService().SaveAsync(demo,Path.Combine(Fixtures.Root,"ux006-demo.printmaster"),true,default);
    }
}
