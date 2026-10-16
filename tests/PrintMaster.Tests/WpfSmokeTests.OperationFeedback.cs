using PrintMaster.Models;
using PrintMaster.ViewModels;
using PrintMaster.Views;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using Xunit;

namespace PrintMaster.Tests;

public sealed partial class WpfSmokeTests
{
    private static async Task VerifyOperationFeedback(MainWindow owner,PrintSessionViewModel vm)
    {
        var oldStatus=vm.Status;
        vm.ProgressCompleted=8; vm.ProgressTotal=8;
        vm.BeginOperation(SessionOperationKind.SaveProject,"Сохранение тестового проекта…");
        try
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); owner.UpdateLayout();
            var progress=(ProgressBar)owner.FindName("OperationProgressBar"); var print=(Button)owner.FindName("PrintButton");
            Assert.True(progress.IsIndeterminate); Assert.Equal(0,progress.Value); Assert.False(print.IsEnabled);
            Assert.True(ToolTipService.GetShowOnDisabled(print)); Assert.Contains("Дождитесь",print.ToolTip.ToString());
            Assert.Contains("Дождитесь",AutomationProperties.GetHelpText(print)); CaptureLayout(owner,"ux014-operation-waiting.png");
            vm.OperationReporter().Report(new("Подготовлено 2 из 3",2,3,"Подготовка"));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            owner.UpdateLayout(); Assert.False(progress.IsIndeterminate); Assert.Equal(3,progress.Maximum); Assert.Equal(2,progress.Value);
            var cancel=Descendants<Button>((DependencyObject)owner.Content).Single(b=>ReferenceEquals(b.Command,vm.CancelCommand));
            ((IInvokeProvider)new ButtonAutomationPeer(cancel).GetPattern(PatternInterface.Invoke)).Invoke();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(cancel.IsEnabled); Assert.Contains("предыдущего файла",vm.Status); CaptureLayout(owner,"ux014-cancel-requested.png");
        }
        finally { vm.EndOperation(); vm.Status=oldStatus; }
        var missing=PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Image,"ux014-missing.png",null,0,1,new(100,100),new(40,40)));
        using(var review=new PrintReviewViewModel([missing.Snapshot()],new(){PrinterName="Microsoft Print to PDF"},["Microsoft Print to PDF"]))
        {
            var dialog=new PrintReviewDialog(review){Owner=owner}; dialog.Show();
            try
            {
                await WaitUntil(()=>!review.IsLoading); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); dialog.UpdateLayout();
                var submit=(Button)dialog.FindName("SubmitPrintButton"); Assert.False(submit.IsEnabled);
                Assert.Contains("Отсутствуют",submit.ToolTip.ToString()); Assert.True(ToolTipService.GetShowOnDisabled(submit)); CaptureLayout(dialog,"ux014-disabled-print-confirmation.png");
            }
            finally {dialog.Close();}
        }
        var details=string.Join("\n",Enumerable.Range(0,80).Select(i=>$"Diagnostic line {i}: long trace details"));
        var error=new OperationErrorDialog(new("Проверка ошибки","Не удалось сохранить файл.","Выберите доступную папку и повторите сохранение.",details)){Owner=owner};
        Exception? failure=null;
        error.Loaded+=(_,_)=>error.Dispatcher.BeginInvoke(new Action(()=>
        {
            try
            {
                var expander=(Expander)error.FindName("OperationErrorDetailsExpander"); Assert.False(expander.IsExpanded);
                foreach(var dimensions in new[]{(600d,400d),(400d,260d)})
                {
                    error.Width=dimensions.Item1;error.Height=dimensions.Item2;error.UpdateLayout();
                    Assert.True(InsideBounds((Button)error.FindName("OperationErrorCloseButton"),(FrameworkElement)error.Content));
                    CaptureLayout(error,$"ux014-error-{dimensions.Item1:0}.png");
                }
                error.Width=600;error.Height=400;expander.IsExpanded=true;error.UpdateLayout();
                Assert.Equal(details,((TextBox)error.FindName("OperationErrorDetails")).Text); CaptureLayout(error,"ux014-error-details.png");
                ((Button)error.FindName("OperationErrorCloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                error.Close();
            }
            catch(Exception ex){failure=ex;error.Close();}
        }));
        error.ShowDialog(); Assert.Null(failure);
    }
}
