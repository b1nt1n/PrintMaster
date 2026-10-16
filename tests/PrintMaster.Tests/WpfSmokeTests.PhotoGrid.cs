using PrintMaster.Controls;
using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using PrintMaster.Views;
using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Xunit;

namespace PrintMaster.Tests;

public sealed partial class WpfSmokeTests
{
    private static async Task VerifyPhotoGridDialog(MainWindow window,PrintSessionViewModel vm)
    {
        vm.FinishTextEditing();vm.ShowPrintPreview=false;window.Width=1460;window.Height=940;
        var photo=StretchTests.Scene();vm.AddPage(photo);vm.SelectedPage=photo;
        await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading);window.UpdateLayout();
        Assert.True(vm.CanArrangePhotoGrid);Assert.True(((Button)window.FindName("PhotoGridButton")).IsEnabled);
        var menu=window.PrepareCanvasContextMenu((AnnotationCanvas)window.FindName("Editor"));Assert.Contains(menu.Items.OfType<MenuItem>(),i=>i.Name=="PhotoGrid");
        var before=photo.Document.Snapshot();var request=vm.CapturePhotoGridRequest(photo);
        var pending=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model=new PhotoGridViewModel(request);var dialog=new PhotoGridDialog(model){Owner=window};
        dialog.Loaded+=async(_,_)=>
        {
            try
            {
                await model.PreviewTask;Assert.NotNull(model.Preview);Assert.True(model.Preview.IsFrozen);
                Assert.False(window.HandleShortcut(System.Windows.Input.Key.Down,System.Windows.Input.ModifierKeys.None));Assert.Same(before,photo.Document.Snapshot());
                foreach(var size in new[]{(900d,740d),(720d,550d),(480d,440d)})
                {
                    dialog.Width=size.Item1;dialog.Height=size.Item2;dialog.UpdateLayout();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.True(InsideBounds((Button)dialog.FindName("ApplyGrid"),dialog),$"Apply clipped at {size}.");
                    Assert.True(InsideBounds(((NumericField)dialog.FindName("RowsInput")).Input,dialog),$"Rows clipped at {size}.");
                }
                var rows=(NumericField)dialog.FindName("RowsInput");rows.Input.Text="0";
                ((Button)dialog.FindName("ApplyGrid")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.True(rows.HasError);Assert.True(dialog.IsVisible);
                rows.CancelDraft();Assert.Same(before,photo.Document.Snapshot());
                ((Button)dialog.FindName("CancelGrid")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if(dialog.IsVisible)dialog.Close();pending.TrySetResult(true);
            }
            catch(Exception ex){pending.TrySetException(ex);dialog.Close();}
        };
        Assert.False(dialog.ShowDialog()==true);await pending.Task;Assert.Same(before,photo.Document.Snapshot());Assert.False(model.CanApply);
        model=new PhotoGridViewModel(request);dialog=new PhotoGridDialog(model){Owner=window};pending=new(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Loaded+=async(_,_)=>
        {
            try
            {
                model.SelectedPreset="2x3";model.Scaling=ScaleMode.Fill;await model.PreviewTask;
                Assert.Equal(6,model.Plan!.Photos.Length);Assert.True(model.ShowFillNotice);Assert.True(model.CanApply);
                Capture(dialog,"ui-photo-grid.png");((Button)dialog.FindName("ApplyGrid")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));pending.TrySetResult(true);
            }
            catch(Exception ex){pending.TrySetException(ex);dialog.Close();}
        };
        Assert.True(dialog.ShowDialog()==true);await pending.Task;Assert.Same(before,photo.Document.Snapshot());
        Assert.True(vm.ApplyPhotoGrid(photo,before,request,model.Options));Assert.True(photo.IsSheetLayout);Assert.Equal(6,photo.Document.Objects.Count);
        vm.UndoCommand.Execute(null);Assert.Same(before,photo.Document.Snapshot());vm.RedoCommand.Execute(null);Assert.Equal(6,photo.Document.Objects.Count);
        await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading);Capture(window,"ui-photo-grid-applied.png");
    }
}
