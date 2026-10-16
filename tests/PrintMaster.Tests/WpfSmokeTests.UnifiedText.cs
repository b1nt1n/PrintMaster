using PrintMaster.Controls;
using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using PrintMaster.Views;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Xunit;

namespace PrintMaster.Tests;

public sealed partial class WpfSmokeTests
{
    private static async Task VerifyUnifiedTextEditor(MainWindow window,PrintSessionViewModel vm)
    {
        window.Width=1460; window.Height=940;
        var page=UnifiedTextTests.Photo(); vm.AddPage(page); vm.SelectedPage=page; vm.ShowPrintPreview=false;
        var caption=Annotation.Create(AnnotationKind.Text,new(180,130),new(580,240),"#111827",3,"Старый текст",36);
        page.Edits.Add(caption); caption=page.Edits.Annotations.Single(); page.Edits.SetCrop(new(80,40,800,650)); page.RotationQuarterTurns=1;
        await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading); window.UpdateLayout();
        var source=(AnnotationCanvas)window.FindName("Editor"); var input=((InlineTextEditor)window.FindName("TextEditor")).TextInputBox;
        var inline=(InlineTextEditor)window.FindName("TextEditor"); vm.Tool=EditorTool.Select;
        source.BeginGesture(new(220,170),true); await Task.Delay(100); window.UpdateLayout();
        Assert.True(vm.IsEditingText); Assert.True(inline.IsVisible); Assert.False(vm.IsSheetSelected); Assert.Equal("Старый текст",vm.EditingText);
        Assert.True(input.Focus()); Assert.Equal(ShortcutScope.Text,window.CurrentShortcutScope);
        Assert.Equal(90,Assert.IsType<RotateTransform>(inline.RenderTransform).Angle,6);
        ApplicationCommands.SelectAll.Execute(null,input); Assert.Equal("Старый текст",input.Selection.Text.TrimEnd('\r','\n'));
        input.Selection.Text="  Новый текст\nВторая строка  "; Assert.Equal("  Новый текст\nВторая строка  ",vm.EditingText);
        Assert.False(window.HandleShortcut(Key.Z,ModifierKeys.Control)); ApplicationCommands.Undo.Execute(null,input); Assert.Equal("Старый текст",vm.EditingText);
        ApplicationCommands.Redo.Execute(null,input); Assert.Contains("Вторая строка",vm.EditingText);
        Assert.True(inline.HandleEditingKey(Key.B,ModifierKeys.Control)); Assert.True(vm.TextBold);
        Assert.True(inline.HandleEditingKey(Key.I,ModifierKeys.Control)); Assert.True(vm.TextItalic);
        vm.TextFontFamily="Arial"; vm.TextFontSizePt=18; vm.TextColor="#175E9C"; vm.TextAlignment=PrintMaster.Models.TextAlignment.Center; vm.TextLineSpacing=1.5;
        Assert.True(vm.ShowTextFormatting); Capture(window,"ux010-photo-inline-rotated.png");
        Assert.True(inline.HandleEditingKey(Key.Escape,ModifierKeys.None)); Assert.Equal(caption,Assert.Single(page.Edits.Annotations));
        source.BeginGesture(new(220,170),true); await Task.Delay(100); new TextRange(input.Document.ContentStart,input.Document.ContentEnd).Text="Готовый текст";
        Assert.True(inline.HandleEditingKey(Key.Enter,ModifierKeys.Control)); Assert.False(vm.IsEditingText); Assert.Equal("Готовый текст",Assert.Single(page.Edits.Annotations).Text);
        var edited=Assert.Single(page.Edits.Annotations); page.RotationQuarterTurns=0;
        page.Document.PlacePrimaryImage(SheetPaper.A4,new(20,30,160,130)); await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading); window.UpdateLayout();
        var sheet=(SheetCanvas)window.FindName("SheetEditor"); var content=page.Document.PrimaryContent!;
        var center=AnnotationTextLayout.Transform(content,content.Placement.BoundsMm).Transform(new((edited.Start.X+edited.End.X)/2,(edited.Start.Y+edited.End.Y)/2));
        sheet.BeginGesture(center,true); await Task.Delay(100); Assert.True(vm.IsEditingText); Assert.Equal("Готовый текст",vm.EditingText); vm.FinishTextEditing(false);
        vm.SetObjectSelection([content.Id]); var menu=window.PrepareCanvasContextMenu(sheet);
        var captions=menu.Items.OfType<MenuItem>().Single(i=>i.Name=="ImageCaptions");
        var captionEntry=captions.Items.OfType<MenuItem>().Single(); Assert.True(captionEntry.Command.CanExecute(null)); captionEntry.Command.Execute(null);
        Assert.True(vm.IsEditingText); vm.FinishTextEditing(false);
        var blank=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(blank); vm.SelectedPage=blank; vm.BeginTextEditing(new(20,30));
        await Task.Delay(100); Assert.True(inline.IsVisible); Assert.True(inline.HandleEditingKey(Key.B,ModifierKeys.Control));
        new TextRange(input.Document.ContentStart,input.Document.ContentEnd).Text="Обычный лист"; inline.HandleEditingKey(Key.Enter,ModifierKeys.Control);
        Assert.Equal("Обычный лист",blank.Document.Objects.OfType<TextObject>().Single().Text);
        await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading);
    }
}
