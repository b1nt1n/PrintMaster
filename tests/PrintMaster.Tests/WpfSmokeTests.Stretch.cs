using PrintMaster.Controls;
using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using PrintMaster.Views;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace PrintMaster.Tests;

public sealed partial class WpfSmokeTests
{
    private static async Task VerifyStretchMode(MainWindow owner,PrintSessionViewModel vm)
    {
        vm.ShowPrintPreview=false; var page=StretchTests.Scene(); vm.AddPage(page); vm.SelectedPage=page;
        vm.Settings.Scaling=ScaleMode.Stretch;
        await WaitUntil(()=>!vm.IsPreviewLoading && vm.EditorPreview!=null); owner.UpdateLayout();
        var editor=(AnnotationCanvas)owner.FindName("Editor"); Assert.Equal(ScaleMode.Stretch,editor.Scaling);
        Assert.Contains("пропорции",vm.CanvasHint); Assert.False(vm.HasFillWarning);
        var visual=new RenderTargetBitmap((int)Math.Ceiling(editor.ActualWidth),(int)Math.Ceiling(editor.ActualHeight),96,96,PixelFormats.Pbgra32);
        visual.Render(editor); var bytes=new byte[visual.PixelWidth*visual.PixelHeight*4]; visual.CopyPixels(bytes,visual.PixelWidth*4,0);
        var safe=editor.SafeArea;
        var expected=new[] {Colors.Red,Colors.Lime,Colors.Blue,Colors.Gold};
        var points=new[]{(.25,.25),(.75,.25),(.25,.75),(.75,.75)};
        for(var i=0;i<4;i++)
        {
            var x=(int)(safe.X+safe.Width*points[i].Item1); var y=(int)(safe.Y+safe.Height*points[i].Item2); var offset=(y*visual.PixelWidth+x)*4;
            Assert.Equal(expected[i].B,bytes[offset]); Assert.Equal(expected[i].G,bytes[offset+1]); Assert.Equal(expected[i].R,bytes[offset+2]);
        }
        CaptureLayout(owner,"pm007-editor-stretch.png");
        using(var review=new PrintReviewViewModel([page.Snapshot()],new() { PrinterName="Microsoft Print to PDF",Scaling=ScaleMode.Stretch },["Microsoft Print to PDF"]))
        {
            var dialog=new PrintReviewDialog(review) { Owner=owner }; dialog.Show();
            try
            {
                await WaitUntil(()=>!review.IsLoading); dialog.UpdateLayout();
                var combo=Descendants<ComboBox>(dialog).Single(c=>AutomationProperties.GetName(c)=="Масштабирование исходных страниц");
                Assert.Equal(ScaleMode.Stretch,combo.SelectedValue); Assert.False(review.AllowCropping); Assert.Contains("пропорции",review.ScalingHint);
                CaptureLayout(dialog,"pm007-print-stretch.png");
            }
            finally { dialog.Close(); }
        }
        using(var export=new ExportViewModel([new(page.Snapshot(),true,true,true)],SheetPaper.A4,null,MonochromeMode.Auto,160,ScaleMode.Stretch,new TestDialogs(),"stretch"))
        {
            export.OutputPath=Path.Combine(Fixtures.Root,"pm007-wpf.png"); var dialog=new ExportDialog(export) { Owner=owner }; dialog.Show();
            try
            {
                await WaitUntil(()=>!export.IsLoading); dialog.UpdateLayout();
                var combo=Descendants<ComboBox>(dialog).Single(c=>AutomationProperties.GetName(c)=="Размещение при экспорте");
                Assert.Equal(ScaleMode.Stretch,combo.SelectedValue); Assert.Contains("пропорции",export.ScalingHint); Assert.DoesNotContain("обрезано",export.Warning);
                CaptureLayout(dialog,"pm007-export-stretch.png");
            }
            finally { dialog.Close(); }
        }
        vm.Settings.Scaling=ScaleMode.Fit;
    }
}
