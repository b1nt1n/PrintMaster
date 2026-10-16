using PrintMaster.Controls;
using PrintMaster.Models;
using PrintMaster.ViewModels;
using PrintMaster.Views;
using System.Windows;
using Xunit;
namespace PrintMaster.Tests;
public sealed partial class WpfSmokeTests
{
    private static async Task VerifyPageOptionsDialog(MainWindow window,PrintSessionViewModel vm)
    {
        vm.ShowPrintPreview=false;var page=PrintPageItem.FromSource(new SourceAsset(Guid.NewGuid(),SourceKind.Image,"PM020",Fixtures.CreateImage("pm020-wpf.png"),0,1,new(640,480),new(100,75)));
        vm.AddPage(page);vm.SelectedPage=page;await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading);
        var model=new PageOptionsEditorViewModel([page.Snapshot()],new PrintSettings{PrinterName="HP LaserJet Professional P1102"}.Snapshot(),NotebookProfiles.Defaults);
        var dialog=new PageOptionsDialog(model){Owner=window};dialog.Show();dialog.UpdateLayout();await Task.Delay(25);
        try
        {
            model.UseOwnSettings=true;model.Settings.Paper=PaperPreset.A5;model.Settings.Monochrome=MonochromeMode.Photo;model.Settings.Dpi=300;
            Assert.True(model.CanApply);dialog.UpdateLayout();CaptureLayout(dialog,"pm020-page-options-wide.png");
            model.Settings.Monochrome=MonochromeMode.Text;dialog.UpdateLayout();
            var threshold=Number(dialog,"Порог чёрного страницы");Assert.True(threshold.Input.Focus());threshold.Input.Text="300";Assert.False(threshold.Commit());Assert.Contains("255",threshold.Error);Assert.Equal(160,model.Settings.Threshold);
            threshold.Input.Text="95";Assert.True(threshold.Commit());Assert.Equal(95,model.Settings.Threshold);
            dialog.Width=440;dialog.Height=470;dialog.UpdateLayout();CaptureLayout(dialog,"pm020-page-options-compact.png");
        }
        finally{dialog.Close();}
        var before=vm.UndoCount;vm.ApplyPageOptions([page],model.Result());Assert.Equal(Math.Min(100,before+1),vm.UndoCount);
        Assert.Equal(PaperPreset.A5,page.PrintOptions!.Paper);Assert.Equal(148d*96/25.4,vm.CanvasWidth,6);Assert.Equal(300,page.PrintOptions.Dpi);
        vm.ShowPrintPreview=true;await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading);Assert.Equal("",vm.PreviewError);
        CaptureLayout(window,"pm020-print-preview.png");vm.ShowPrintPreview=false;await WaitUntil(()=>!vm.IsPreviewLoading);
        vm.UndoCommand.Execute(null);Assert.Null(page.PrintOptions);vm.RedoCommand.Execute(null);Assert.Equal(95,page.PrintOptions!.Threshold);
        await WaitUntil(()=>!vm.IsPreviewLoading);
    }
}
