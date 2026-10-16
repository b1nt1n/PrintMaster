using PrintMaster.Controls;
using PrintMaster.Models;
using PrintMaster.ViewModels;
using PrintMaster.Views;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Xunit;

namespace PrintMaster.Tests;

public sealed partial class WpfSmokeTests
{
    private static NumericField Number(DependencyObject root,string name)=>Descendants<NumericField>(root)
        .Single(f=>f.IsVisible && AutomationProperties.GetName(f)==name);
    private static void NumberKey(NumericField field,Window window,Key key)=>field.Input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
        PresentationSource.FromVisual(window),0,key) { RoutedEvent=Keyboard.PreviewKeyDownEvent });

    private static async Task VerifyNumericInput(MainWindow window,PrintSessionViewModel vm)
    {
        vm.FinishTextEditing(); vm.ShowPrintPreview=false; window.Width=1460; window.Height=940;
        var inspector=(FrameworkElement)window.FindName("InspectorPane");
        if(!inspector.IsVisible) ((Button)window.FindName("InspectorToggle")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var page=PrintPageItem.CreateBlank(SheetPaper.A4);
        var text=new TextObject(Guid.NewGuid(),new(new(20,20,90,40)),"Первая строка\nВторая строка\nТретья строка",new(FontSizePt:14));
        var shape=new ShapeObject(Guid.NewGuid(),new(new(20,100,40,30)),SheetShapeKind.Rectangle,new("#175E9C",.6));
        page.Document.AddObjects([text,shape]); vm.AddPage(page); vm.SelectedPage=page; vm.SetObjectSelection([text.Id]); vm.Tool=EditorTool.Select;
        await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading); window.UpdateLayout(); await Task.Delay(35);
        var font=Number(window,"Размер выбранного текста"); Assert.True(font.Input.Focus());
        Assert.Equal(ShortcutScope.Text,window.CurrentShortcutScope); var undo=vm.UndoCount;
        var owned=string.Join("; ",window.OwnedWindows.Cast<Window>().Where(w=>w.IsVisible).Select(w=>w.Title));
        Assert.True(owned.Length==0,"Unexpected owned window during numeric input: "+owned);
        font.Input.Text="2000"; NumberKey(font,window,Key.Enter);
        var menuOpen=typeof(MainWindow).GetProperty("WorkMenuIsOpen",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)?.GetValue(window);
        Assert.True(font.HasError,$"Numeric Enter did not validate: modifiers={Keyboard.Modifiers}; enabled={font.IsEnabled}; focused={font.Input.IsKeyboardFocused}; scope={window.CurrentShortcutScope}; picking={vm.IsPickingProjectColor}; menu={menuOpen}; owned={owned}.");
        Assert.Equal("2000",font.Input.Text); Assert.Contains("1000",font.Error); Assert.Equal(14,vm.TextFontSizePt); Assert.Equal(undo,vm.UndoCount);
        font.Input.SelectAll(); Assert.Equal("2000",font.Input.SelectedText);
        Assert.False(window.HandleShortcut(Key.A,ModifierKeys.Control)); Assert.Single(vm.SelectedObjectIds);
        NumberKey(font,window,Key.Escape); Assert.Equal("14",font.Input.Text); Assert.False(font.HasError); Assert.Equal(undo,vm.UndoCount);
        font.Input.Text="18,5"; NumberKey(font,window,Key.Enter); Assert.Equal(18.5,vm.TextFontSizePt); Assert.Equal(Math.Min(undo+1,100),vm.UndoCount);
        ((SheetCanvas)window.FindName("SheetEditor")).Focus();
        vm.UndoCommand.Execute(null); Assert.Equal(14,vm.TextFontSizePt);
        vm.RedoCommand.Execute(null); Assert.Equal(18.5,vm.TextFontSizePt);
        Assert.True(font.Input.Focus()); font.Input.Text="12.5"; ((SheetCanvas)window.FindName("SheetEditor")).Focus(); Assert.Equal(12.5,vm.TextFontSizePt);
        var height=Number(window,"Высота объекта"); var previousHeight=vm.ObjectHeight;
        height.Input.Focus(); Assert.True(PrintMaster.Services.NumericInputRules.TryParse(height.Input.Text,out var exactHeight)); Assert.Equal(previousHeight,exactHeight);
        height.Input.Text="1"; NumberKey(height,window,Key.Enter);
        Assert.Equal(previousHeight,vm.ObjectHeight); Assert.Equal("1",height.Input.Text); Assert.Contains("Высоты",height.Error);
        var path=ProjectFileTests.PathFor("ux011-wpf-blocked-save"); Assert.False(await vm.SaveProjectToAsync(path)); Assert.False(File.Exists(path));
        Assert.Same(height.Input,Keyboard.FocusedElement); Assert.Equal("1",height.Input.Text);
        await vm.PrintCommand.ExecuteAsync(null); Assert.Contains("Исправьте",vm.Status); Assert.False(vm.IsBusy);
        CaptureLayout(window,"ux011-object-height-error.png"); NumberKey(height,window,Key.Escape); Assert.False(height.HasError);
        Assert.True(await vm.SaveProjectToAsync(path)); Assert.True(File.Exists(path));

        var zoom=(NumericField)window.FindName("ZoomInput"); var oldZoom=vm.Zoom; zoom.Input.Focus(); zoom.Input.Text="0"; NumberKey(zoom,window,Key.Enter);
        Assert.Equal(oldZoom,vm.Zoom); Assert.Contains("800",zoom.Error); Assert.Equal("0",zoom.Input.Text);
        NumberKey(zoom,window,Key.Escape); zoom.Input.Text="175,5%"; NumberKey(zoom,window,Key.Enter); Assert.Equal(175.5,vm.Zoom);
        vm.SetObjectSelection([shape.Id]); window.UpdateLayout(); await Task.Delay(20);
        var width=Number(window,"Ширина объекта"); width.Input.Focus(); width.Input.Text="-"; NumberKey(width,window,Key.Enter); Assert.True(width.HasError);
        vm.SetObjectSelection([text.Id]); window.UpdateLayout(); await Task.Delay(20);
        Assert.Equal("90",width.Input.Text); Assert.False(width.HasError); Assert.Equal(90,vm.ObjectWidth);

        var creation=new NewSheetDialog(new PrintSettings { PrinterName="HP LaserJet Professional P1102",Paper=PaperPreset.Custom }) { Owner=window };
        creation.Show(); creation.UpdateLayout(); await Task.Delay(30);
        var paperWidth=Number(creation,"Ширина нового листа"); paperWidth.Input.Focus(); paperWidth.Input.Text="217"; NumberKey(paperWidth,creation,Key.Enter);
        Assert.Contains("216",paperWidth.Error); Assert.Equal("165",creation.ViewModel.WidthText); Assert.Equal("217",paperWidth.Input.Text); Assert.True(creation.IsVisible);
        paperWidth.Input.Text="208,5"; NumberKey(paperWidth,creation,Key.Enter); Assert.True(creation.ViewModel.CanCreate); Assert.Equal(208.5,creation.Paper!.SizeMm.Width);
        CaptureLayout(creation,"ux011-paper-limits.png"); creation.Close();

        var notebook=new NotebookProfileDialog(NotebookProfiles.Default(NotebookVariant.GridSingle),true) { Owner=window }; notebook.Show(); notebook.UpdateLayout(); await Task.Delay(30);
        var cell=Number(notebook,"Ширина клетки"); cell.Input.Focus(); cell.Input.Text="0"; NumberKey(cell,notebook,Key.Enter);
        Assert.True(cell.HasError); Assert.Equal(5,notebook.Result!.GridWidthMm); Assert.Equal("0",cell.Input.Text);
        cell.Input.Text="4,8"; NumberKey(cell,notebook,Key.Enter); Assert.False(cell.HasError); Assert.Equal(4.8,notebook.Result!.GridWidthMm); notebook.Close();

        var reviewModel=new PrintReviewViewModel([DrawingStyleTests.ImagePage().Snapshot()],new PrintSettings { PrinterName="HP LaserJet Professional P1102",Paper=PaperPreset.Custom },vm.Printers.ToArray());
        var review=new PrintReviewDialog(reviewModel) { Owner=window }; review.Show(); review.UpdateLayout(); await Task.Delay(40);
        var reviewedWidth=Number(review,"Ширина бумаги задания"); reviewedWidth.Input.Focus(); reviewedWidth.Input.Text="217"; NumberKey(reviewedWidth,review,Key.Enter);
        ((Button)review.FindName("SubmitPrintButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Null(reviewModel.AcceptedSettings); Assert.True(review.IsVisible); Assert.Equal("217",reviewedWidth.Input.Text); Assert.Contains("216",reviewedWidth.Error);
        reviewModel.Settings.PrinterName="Microsoft Print to PDF"; Assert.True(reviewedWidth.Commit()); Assert.Equal(217,reviewModel.Settings.CustomWidthMm); Assert.False(reviewedWidth.HasError);
        review.Close();
        vm.Settings.PrinterName="Microsoft Print to PDF"; window.Activate(); ((SheetCanvas)window.FindName("SheetEditor")).Focus();
    }
}
