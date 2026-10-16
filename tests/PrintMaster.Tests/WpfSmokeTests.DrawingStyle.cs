using PrintMaster.Controls;
using PrintMaster.Models;
using PrintMaster.ViewModels;
using PrintMaster.Views;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Xunit;

namespace PrintMaster.Tests;

public sealed partial class WpfSmokeTests
{
    private static async Task VerifyDrawingInspectorInCompactWindow(MainWindow window,PrintSessionViewModel vm)
    {
        vm.FinishTextEditing(); vm.ShowPrintPreview=false; vm.Tool=EditorTool.Select;
        var page=PrintPageItem.CreateBlank(SheetPaper.A4);
        var shape=new ShapeObject(Guid.NewGuid(),new(new(25,35,40,20)),SheetShapeKind.Rectangle,new("#175E9C",0.6));
        var marker=new InkObject(Guid.NewGuid(),new(new(85,55,30,10)),[new(0,0),new(1,1)],new("#C62828",2,0.3));
        page.Document.AddObjects([shape,marker]); vm.AddPage(page); vm.SelectedPage=page; vm.SetObjectSelection([shape.Id,marker.Id]);
        foreach(var size in new[] {(1460d,940d),(780d,560d),(720d,480d)})
        {
            window.Width=size.Item1; window.Height=size.Item2; window.UpdateLayout(); await Task.Delay(40);
            var inspector=(FrameworkElement)window.FindName("InspectorPane");
            if(!inspector.IsVisible) ((Button)window.FindName("InspectorToggle")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            var panel=Descendants<DrawingStylePanel>((DependencyObject)window.Content).Single(p=>p.IsVisible);
            var color=(TextColorEditor)panel.FindName("SelectedColorEditor"); var hex=(TextBox)color.FindName("HexInput");
            var width=((NumericField)panel.FindName("WidthInput")).Input; var palette=(Button)color.FindName("PaletteButton");
            Assert.True(InsideBounds(hex,inspector),$"HEX clipped at {size}.");
            Assert.True(InsideBounds(width,inspector),$"Width field clipped at {size}.");
            Assert.True(InsideBounds(palette,inspector),$"Palette button clipped at {size}.");
            Assert.True(width.IsEnabled); Assert.True(color.IsMixed); Assert.Empty(hex.Text); Assert.Empty(width.Text);
            CaptureLayout(window,$"ux008-inspector-{size.Item1:0}-{size.Item2:0}.png");
        }
        var visiblePanel=Descendants<DrawingStylePanel>((DependencyObject)window.Content).Single(p=>p.IsVisible);
        var colorEditor=(TextColorEditor)visiblePanel.FindName("SelectedColorEditor"); var widthField=(NumericField)visiblePanel.FindName("WidthInput"); var input=widthField.Input;
        var before=vm.UndoCount; input.Focus(); input.Text="1,25";
        input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),0,Key.Enter) { RoutedEvent=Keyboard.PreviewKeyDownEvent });
        Assert.All(page.Document.Objects,o=>Assert.Equal(1.25,DrawingStyleTests.StyleOf(o).WidthMm)); Assert.Equal(Math.Min(before+1,100),vm.UndoCount);
        var hexInput=(TextBox)colorEditor.FindName("HexInput"); hexInput.Focus(); hexInput.Text="3f799b";
        hexInput.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),0,Key.Enter) { RoutedEvent=Keyboard.PreviewKeyDownEvent });
        Assert.All(page.Document.Objects,o=>Assert.Equal("#3F799B",DrawingStyleTests.StyleOf(o).Color));
        Assert.Equal(0.3,page.Document.Objects.OfType<InkObject>().Single().Style.Opacity); Assert.Equal(Math.Min(before+2,100),vm.UndoCount);
        input.Focus(); input.Text="bad";
        input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),0,Key.Enter) { RoutedEvent=Keyboard.PreviewKeyDownEvent });
        Assert.True(widthField.HasError); Assert.Equal(Math.Min(before+2,100),vm.UndoCount);
        input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),0,Key.Escape) { RoutedEvent=Keyboard.PreviewKeyDownEvent });
        Assert.False(widthField.HasError); Assert.Equal("1.25",input.Text.Replace(',','.'));
        vm.ShowPrintPreview=true; window.UpdateLayout();
        Assert.False(input.IsVisible); Assert.False(hexInput.IsVisible); Assert.False(input.IsEnabled);
        vm.ShowPrintPreview=false; window.Width=1460; window.Height=940; window.UpdateLayout();
    }
}
