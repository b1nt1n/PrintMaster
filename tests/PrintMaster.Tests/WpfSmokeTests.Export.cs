using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using PrintMaster.Views;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Xunit;

namespace PrintMaster.Tests;

public sealed partial class WpfSmokeTests
{
    private static async Task VerifySheetExport(MainWindow owner,PrintSessionViewModel vm,TestDialogs dialogs)
    {
        vm.FinishTextEditing(); var page=SheetExportTests.Scene(); vm.AddPage(page); vm.SelectedPage=page;
        var before=page.Document.Snapshot(); var target=Path.Combine(SheetExportTests.Folder("wpf"),"from-menu.pdf");
        Assert.True(vm.ExportPagesCommand.CanExecute(null));
        var entry=Descendants<MenuItem>((DependencyObject)owner.Content).FirstOrDefault(m=>Equals(m.Header,"Экспорт листов…"));
        // Menu child items are logical until their popup opens.
        entry ??=((Menu)owner.FindName("MainMenu")).Items.OfType<MenuItem>().SelectMany(m=>m.Items.OfType<MenuItem>()).Single(m=>Equals(m.Header,"Экспорт листов…"));
        Assert.Same(vm.ExportPagesCommand,entry.Command);
        dialogs.ExportReview=model=>
        {
            var dialog=new ExportDialog(model) { Owner=owner };
            dialog.Loaded+=(_,_)=>dialog.Dispatcher.BeginInvoke(async ()=>
            {
                try
                {
                    model.Format=ExportFormat.Pdf; model.OutputPath=target;
                    await WaitUntil(()=>!model.IsLoading && model.Preview!=null);
                    dialog.UpdateLayout(); Assert.True(model.CanAccept); Assert.Equal(Visibility.Visible,((Button)dialog.FindName("ExportAcceptButton")).Visibility);
                    var format=Descendants<ComboBox>(dialog).Single(c=>AutomationProperties.GetName(c)=="Формат экспорта"); Assert.Equal(ExportFormat.Pdf,format.SelectedValue);
                    foreach(var size in new[] {(960d,740d),(700d,440d)})
                    {
                        dialog.Width=size.Item1; dialog.Height=size.Item2; dialog.UpdateLayout();
                        Assert.True(InsideBounds((Button)dialog.FindName("ExportAcceptButton"),dialog)); CaptureLayout(dialog,$"ux012-export-dialog-{size.Item1:0}.png");
                    }
                    ((Button)dialog.FindName("ExportAcceptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch(Exception ex) { modalError=ex; dialog.DialogResult=false; }
            },DispatcherPriority.Background);
            return dialog.ShowDialog()==true;
        };
        try
        {
            await vm.ExportPagesCommand.ExecuteAsync(null); Assert.Null(modalError); Assert.True(File.Exists(target)); Assert.Equal(before,page.Document.Snapshot());
            Assert.Contains("Экспорт сохранен",vm.Status); Assert.False(vm.IsBusy);
        }
        finally { dialogs.ExportReview=null; modalError=null; }
        var missing=PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Image,Path.Combine(Fixtures.Root,"ux012-missing.png"),null,0,1,new(100,100),new(40,40)));
        using var invalid=new ExportViewModel([new(missing.Snapshot(),true,true,true)],SheetPaper.A4,null,MonochromeMode.Auto,160,ScaleMode.Fit,new SheetExportTests.ExportDialogs(),"missing");
        var errors=new ExportDialog(invalid) { Owner=owner }; errors.Show(); errors.UpdateLayout();
        Assert.Contains("Отсутствуют",invalid.Error); Assert.False(((Button)errors.FindName("ExportAcceptButton")).IsEnabled); Assert.Null(invalid.AcceptedPlan); errors.Close();
        ((PrintMaster.Controls.SheetCanvas)owner.FindName("SheetEditor")).Focus();
    }
    private static Exception? modalError;
}
