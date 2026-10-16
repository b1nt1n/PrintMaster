using PrintMaster.Controls;
using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using PrintMaster.Views;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Xunit;

namespace PrintMaster.Tests;

public sealed partial class WpfSmokeTests
{
    private static async Task VerifyInteractiveCrop(MainWindow owner,PrintSessionViewModel vm)
    {
        vm.ShowPrintPreview=false;
        var editor=(AnnotationCanvas)owner.FindName("Editor");
        for(var turn=0;turn<4;turn++)
        {
            var page=StretchTests.Scene(); page.RotationQuarterTurns=turn; page.Edits.SetCrop(new(100,40,200,120));
            vm.AddPage(page); vm.SelectedPage=page;
            await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading); owner.UpdateLayout(); editor.Focus();
            var before=page.Document.Snapshot(); var undo=vm.UndoCount; var tool=vm.Tool;
            await InteractWithModal<ImageCropDialog>(owner,()=>Assert.True(owner.HandleShortcut(Key.C,ModifierKeys.None)),async dialog=>
            {
                await dialog.InitializeAsync(); dialog.UpdateLayout(); Capture(dialog,$"pm008-crop-initial-{turn}.png");
                var crop=dialog.CropEditor; Assert.Equal(before.PrimaryContent!.Edits.Crop,dialog.ResultCrop);
                Assert.Equal(tool,vm.Tool); Assert.Equal(before,page.Document.Snapshot());
                Assert.False(owner.HandleShortcut(Key.Z,ModifierKeys.Control));
                Assert.False(owner.HandleShortcut(Key.A,ModifierKeys.Control));
                Assert.False(owner.HandleShortcut(Key.Delete,ModifierKeys.None));
                Assert.Equal(before,page.Document.Snapshot());
                var rect=crop.CropScreenBounds;
                var handles=new[] {(CropHandle.TopLeft,new Point(rect.X,rect.Y)),(CropHandle.Top,new(rect.X+rect.Width/2,rect.Y)),
                    (CropHandle.TopRight,new(rect.Right,rect.Y)),(CropHandle.Right,new(rect.Right,rect.Y+rect.Height/2)),
                    (CropHandle.BottomRight,new(rect.Right,rect.Bottom)),(CropHandle.Bottom,new(rect.X+rect.Width/2,rect.Bottom)),
                    (CropHandle.BottomLeft,new(rect.X,rect.Bottom)),(CropHandle.Left,new(rect.X,rect.Y+rect.Height/2))};
                foreach(var (handle,point) in handles) Assert.Equal(handle,crop.HitCropHandle(point));
                var originalOriented=crop.OrientedCrop;
                crop.BeginCropGesture(crop.SourcePointAt(new(rect.Right,rect.Bottom)),CropHandle.BottomRight);
                crop.UpdateGesture(crop.SourcePointAt(new(rect.Right+20,rect.Bottom+10))); crop.CommitGesture();
                Assert.True(crop.OrientedCrop.Width>originalOriented.Width); Assert.True(crop.OrientedCrop.Height>originalOriented.Height);
                Assert.Equal(before,page.Document.Snapshot()); Assert.Equal(undo,vm.UndoCount);
                var committed=dialog.ResultCrop;
                crop.BeginCropGesture(crop.SourcePointAt(new(rect.X+rect.Width/2,rect.Y+rect.Height/2)),CropHandle.Move);
                crop.UpdateGesture(crop.SourcePointAt(new(rect.X+rect.Width/2+30,rect.Y+rect.Height/2+20)));
                crop.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,0) {RoutedEvent=Mouse.LostMouseCaptureEvent});
                Assert.False(crop.IsCropDragging); Assert.Equal(committed,dialog.ResultCrop);
                var combo=Descendants<ComboBox>(dialog).Single(c=>c.Name=="CropRatios"); combo.SelectedValue=1d; dialog.UpdateLayout();
                Assert.Equal(1,crop.OrientedCrop.Width/crop.OrientedCrop.Height,8);
                var details=Descendants<Expander>(dialog).Single(); details.IsExpanded=true; dialog.UpdateLayout();
                var fields=Descendants<NumericField>(dialog).ToArray(); var width=fields.Single(f=>f.Name=="WidthInput");
                width.Input.Text="80"; Assert.True(width.Commit()); Assert.Equal(80,crop.OrientedCrop.Width,8); Assert.Equal(80,crop.OrientedCrop.Height,8);
                width.Input.Text="0"; Assert.False(width.Commit());
                Descendants<Button>(dialog).Single(b=>b.Name=="ApplyCrop").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(dialog.IsVisible); Assert.True(width.HasError); width.CancelDraft();
                dialog.Width=420; dialog.Height=500; dialog.UpdateLayout(); Capture(dialog,$"pm008-crop-compact-{turn}.png");
                Assert.True(crop.ActualWidth>250); Assert.True(crop.ActualHeight>30);
                Assert.True(Descendants<Button>(dialog).Single(b=>b.Name=="ApplyCrop").IsVisible);
                Descendants<Button>(dialog).Single(b=>b.Name=="ApplyCrop").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            Assert.Equal(Math.Min(undo+1,100),vm.UndoCount); Assert.NotEqual(before,page.Document.Snapshot());
            var applied=page.Document.Snapshot();
            await InteractWithModal<ImageCropDialog>(owner,()=>vm.SetToolCommand.Execute(EditorTool.Crop),async dialog=>
            {
                await dialog.InitializeAsync(); Assert.Equal(applied.PrimaryContent!.Edits.Crop,dialog.ResultCrop);
                Descendants<Button>(dialog).Single(b=>b.Name=="ResetCropDraft").Command.Execute(null);
                Assert.Equal(new RectD(0,0,400,200),dialog.ResultCrop);
                Assert.Equal(applied,page.Document.Snapshot());
                // Closing without Apply is the same parent-document boundary as Cancel/Escape.
            });
            Assert.Equal(applied,page.Document.Snapshot()); Assert.Equal(Math.Min(undo+1,100),vm.UndoCount);
            vm.UndoCommand.Execute(null); Assert.Equal(before,page.Document.Snapshot());
            await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading);
            var queue=(ListBox)owner.FindName("QueueList"); queue.Focus(); Assert.False(owner.HandleShortcut(Key.C,ModifierKeys.None));
        }
        var sheet=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(sheet); vm.SelectedPage=sheet;
        Assert.False(vm.CanCropSelection);
        await vm.InsertSheetImagesFromAsync([Fixtures.CreateImage("pm008-toolbar-composed.png")]);
        Assert.True(vm.CanCropSelection); var sheetBefore=sheet.Document.Snapshot();
        await InteractWithModal<ImageCropDialog>(owner,()=>vm.SetToolCommand.Execute(EditorTool.Crop),async dialog=>
        {
            await dialog.InitializeAsync(); dialog.CropEditor.BeginGesture(new(100,100)); dialog.CropEditor.UpdateGesture(new(600,500)); dialog.CropEditor.CommitGesture();
            Descendants<Button>(dialog).Single(b=>b.Name=="ApplyCrop").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        Assert.NotEqual(sheetBefore,sheet.Document.Snapshot()); vm.UndoCommand.Execute(null); Assert.Equal(sheetBefore,sheet.Document.Snapshot());
    }
}
