using PrintMaster.Controls;
using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using PrintMaster.Views;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Xunit;

namespace PrintMaster.Tests;

public sealed partial class WpfSmokeTests
{
    private static TextColorEditor VisibleColor(MainWindow window,string label)=>Descendants<TextColorEditor>(window).Single(c=>c.IsVisible && c.ColorLabel==label);
    private static void ArmPicker(TextColorEditor editor)=>Descendants<Button>(editor).Single(b=>b.Name=="PipetteButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task WaitColorReady(PrintSessionViewModel vm)=>await WaitUntil(()=>vm.CanPickProjectColor);
    private static async Task VerifyColorPickingAndAppearance(MainWindow window,PrintSessionViewModel vm)
    {
        vm.ShowPrintPreview=false;window.Width=1460;window.Height=940;
        var source=StretchTests.Scene();var asset=source.Document.PrimaryAsset!;var sheet=PrintPageItem.CreateBlank(SheetPaper.A4);
        var photo=new ContentObject(Guid.NewGuid(),new(new(20,20,80,40)),asset.Id,ContentEdits.Empty(asset.LogicalSize));sheet.Document.AddContent(asset,photo);
        var shape=new ShapeObject(Guid.NewGuid(),new(new(130,80,40,30)),SheetShapeKind.Rectangle,new("#112233",.5));sheet.Document.AddObject(shape);
        vm.AddPage(sheet);vm.SelectedPage=sheet;vm.SetObjectSelection([shape.Id]);await WaitColorReady(vm);window.UpdateLayout();
        if(!((FrameworkElement)window.FindName("InspectorPane")).IsVisible){((Button)window.FindName("InspectorToggle")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();}
        var canvas=(SheetCanvas)window.FindName("SheetEditor");var color=VisibleColor(window,"Цвет выделения");Assert.True(color.CanPickColor);
        var undo=vm.UndoCount;ArmPicker(color);Assert.True(vm.IsPickingProjectColor);Assert.True(canvas.IsColorPicking);
        var before=sheet.Document.Snapshot();canvas.Focus();Assert.True(window.HandleShortcut(Key.Delete,ModifierKeys.None));Assert.False(ApplicationCommands.Delete.CanExecute(null,canvas));Assert.Equal(before,sheet.Document.Snapshot());
        Assert.True(canvas.RequestColorSampleAt(new(80*SheetEditing.DipPerMm,30*SheetEditing.DipPerMm)));
        await window.ColorSamplingTask!;Assert.Equal("#00FF00",vm.SelectedDrawingColor);Assert.False(vm.IsPickingProjectColor);Assert.Equal(Math.Min(undo+1,100),vm.UndoCount);
        Assert.Equal(shape.Placement,vm.SelectedSheetObject!.Placement);vm.UndoCommand.Execute(null);Assert.Equal(before,sheet.Document.Snapshot());await WaitColorReady(vm);
        undo=vm.UndoCount;ArmPicker(color);Assert.True(window.HandleShortcut(Key.Escape,ModifierKeys.None));Assert.False(vm.IsPickingProjectColor);Assert.Equal(undo,vm.UndoCount);Assert.Equal(before,sheet.Document.Snapshot());
        // The scene worker deliberately ignores cancellation to prove stale completions cannot recolor a new context.
        var realSampler=window.SampleProjectColor;using(var gate=new ManualResetEventSlim(false))
        {
            var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.SampleProjectColor=(_,_,_,_)=>{started.TrySetResult();if(!gate.Wait(TimeSpan.FromSeconds(10)))throw new TimeoutException("QA color gate");return "#ABCDEF";};
            ArmPicker(color);canvas.RequestColorSampleAt(new(80*SheetEditing.DipPerMm,30*SheetEditing.DipPerMm));await started.Task;
            var pending=window.ColorSamplingTask!;var other=PrintPageItem.CreateBlank(new(new(148,210),false,SheetMargins.SafeDefault));
            vm.AddPage(other);vm.SelectedPage=other;Assert.False(vm.IsPickingProjectColor);gate.Set();await pending;
            Assert.Equal(before,sheet.Document.Snapshot());Assert.Empty(other.Document.Objects);window.SampleProjectColor=realSampler;
        }
        vm.SelectedPage=sheet;vm.SetObjectSelection([shape.Id]);await WaitColorReady(vm);window.UpdateLayout();
        var panel=Descendants<DrawingStylePanel>(window).Single(p=>p.IsVisible);
        var fill=Descendants<CheckBox>(panel).Single(c=>c.Name=="FillToggle");fill.SetCurrentValue(ToggleButton.IsCheckedProperty,true);Assert.True(vm.HasSelectedDrawingFill);
        await WaitColorReady(vm);window.UpdateLayout();var fillColor=VisibleColor(window,"Цвет заливки");ArmPicker(fillColor);
        canvas.RequestColorSampleAt(new(40*SheetEditing.DipPerMm,50*SheetEditing.DipPerMm));await window.ColorSamplingTask!;
        Assert.Equal("#0000FF",vm.SelectedDrawingFillColor);await WaitColorReady(vm);window.UpdateLayout();
        var opacity=Descendants<NumericField>(panel).Single(f=>f.Name=="OpacityInput");opacity.Input.Text="50";Assert.True(opacity.Commit());Assert.Equal(.5,((ShapeObject)vm.SelectedSheetObject!).Style.Opacity);
        opacity.Input.Text="101";Assert.False(opacity.Commit());opacity.CancelDraft();await WaitColorReady(vm);window.UpdateLayout();Capture(window,"pm010-sheet-fill-opacity.png");
        vm.ShowPrintPreview=true;await WaitUntil(()=>!vm.IsPreviewLoading);Assert.False(vm.CanPickProjectColor);Assert.False(vm.IsPickingProjectColor);vm.ShowPrintPreview=false;await WaitColorReady(vm);
        // Text uses the same color target instead of changing the drawing default.
        var text=new TextObject(Guid.NewGuid(),new(new(130,140,50,25)),"Цвет текста",new());sheet.Document.AddObject(text);vm.SetObjectSelection([text.Id]);await WaitColorReady(vm);window.UpdateLayout();
        var ink=vm.InkColor;var textColor=VisibleColor(window,"Цвет текста");ArmPicker(textColor);canvas.RequestColorSampleAt(new(80*SheetEditing.DipPerMm,50*SheetEditing.DipPerMm));await window.ColorSamplingTask!;
        Assert.Equal("#FFD700",vm.TextColor);Assert.Equal(ink,vm.InkColor);
        await WaitColorReady(vm);if(((FrameworkElement)window.FindName("InspectorPane")).IsVisible){((Button)window.FindName("InspectorToggle")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();}
        canvas.Focus();Assert.True(window.HandleShortcut(Key.I,ModifierKeys.None));canvas.RequestColorSampleAt(new(80*SheetEditing.DipPerMm,30*SheetEditing.DipPerMm));await window.ColorSamplingTask!;
        Assert.Equal("#00FF00",vm.TextColor);Assert.Equal(ink,vm.InkColor);
        ((Button)window.FindName("InspectorToggle")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();
        // Source points are inverse-mapped through the displayed crop and all four rotations/scaling modes.
        source.Edits.SetCrop(new(100,0,200,200));vm.AddPage(source);vm.SelectedPage=source;vm.Tool=EditorTool.Brush;
        var sourceCanvas=(AnnotationCanvas)window.FindName("Editor");
        foreach(var turn in Enumerable.Range(0,4))foreach(var scaling in new[]{ScaleMode.Fit,ScaleMode.Fill,ScaleMode.Stretch})
        {
            source.RotationQuarterTurns=turn;vm.Settings.Scaling=scaling;await WaitColorReady(vm);vm.InkColor="#112233";window.UpdateLayout();Capture(window,$"pm010-source-{turn}-{scaling}.png");
            var defaults=VisibleColor(window,"Цвет нового штриха");var crop=source.Edits.Crop;
            var placement=PageLayout.Place(new SizeD(crop.Width,crop.Height).Rotated(turn),vm.CanvasSafeArea,scaling);
            var point=ImageGeometry.CreateTransform(crop,turn,placement).Transform(new(250,50));
            ArmPicker(defaults);Assert.True(sourceCanvas.RequestColorSampleAt(new(point.X,point.Y)));await window.ColorSamplingTask!;
            Assert.Equal("#00FF00",vm.InkColor);Assert.Equal(crop,source.Edits.Crop);Assert.Empty(source.Edits.Annotations);
        }
        vm.Settings.Scaling=ScaleMode.Fit;vm.Tool=EditorTool.Brush;await WaitColorReady(vm);window.UpdateLayout();Capture(window,"pm010-source-final.png");
        var sourceDefaults=VisibleColor(window,"Цвет нового штриха");ArmPicker(sourceDefaults);Assert.True(sourceCanvas.RequestColorSampleAt(new(2,2)));await window.ColorSamplingTask!;Assert.Equal("#FFFFFF",vm.InkColor);
        var annotation=Annotation.Create(AnnotationKind.Rectangle,new(130,30),new(220,130),"#112233",4);source.Edits.Add(annotation);vm.Tool=EditorTool.Select;
        sourceCanvas.BeginGesture(new(160,80));sourceCanvas.CommitGesture();Assert.Equal(annotation.Id,vm.SelectedSourceAnnotation!.Id);
        await WaitColorReady(vm);window.UpdateLayout();var sourcePanel=Descendants<DrawingStylePanel>(window).Single(p=>p.IsVisible);
        Descendants<CheckBox>(sourcePanel).Single(c=>c.Name=="FillToggle").SetCurrentValue(ToggleButton.IsCheckedProperty,true);
        vm.SelectedDrawingFillColor="#FF0000";var sourceOpacity=Descendants<NumericField>(sourcePanel).Single(f=>f.Name=="OpacityInput");sourceOpacity.Input.Text="25";Assert.True(sourceOpacity.Commit());
        Assert.Equal(.25,source.Edits.Annotations.Single().Opacity);Assert.Equal("#FF0000",source.Edits.Annotations.Single().FillColor);Capture(window,"pm010-source-appearance.png");
    }
}
