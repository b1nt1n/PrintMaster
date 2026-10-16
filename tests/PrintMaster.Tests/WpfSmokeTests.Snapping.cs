using PrintMaster.Controls;
using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using PrintMaster.Views;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace PrintMaster.Tests;

public sealed partial class WpfSmokeTests
{
    private static bool HasPinkGuide(BitmapSource bitmap,PointD location)
    {
        var x=(int)(location.X*SheetEditing.DipPerMm);var y=(int)(location.Y*SheetEditing.DipPerMm);
        for(var i=-4;i<=4;i++)
        {
            if(x+i<0 || x+i>=bitmap.PixelWidth || y<0 || y>=bitmap.PixelHeight) continue;
            var b=new byte[4];bitmap.CopyPixels(new Int32Rect(x+i,y,1,1),b,4,0);
            if(b[2]>200 && b[1]<180 && b[0]>100 && b[2]-b[0]>30) return true;
        }
        return false;
    }
    private static void SnapAltEvent(SheetCanvas canvas,bool down)
    {
        var e=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(canvas)!,0,Key.LeftAlt)
            {RoutedEvent=down ? Keyboard.PreviewKeyDownEvent : Keyboard.PreviewKeyUpEvent};
        canvas.RaiseEvent(e);Assert.True(e.Handled);
    }
    private static async Task VerifyPrintableSnapping(MainWindow owner,PrintSessionViewModel vm)
    {
        var page=PrintPageItem.CreateBlank(SheetPaper.A4);
        var a=new ShapeObject(Guid.NewGuid(),new(new(30,40,40,20)),SheetShapeKind.Rectangle,new("#000000",.4,1,"#000000"));
        var b=a with {Id=Guid.NewGuid(),Placement=new(new(85,40,25,20))};page.Document.AddObjects([a,b]);
        var revision=page.Document.Revision;var raster=RasterForCanvas(page);var safe=SheetPaper.A4.SafeAreaMm;
        var canvas=new SheetCanvas {HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,Width=210*SheetEditing.DipPerMm,Height=297*SheetEditing.DipPerMm,Page=page,
            Preview=raster,PreviewFrame=new(page.Id,revision,raster),SafeArea=new(safe.X*SheetEditing.DipPerMm,safe.Y*SheetEditing.DipPerMm,safe.Width*SheetEditing.DipPerMm,safe.Height*SheetEditing.DipPerMm),SelectedObjectId=a.Id,SelectedObjectIds=[a.Id,b.Id],SnappingEnabled=true};
        var host=new Window {Owner=owner,Content=canvas,Width=830,Height=750};host.Show();host.UpdateLayout();
        var start=new PointD(50,50);var delta=new PointD(safe.X-SheetEditing.VisualBounds(a).X+.5,safe.Y-SheetEditing.VisualBounds(a).Y+.5);
        var near=new PointD(start.X+delta.X,start.Y+delta.Y);
        try
        {
            canvas.BeginGesture(start);await WaitUntil(()=>canvas.MovePreviewReady);canvas.UpdateGesture(near);
            Assert.Equal(revision,page.Document.Revision);Assert.Equal(5,SheetEditing.VisualBounds(canvas.GestureDraft!).X,8);Assert.Equal(5,SheetEditing.VisualBounds(canvas.GestureDraft!).Y,8);
            Assert.NotNull(canvas.ActiveSnap!.VerticalGuide);Assert.NotNull(canvas.ActiveSnap.HorizontalGuide);
            Capture(host,"pm009-active-guides.png");
            var guideBitmap=CanvasPixels(canvas);var visualOffset=VisualTreeHelper.GetOffset(canvas);
            var pixelSamples=new List<string>();
            for(var pixelX=10;pixelX<42;pixelX++)
            {
                var pixel=new byte[4];guideBitmap.CopyPixels(new Int32Rect(pixelX,(int)(100*SheetEditing.DipPerMm),1,1),pixel,4,0);
                if(pixel[2]>170 && pixel[1]<200 && pixel[0]>80)pixelSamples.Add($"{pixelX}:{pixel[2]},{pixel[1]},{pixel[0]}");
            }
            Assert.True(HasPinkGuide(guideBitmap,new(5,100)),"Guide pixels; canvas offset="+visualOffset+"; candidates="+string.Join(" ",pixelSamples));
            var snapped=canvas.GestureDraft;
            for(var i=0;i<8;i++){canvas.UpdateGesture(near);Assert.Equal(snapped,canvas.GestureDraft);}
            SnapAltEvent(canvas,true);Assert.Null(canvas.ActiveSnap);Assert.Equal(5.5,SheetEditing.VisualBounds(canvas.GestureDraft!).X,8);
            SnapAltEvent(canvas,false);Assert.Equal(5,SheetEditing.VisualBounds(canvas.GestureDraft!).X,8);
            canvas.SnappingEnabled=false;Assert.Null(canvas.ActiveSnap);Assert.Equal(5.5,SheetEditing.VisualBounds(canvas.GestureDraft!).Y,8);
            canvas.SnappingEnabled=true;Assert.Equal(5,SheetEditing.VisualBounds(canvas.GestureDraft!).Y,8);
            var oldSafe=canvas.SafeArea;canvas.SafeArea=oldSafe with {X=6*SheetEditing.DipPerMm};Assert.Equal(6,canvas.ActiveSnap!.VerticalGuide!.Position,8);
            canvas.SafeArea=oldSafe;canvas.ViewScale=8;Assert.Null(canvas.ActiveSnap!.VerticalGuide);Assert.Equal(5.5,SheetEditing.VisualBounds(canvas.GestureDraft!).X,8);
            canvas.ViewScale=1;Assert.Equal(5,canvas.ActiveSnap!.VerticalGuide!.Position,8);
            canvas.CommitGesture();Assert.Null(canvas.ActiveSnap);Assert.True(canvas.HasCommittedPresentation);Assert.Equal(revision+1,page.Document.Revision);
            var movedA=page.Document.Objects[0];var movedB=page.Document.Objects[1];Assert.Equal(55,movedB.Placement.BoundsMm.X-movedA.Placement.BoundsMm.X,8);
            Assert.Equal(5,SheetEditing.VisualBounds(movedA).X,8);Assert.Equal(5,SheetEditing.VisualBounds(movedA).Y,8);
            var after=CanvasPixels(canvas);Assert.False(HasPinkGuide(after,new(5,100)));Assert.True(GrayAt(after,new(15,15))<30);Assert.True(GrayAt(after,new(45,50))>240);
            canvas.PreviewFrame=new(page.Id,revision,raster);Assert.True(canvas.HasCommittedPresentation);Assert.True(GrayAt(CanvasPixels(canvas),new(15,15))<30);
            canvas.PreviewFrame=new(page.Id,page.Document.Revision,RasterForCanvas(page));Assert.False(canvas.HasCommittedPresentation);Assert.False(HasPinkGuide(CanvasPixels(canvas),new(5,100)));
            page.Document.Undo();canvas.PreviewFrame=new(page.Id,page.Document.Revision,RasterForCanvas(page));Assert.Equal(new SheetObject[]{a,b},page.Document.Objects);
            canvas.SelectedObjectId=a.Id;canvas.SelectedObjectIds=[a.Id,b.Id];var before=page.Document.Snapshot();
            foreach(var zoom in new[]{.25,1d,4d,8d})
            {
                canvas.ViewScale=zoom;canvas.BeginGesture(start);var offset=4/(zoom*SheetEditing.DipPerMm);
                canvas.UpdateGesture(new(start.X+safe.X-SheetEditing.VisualBounds(a).X+offset,start.Y));Assert.NotNull(canvas.ActiveSnap!.VerticalGuide);
                offset=8/(zoom*SheetEditing.DipPerMm);canvas.UpdateGesture(new(start.X+safe.X-SheetEditing.VisualBounds(a).X+offset,start.Y));Assert.NotNull(canvas.ActiveSnap!.VerticalGuide);
                offset=12/(zoom*SheetEditing.DipPerMm);canvas.UpdateGesture(new(start.X+safe.X-SheetEditing.VisualBounds(a).X+offset,start.Y));Assert.Null(canvas.ActiveSnap!.VerticalGuide);
                canvas.CancelGesture();Assert.Null(canvas.ActiveSnap);Assert.Equal(before,page.Document.Snapshot());
            }
            canvas.ViewScale=1;canvas.BeginGesture(start);canvas.UpdateGesture(near);
            canvas.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,0){RoutedEvent=Mouse.LostMouseCaptureEvent});Assert.Null(canvas.ActiveSnap);Assert.False(canvas.IsGestureActive);Assert.Equal(before,page.Document.Snapshot());
            canvas.BeginGesture(start);canvas.UpdateGesture(near);canvas.Tool=EditorTool.Rectangle;Assert.Null(canvas.ActiveSnap);canvas.Tool=EditorTool.Select;
            canvas.BeginGesture(start);canvas.UpdateGesture(near);canvas.IsPrintPreview=true;Assert.Null(canvas.ActiveSnap);canvas.IsPrintPreview=false;
            canvas.BeginGesture(start);canvas.UpdateGesture(near);canvas.Page=PrintPageItem.CreateBlank(SheetPaper.A4);Assert.Null(canvas.ActiveSnap);
            // Alt-click keeps the existing selection cycling and never begins a snapping drag.
            var layered=PrintPageItem.CreateBlank(SheetPaper.A4);var front=a with{Id=Guid.NewGuid()};layered.Document.AddObjects([a,front]);canvas.Page=layered;canvas.SelectedObjectId=front.Id;canvas.SelectedObjectIds=[front.Id];
            canvas.BeginGesture(start,modifiers:ModifierKeys.Alt);Assert.Equal(a.Id,canvas.SelectedObjectId);Assert.False(canvas.IsGestureActive);Assert.Null(canvas.ActiveSnap);
        }
        finally{host.Close();}
        vm.ShowPrintPreview=false;vm.SnapToPrintableArea=true;vm.AddPage(page);vm.SelectedPage=page;vm.SetObjectSelection([a.Id,b.Id]);
        await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading);owner.Width=1460;owner.Height=940;owner.UpdateLayout();
        var toggle=(ToggleButton)owner.FindName("SnapToggle");Assert.True(toggle.IsChecked);toggle.SetCurrentValue(ToggleButton.IsCheckedProperty,false);Assert.False(vm.SnapToPrintableArea);
        toggle.SetCurrentValue(ToggleButton.IsCheckedProperty,true);Assert.True(((SheetCanvas)owner.FindName("SheetEditor")).SnappingEnabled);
        var sheetEditor=(SheetCanvas)owner.FindName("SheetEditor");sheetEditor.Focus();var oldX=page.Document.Objects[0].Placement.BoundsMm.X;
        Assert.True(owner.HandleShortcut(Key.Right,ModifierKeys.None));Assert.Equal(oldX+1,page.Document.Objects[0].Placement.BoundsMm.X,8);vm.UndoCommand.Execute(null);
        await vm.InsertSheetImagesFromAsync([Fixtures.CreateImage("pm009-free-photo.png")]);owner.UpdateLayout();
        if(!((FrameworkElement)owner.FindName("InspectorPane")).IsVisible){((Button)owner.FindName("InspectorToggle")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));owner.UpdateLayout();}
        var checkbox=Descendants<CheckBox>(owner).Single(c=>AutomationProperties.GetName(c)=="Сохранять пропорции выбранного фото");Assert.True(checkbox.IsChecked);
        checkbox.SetCurrentValue(ToggleButton.IsCheckedProperty,false);Assert.False(vm.KeepPhotoAspectRatio);
        var photo=Assert.IsType<ContentObject>(vm.SelectedSheetObject);var height=photo.Placement.BoundsMm.Height;
        var widthInput=Descendants<NumericField>(owner).Single(f=>AutomationProperties.GetName(f)=="Ширина объекта");widthInput.Input.Text="40";Assert.True(widthInput.Commit());Assert.Equal(40,vm.ObjectWidth);Assert.Equal(height,vm.ObjectHeight);
        var combo=Descendants<ComboBox>(owner).Single(c=>AutomationProperties.GetName(c)=="Выравнивание по печатной области");combo.SelectedValue="bottom-right";owner.UpdateLayout();
        Descendants<Button>(owner).Single(c=>AutomationProperties.GetName(c)=="Применить выравнивание объекта").Command.Execute(null);
        var aligned=SheetEditing.VisualBounds(vm.SelectedSheetObject!);Assert.Equal(vm.SafeAreaMm.Right,aligned.Right,8);Assert.Equal(vm.SafeAreaMm.Bottom,aligned.Bottom,8);
        var menu=owner.PrepareCanvasContextMenu(sheetEditor);Assert.Contains(MenuEntry(menu,"Align").Items.OfType<MenuItem>(),i=>i.Name=="Align_top_left");
        Capture(owner,"pm009-placement-controls.png");
    }
}
