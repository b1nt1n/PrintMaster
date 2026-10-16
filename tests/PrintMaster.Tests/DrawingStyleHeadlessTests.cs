using PrintMaster.Controls;
using PrintMaster.Models;
using PrintMaster.ViewModels;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Xunit;

namespace PrintMaster.Tests;

// WPF permits one Application per process. Run this opt-in fact separately from the
// visible WpfSmokeTests. It never starts App.Run, shows a Window or opens a native picker.
public sealed class HeadlessStyleFactAttribute : FactAttribute
{
    public HeadlessStyleFactAttribute()
    { if(Environment.GetEnvironmentVariable("PRINTMASTER_HEADLESS_STYLE")!="1") Skip="Run separately with PRINTMASTER_HEADLESS_STYLE=1, Category=HeadlessUI."; }
}

public sealed class DrawingStyleHeadlessTests
{
    [HeadlessStyleFact] [Trait("Category","HeadlessUI")]
    public Task InspectorBindsLiveValuesMixedColorHexAndWidthWithoutShowingAnyWindow()=>OnSta(async ()=>
    {
        var app=TestApplication.Create();
        var errors=new StringBuilder(); using var listener=new TextWriterTraceListener(new StringWriter(errors));
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        using var vm=DrawingStyleTests.Session();
        try
        {
            var page=PrintPageItem.CreateBlank(SheetPaper.A4);
            var rectangle=new ShapeObject(Guid.NewGuid(),new(new(20,30,40,20)),SheetShapeKind.Rectangle,new("#175E9C",0.6));
            var ink=new InkObject(Guid.NewGuid(),new(new(80,50,30,15)),[new(0,0),new(1,1)],new("#C62828",2,0.3));
            page.Document.AddObjects([rectangle,ink]); vm.AddPage(page); vm.SetObjectSelection([rectangle.Id]);
            var panel=new DrawingStylePanel { DataContext=vm };
            var sheetTools=new SheetToolsPanel { DataContext=vm };
            var color=(TextColorEditor)panel.FindName("SelectedColorEditor");
            var hex=(TextBox)color.FindName("HexInput"); var width=((NumericField)panel.FindName("WidthInput")).Input;
            async Task Layout()
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                panel.Measure(new(220,900)); panel.Arrange(new(0,0,220,panel.DesiredSize.Height)); panel.UpdateLayout();
                sheetTools.Measure(new(240,1500)); sheetTools.Arrange(new(0,0,240,sheetTools.DesiredSize.Height)); sheetTools.UpdateLayout();
            }
            void Blur(TextBox input)=>input.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice,0,input,null) { RoutedEvent=Keyboard.LostKeyboardFocusEvent });
            await Layout(); Assert.Equal("#175E9C",hex.Text); Assert.Equal("0.6",width.Text.Replace(',','.')); Assert.True(width.IsEnabled);
            var count=vm.UndoCount; width.Text="1,25"; Blur(width); await Layout();
            Assert.Equal(1.25,DrawingStyleTests.StyleOf(page.Document.Objects[0]).WidthMm); Assert.Equal(count+1,vm.UndoCount);
            vm.SetObjectSelection([rectangle.Id,ink.Id]); await Layout();
            Assert.True(color.IsMixed); Assert.Empty(hex.Text); Assert.Empty(width.Text);
            count=vm.UndoCount; Blur(hex); Blur(width); Assert.Equal(count,vm.UndoCount);
            // Applying the representative color must still update the differently colored member.
            hex.Text="#175e9c"; Blur(hex); await Layout();
            Assert.False(color.IsMixed); Assert.Equal("#175E9C",hex.Text); Assert.Equal(count+1,vm.UndoCount);
            Assert.Equal("#175E9C",DrawingStyleTests.StyleOf(page.Document.Objects[1]).Color);
            hex.Text="ZZFFFF"; Blur(hex); Assert.Equal(count+1,vm.UndoCount);
            vm.UndoCommand.Execute(null); await Layout(); Assert.True(color.IsMixed); Assert.Empty(hex.Text);
            vm.SetObjectSelection([ink.Id]); await Layout(); Assert.Equal("#C62828",hex.Text); Assert.Equal("2",width.Text);
            vm.IsBusy=true; await Layout(); Assert.False(width.IsEnabled); vm.IsBusy=false;
            var image=DrawingStyleTests.ImagePage(); var annotation=Annotation.Create(AnnotationKind.Arrow,new(50,50),new(250,250),"#B94712",12.5);
            image.Edits.Add(annotation); vm.AddPage(image); vm.SelectedPage=image; vm.SelectedSourceAnnotation=annotation; await Layout();
            Assert.Equal("#B94712",hex.Text); Assert.Equal("12.5",width.Text.Replace(',','.'));
            vm.SelectedDrawingColor="#33AA77"; await Layout(); Assert.Equal("#33AA77",hex.Text);
            vm.UndoCommand.Execute(null); await Layout(); Assert.Equal("#B94712",hex.Text);
            Assert.Empty(errors.ToString()); Assert.Empty(app.Windows.Cast<Window>());
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); app.Shutdown(); }
    });

    private static Task OnSta(Func<Task> body)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async ()=>
            {
                try { await body(); completion.SetResult(); }
                catch(Exception ex) { completion.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground=true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
}
