using PrintMaster.Controls;
using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using PrintMaster.Views;
using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Xunit;

namespace PrintMaster.Tests;

public sealed partial class WpfSmokeTests
{
    private static async Task VerifyPartialInkErasure(MainWindow window,PrintSessionViewModel vm)
    {
        vm.FinishTextEditing();vm.ShowPrintPreview=false;window.Width=1460;window.Height=940;
        var source=StretchTests.Scene();vm.AddPage(source);vm.SelectedPage=source;
        // The full regression can enter here with the bounded history already full.
        for(var i=0;i<104;i++)source.Copies=1+i%2;
        await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading);window.UpdateLayout();
        var editor=(AnnotationCanvas)window.FindName("Editor");
        vm.Tool=EditorTool.Brush;vm.BrushThickness=10;vm.BrushOpacity=.4;vm.InkColor="#E11D48";
        window.UpdateLayout();Assert.Equal(.4,editor.InkOpacity);Assert.Equal(10,editor.StrokeThickness);
        editor.BeginGesture(new(100,100));editor.UpdateGesture(new(300,100));editor.CommitGesture();
        var original=Assert.Single(source.Edits.Annotations);Assert.Equal(.4,original.Opacity);Assert.Equal(10,original.Thickness);
        vm.Tool=EditorTool.Marker;vm.MarkerThickness=22;vm.MarkerOpacity=.2;window.UpdateLayout();
        Assert.Equal(22,editor.StrokeThickness);Assert.Equal(.2,editor.InkOpacity);
        editor.BeginGesture(new(100,170));editor.UpdateGesture(new(300,170));editor.CommitGesture();
        Assert.Equal(.2,source.Edits.Annotations[1].Opacity);Assert.Equal(22,source.Edits.Annotations[1].Thickness);
        vm.Tool=EditorTool.Eraser;vm.EraserDiameter=40;window.UpdateLayout();
        var state=source.Document.Snapshot();var undo=vm.UndoCount;
        editor.BeginGesture(new(200,70));editor.UpdateGesture(new(200,125));Assert.Same(state,source.Document.Snapshot());
        editor.CancelGesture();Assert.Same(state,source.Document.Snapshot());Assert.Equal(undo,vm.UndoCount);
        editor.BeginGesture(new(200,70));editor.UpdateGesture(new(200,125));editor.CommitGesture();
        Assert.Equal(3,source.Edits.Annotations.Count);Assert.Equal(Math.Min(100,undo+1),vm.UndoCount);Assert.Contains("Стереть часть рисунка",vm.UndoDescription);
        var fragments=source.Edits.Annotations.Where(a=>a.Kind==AnnotationKind.Ink).ToArray();Assert.Equal(2,fragments.Length);
        Assert.Equal(new PointD(175,100),fragments[0].Points[^1]);Assert.Equal(new PointD(225,100),fragments[1].Points[0]);
        vm.UndoCommand.Execute(null);Assert.Equal(2,source.Edits.Annotations.Count);Assert.Equal(original.Points.ToArray(),source.Edits.Annotations[0].Points.ToArray());
        vm.RedoCommand.Execute(null);Assert.Equal(3,source.Edits.Annotations.Count);
        var revision=source.Document.Revision;editor.BeginGesture(new(500,400));editor.CommitGesture();Assert.Equal(revision,source.Document.Revision);
        var sheet=PrintPageItem.CreateBlank(SheetPaper.A4);
        var ink=new InkObject(Guid.NewGuid(),new(new(20,50,80,10)),[new(0,.5),new(1,.5)],new("#000000",2,.6));
        var locked=ink with {Id=Guid.NewGuid(),Locked=true};
        var text=new TextObject(Guid.NewGuid(),new(new(30,70,70,20)),"Сохранить текст",new());
        sheet.Document.AddObjects([ink,locked,text]);vm.AddPage(sheet);vm.SelectedPage=sheet;
        await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading);window.UpdateLayout();
        var canvas=(SheetCanvas)window.FindName("SheetEditor");
        vm.Tool=EditorTool.Eraser;vm.InkToolSize=10;window.UpdateLayout();Assert.Equal(10,canvas.EraserDiameterMm,8);
        state=sheet.Document.Snapshot();undo=vm.UndoCount;
        canvas.BeginGesture(new(60,30));canvas.UpdateGesture(new(60,90));Assert.Same(state,sheet.Document.Snapshot());
        canvas.CancelGesture();Assert.Same(state,sheet.Document.Snapshot());
        canvas.BeginGesture(new(60,30));canvas.UpdateGesture(new(60,90));canvas.CommitGesture();
        Assert.Equal(4,sheet.Document.Objects.Count); // Immediate save/print must see committed geometry even while preview is running.
        await canvas.ErasureCommitTask;
        Assert.Equal(4,sheet.Document.Objects.Count);Assert.Equal(Math.Min(100,undo+1),vm.UndoCount);Assert.Contains("Стереть часть рисунка",vm.UndoDescription);Assert.Equal(locked,sheet.Document.Objects[2]);Assert.Equal(text,sheet.Document.Objects[3]);
        var split=sheet.Document.Objects.OfType<InkObject>().Where(i=>!i.Locked).ToArray();Assert.Equal(2,split.Length);
        Assert.Equal(.425,split[0].Points[^1].X,8);Assert.Equal(.575,split[1].Points[0].X,8);Assert.All(split,i=>Assert.Equal(ink.Style,i.Style));
        Assert.True(canvas.HasCommittedPresentation || vm.EditorFrame?.Revision==sheet.Document.Revision);await WaitUntil(()=>!vm.IsPreviewLoading && vm.EditorFrame?.Revision==sheet.Document.Revision);
        vm.UndoCommand.Execute(null);Assert.Equal(3,sheet.Document.Objects.Count);vm.RedoCommand.Execute(null);Assert.Equal(4,sheet.Document.Objects.Count);
        vm.Tool=EditorTool.Eraser;canvas.BeginGesture(new(60,30));canvas.UpdateGesture(new(60,90));
        vm.SelectedPage=source;window.UpdateLayout();canvas.CommitGesture();Assert.Equal(4,sheet.Document.Objects.Count);
        vm.SelectedPage=sheet;vm.Tool=EditorTool.ObjectEraser;window.UpdateLayout();canvas.BeginGesture(new(40,80));Assert.DoesNotContain(sheet.Document.Objects,o=>o.Id==text.Id);vm.UndoCommand.Execute(null);
        vm.Tool=EditorTool.Brush;window.UpdateLayout();canvas.Focus();Assert.True(window.HandleShortcut(Key.M,ModifierKeys.None));Assert.Equal(EditorTool.Marker,vm.Tool);
        Assert.True(window.HandleShortcut(Key.E,ModifierKeys.None));Assert.Equal(EditorTool.Eraser,vm.Tool);
        Assert.True(window.HandleShortcut(Key.E,ModifierKeys.Shift));Assert.Equal(EditorTool.ObjectEraser,vm.Tool);
        vm.Tool=EditorTool.Brush;window.UpdateLayout();
        var panel=Descendants<InkToolsPanel>(window).Single(p=>p.IsVisible);
        var size=(NumericField)panel.FindName("InkSizeInput");size.Input.Focus();size.Input.Text="0";Assert.False(size.Commit());Assert.True(size.HasError);size.CancelDraft();
        var opacity=(NumericField)panel.FindName("InkOpacityInput");opacity.Input.Focus();opacity.Input.Text="65";Assert.True(opacity.Commit());Assert.Equal(.65,vm.BrushOpacity,8);
        Assert.False(window.HandleShortcut(Key.E,ModifierKeys.None)); // Native text input owns tool letters.
        vm.ShowPrintPreview=true;window.UpdateLayout();Assert.False(panel.IsVisible);vm.ShowPrintPreview=false;
        Capture(window,"ui-partial-eraser.png");
    }
}
