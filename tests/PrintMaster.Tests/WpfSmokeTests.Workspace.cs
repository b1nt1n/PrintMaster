using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using PrintMaster.Views;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Xunit;

namespace PrintMaster.Tests;

public sealed partial class WpfSmokeTests
{
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    private static void CaptureLayout(Window window,string name)
    {
        var root=(FrameworkElement)window.Content; root.UpdateLayout(); var dpi=VisualTreeHelper.GetDpi(window);
        var image=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(root.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);
        image.Render(root); var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output=File.Create(Path.Combine(Fixtures.Root,name)); encoder.Save(output);
    }
    private static bool InsideBounds(FrameworkElement item,FrameworkElement host)
    {
        if(!item.IsVisible || item.ActualWidth<1 || item.ActualHeight<1) return false;
        var b=item.TransformToAncestor(host).TransformBounds(new Rect(item.RenderSize));
        return b.X>=-1 && b.Y>=-1 && b.Right<=host.ActualWidth+1 && b.Bottom<=host.ActualHeight+1;
    }
    private static async Task VerifyResponsiveWorkspace(MainWindow window,PrintSessionViewModel vm)
    {
        vm.ShowPrintPreview=false; vm.SelectedPage=vm.Pages.First(p=>p.IsSheetLayout && !p.HasMissingSources);
        window.Width=1460; window.Height=940; window.UpdateLayout();
        var nativeDpi=GetDpiForWindow(new WindowInteropHelper(window).Handle); var wpfDpi=VisualTreeHelper.GetDpi(window).PixelsPerInchX;
        Assert.Equal((double)nativeDpi,wpfDpi,2);
        File.WriteAllText(Path.Combine(Fixtures.Root,"ux007-runtime-dpi.txt"),$"Native={nativeDpi}; WPF={wpfDpi}; Window={window.ActualWidth}x{window.ActualHeight}; Max={window.MaxWidth}x{window.MaxHeight}");
        var inspector=(ColumnDefinition)window.FindName("InspectorColumn"); inspector.Width=new GridLength(990); window.UpdateLayout(); await Task.Delay(40);
        var canvas=(FrameworkElement)window.FindName("CanvasPane"); CaptureLayout(window,"ux007-center-limit.png");
        Assert.True(canvas.ActualWidth>=359,$"Central pane shrank to {canvas.ActualWidth:F1} DIP.");
        Assert.True(InsideBounds((FrameworkElement)window.FindName("ZoomInput"),canvas));
        foreach(var size in new[] {(1460d,940d),(1180d,720d),(900d,600d),(780d,560d),(720d,480d)})
        {
            window.Width=size.Item1; window.Height=size.Item2; window.UpdateLayout(); await Task.Delay(40);
            var props=(FrameworkElement)window.FindName("InspectorPane");
            if(!props.IsVisible) ((Button)window.FindName("InspectorToggle")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert.True(canvas.ActualWidth>=359,$"Center {canvas.ActualWidth:F1} at {size}.");
            Assert.True(InsideBounds((FrameworkElement)window.FindName("ZoomInput"),canvas));
            Assert.True(InsideBounds((FrameworkElement)window.FindName("FitPageButton"),canvas));
            Assert.True(InsideBounds((FrameworkElement)window.FindName("PrintButton"),(FrameworkElement)window.Content));
            var menu=(Menu)window.FindName("MainMenu");
            Assert.All(menu.Items.OfType<MenuItem>(),item=>Assert.True(InsideBounds(item,(FrameworkElement)window.Content),$"Hidden header menu {item.Header} at {size}."));
            CaptureLayout(window,$"ux007-layout-{size.Item1:0}-{size.Item2:0}.png");
        }
        window.Width=900; window.Height=600; window.UpdateLayout();
        var queueCount=vm.Pages.Count;
        for(var i=0;i<40;i++) vm.AddPage(PrintPageItem.CreateBlank(SheetPaper.A4));
        vm.SetQueueSelection(vm.Pages.Skip(queueCount)); vm.BatchCopies=999; vm.SetHighlightedCopiesCommand.Execute(null);
        window.Width=720; window.Height=480; window.UpdateLayout();
        var header=(FrameworkElement)window.FindName("HeaderGrid");
        Assert.True(InsideBounds((FrameworkElement)window.FindName("PrintButton"),header));
        var path=Fixtures.CreateImage("ux007-long-caption.png");
        var asset=new SourceAsset(Guid.NewGuid(),SourceKind.Image,new string('Д',180)+".png",path,0,1,new(800,600),new(160,120));
        vm.AddPage(new PrintPageItem(SheetDocument.FromSource(asset))); vm.SelectedPage=vm.Pages.Last();
        await WaitUntil(()=>!vm.IsPreviewLoading && vm.EditorPreview!=null); window.UpdateLayout();
        Assert.True(InsideBounds((FrameworkElement)window.FindName("ZoomInput"),canvas)); CaptureLayout(window,"ux007-long-names-counts.png");
        vm.SetQueueSelection(vm.Pages.Skip(queueCount)); vm.DeleteHighlightedPagesCommand.Execute(null);
        window.Width=900; window.Height=600; window.UpdateLayout();
        var editPage=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(editPage); vm.SelectedPage=editPage;
        vm.BeginTextEditing(new(20,30)); vm.EditingText="Проверка видимости фокуса\nи кнопок ввода текста.";
        window.UpdateLayout(); await Task.Delay(80);
        var input=(PrintMaster.Controls.InlineTextEditor)window.FindName("TextEditor");
        Assert.True(input.TextInputBox.IsKeyboardFocusWithin);
        var finish=Descendants<Button>((DependencyObject)window.Content).Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="Завершить ввод текста");
        Assert.True(InsideBounds(finish,canvas)); CaptureLayout(window,"ux007-inline-focus.png"); vm.FinishTextEditing(false);
        // This case checks long error layout. A separate master case verifies a real PDF driver preview.
        using var model=new PrintReviewViewModel([PrintPageItem.CreateBlank(SheetPaper.A4).Snapshot()],new PrintSettings { PrinterName="" },["Microsoft Print to PDF"]);
        var review=new PrintReviewDialog(model) { Owner=window,Width=650,Height=470 }; review.Show();
        try
        {
            await WaitUntil(()=>!model.IsLoading);
            model.Error=string.Join(" ",Enumerable.Repeat("Драйвер не принял размер бумаги; проверьте выбранный принтер и длинное имя исходного документа.",70));
            review.UpdateLayout(); CaptureLayout(review,"ux007-long-print-error.png");
            Assert.True(InsideBounds((FrameworkElement)review.FindName("SubmitPrintButton"),(FrameworkElement)review.Content),"Long error hid the print controls.");
        }
        finally { review.Close(); }
        var settingsModel=new ApplicationSettingsViewModel(new(),["Microsoft Print to PDF",new string('П',160)],new TestDialogs());
        settingsModel.LibreOfficePath="C:/Очень длинный путь/"+new string('П',180)+"/program/soffice.com";
        settingsModel.Error=string.Join(" ",Enumerable.Repeat("Проверьте путь к LibreOffice и параметры преобразования документов.",70));
        var settings=new ApplicationSettingsDialog(settingsModel) { Owner=window,Width=450,Height=420 }; settings.Show(); settings.UpdateLayout();
        try
        {
            var save=Descendants<Button>((DependencyObject)settings.Content).Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="Сохранить настройки");
            Assert.True(InsideBounds(save,(FrameworkElement)settings.Content)); CaptureLayout(settings,"ux007-small-settings-error.png");
        }
        finally { settings.Close(); }
        var create=new NewSheetDialog(new PrintSettings { Paper=PaperPreset.Custom,CustomWidthMm=165,CustomHeightMm=205 }) { Owner=window,SizeToContent=SizeToContent.Manual,Height=360 };
        create.Show(); create.UpdateLayout();
        try
        {
            var button=Descendants<Button>((DependencyObject)create.Content).Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="Создать лист");
            Assert.True(InsideBounds(button,(FrameworkElement)create.Content)); CaptureLayout(create,"ux007-small-new-sheet.png");
        }
        finally { create.Close(); }
        window.Width=1460; window.Height=940; window.UpdateLayout();
    }
}
