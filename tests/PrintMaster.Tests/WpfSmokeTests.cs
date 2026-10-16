using PrintMaster.Controls;
using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using PrintMaster.Views;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace PrintMaster.Tests;

public sealed partial class WpfSmokeTests
{
    [Fact] [Trait("Category","UI")]
    public Task RealWpfWindowLoadsBindsAndRendersAllModes()=>OnSta(async ()=>
    {
        var app=TestApplication.Create();
        var bindingErrors=new StringBuilder(); using var listener=new TextWriterTraceListener(new StringWriter(bindingErrors));
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        var clipboard=new FakeClipboard(); var dialogs=new TestDialogs(); var vm=new PrintSessionViewModel(dialogs,Path.Combine(Fixtures.Root,"ui-recovery-"+Guid.NewGuid().ToString("N")),clipboard); var window=new MainWindow(vm) { Width=1460,Height=940 };
        app.MainWindow=window; window.Show();
        Assert.Equal(SelectionMode.Extended,((ListBox)window.FindName("QueueList")).SelectionMode);
        try
        {
            await vm.InitializeAsync();
            vm.SnapToPrintableArea=false; // Legacy gesture expectations cover unsnapped positioning; PM-009 has its own checks.
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if(Environment.GetEnvironmentVariable("PRINTMASTER_PM009_ONLY")=="1") {await VerifyPrintableSnapping(window,vm);return;}
            if(Environment.GetEnvironmentVariable("PRINTMASTER_PM010_ONLY")=="1") {await VerifyColorPickingAndAppearance(window,vm);return;}
            if(Environment.GetEnvironmentVariable("PRINTMASTER_PM012_ONLY")=="1") {await VerifyPartialInkErasure(window,vm);return;}
            if(Environment.GetEnvironmentVariable("PRINTMASTER_PM013_ONLY")=="1") {await VerifyPhotoGridDialog(window,vm);return;}
            if(Environment.GetEnvironmentVariable("PRINTMASTER_PM014_ONLY")=="1") {await VerifyMarkdownEditor(window,vm);return;}
            if(Environment.GetEnvironmentVariable("PRINTMASTER_NUMERIC_ONLY")=="1") {await VerifyNumericInput(window,vm);return;}
            await VerifyNotebookPickerBindings(window);
            Assert.Single(app.Windows.Cast<Window>());
            await vm.ImportFilesAsync(new[] { Fixtures.CreateImage("ui-source.png"),Fixtures.CreatePdf(),Fixtures.CreateDocx() });
            Assert.Empty(dialogs.Errors); Assert.Equal(5,vm.Pages.Count);
            await WaitUntil(()=>vm.EditorPreview!=null); window.UpdateLayout();
            Assert.True(vm.IsImageSelected); Assert.True(vm.CanEdit); Assert.True(vm.PrintCommand.CanExecute(null));
            var selected=vm.SelectedPage!; selected.Copies=3; Assert.Equal(7,vm.Impressions);
            vm.RotateCommand.Execute(null); Assert.Equal(1,selected.RotationQuarterTurns); vm.UndoCommand.Execute(null); Assert.Equal(0,selected.RotationQuarterTurns);
            vm.RedoCommand.Execute(null); Assert.Equal(1,selected.RotationQuarterTurns); vm.UndoCommand.Execute(null); await WaitUntil(()=>vm.EditorPreview!=null);
            var editor=(AnnotationCanvas)window.FindName("Editor");
            var toolButton=Descendants(window).OfType<System.Windows.Controls.Primitives.ToggleButton>()
                .First(b=>b.CommandParameter is EditorTool t && t==EditorTool.Arrow);
            vm.Tool=EditorTool.Arrow; toolButton.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,false);
            vm.SetToolCommand.Execute(EditorTool.Arrow); Assert.True(toolButton.IsChecked);
            vm.Tool=EditorTool.Arrow; editor.BeginGesture(new(100,100)); editor.UpdateGesture(new(350,300)); editor.CommitGesture();
            Assert.Single(selected.Edits.Annotations); Assert.Equal(AnnotationKind.Arrow,selected.Edits.Annotations[0].Kind);
            vm.Tool=EditorTool.Select; editor.BeginGesture(new(200,180)); editor.UpdateGesture(new(260,220)); editor.CommitGesture();
            Assert.Equal(new PointD(160,140),selected.Edits.Annotations[0].Start);
            vm.Tool=EditorTool.ObjectEraser; editor.BeginGesture(new(285,240)); Assert.Empty(selected.Edits.Annotations);
            vm.Tool=EditorTool.Crop; vm.CropRatio=1; editor.BeginGesture(new(200,150)); editor.UpdateGesture(new(650,500)); editor.CommitGesture();
            Assert.Equal(new RectD(200,150,450,450),selected.Edits.Crop); vm.ResetCropCommand.Execute(null);
            vm.Tool=EditorTool.Brush; editor.BeginGesture(new(100,500)); editor.UpdateGesture(new(200,550)); editor.UpdateGesture(new(300,520)); editor.CommitGesture();
            Assert.Single(selected.Edits.Annotations); Assert.Equal(3,selected.Edits.Annotations[0].Points.Count); vm.UndoCommand.Execute(null);
            vm.Tool=EditorTool.Select;
            selected.Edits.Add(Annotation.Create(AnnotationKind.Arrow,new(400,240),new(760,470),"#111827",5));
            selected.Edits.Add(Annotation.Create(AnnotationKind.Text,new(120,210),new(520,290),"#111827",3,"Тестовая аннотация",36));
            selected.Edits.SetCrop(new(70,30,870,670));
            vm.RotateCommand.Execute(null); await WaitUntil(()=>vm.EditorPreview!=null); await Task.Delay(100); window.UpdateLayout();
            vm.Tool=EditorTool.Crop; vm.CropRatio=16d/9; editor.BeginGesture(new(200,100)); editor.UpdateGesture(new(500,400)); editor.CommitGesture();
            Assert.Equal(16d/9,selected.Snapshot().ContentSize.Width/selected.Snapshot().ContentSize.Height,6);
            selected.Edits.SetCrop(new(70,30,870,670));
            await WaitUntil(()=>selected.Thumbnail!=null);
            Assert.Equal(selected,editor.Page); Assert.NotNull(editor.Preview);
            Capture(window,"ui-image-editor.png");
            vm.Tool=EditorTool.Crop; window.UpdateLayout(); Capture(window,"ui-crop-mode.png"); vm.Tool=EditorTool.Select;
            var monoToggle=(CheckBox)window.FindName("PrintPreviewToggle"); monoToggle.IsChecked=true;
            await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading); window.UpdateLayout();
            Assert.True(editor.IsPrintPreview); Assert.False(vm.CanEdit); Assert.False(vm.ShowEditorTools); Assert.False(vm.UndoCommand.CanExecute(null));
            Capture(window,"ui-print-preview.png");
            var annotations=selected.Edits.Annotations.Count; editor.BeginGesture(new(30,30)); editor.UpdateGesture(new(300,300)); editor.CommitGesture(); Assert.Equal(annotations,selected.Edits.Annotations.Count);
            vm.Settings.Monochrome=MonochromeMode.Text; Assert.True(vm.HasThresholdWarning);
            await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading); Capture(window,"ui-threshold-warning.png");
            vm.Settings.Threshold=190; await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading);
            monoToggle.IsChecked=false; await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading); Assert.True(vm.CanEdit);
            vm.Settings.Monochrome=MonochromeMode.Auto;
            vm.SelectedPage=vm.Pages.First(p=>!p.IsImage); await WaitUntil(()=>vm.EditorPreview!=null); window.UpdateLayout();
            Assert.True(vm.IsDocumentSelected); Assert.False(vm.CanEdit); Assert.False(vm.SaveCopyCommand.CanExecute(null)); Capture(window,"ui-document.png");
            window.Width=1180; window.Height=720; window.UpdateLayout(); Capture(window,"ui-minimum-size.png");
            Assert.True(editor.ActualWidth>0 && editor.ActualHeight>0);
            var blank=PrintPageItem.CreateBlank(SheetPaper.A4);
            blank.Document.AddObject(new TextObject(Guid.NewGuid(),new(new(20,20,100,20)),"Лист без исходного файла",new(FontSizePt:18)));
            blank.Document.AddObject(new ShapeObject(Guid.NewGuid(),new(new(20,70,70,30)),SheetShapeKind.Rectangle,new("#175E9C",0.6,1,"#DCEBFA")));
            vm.AddPage(blank); vm.SelectedPage=blank; await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading); window.UpdateLayout();
            Assert.True(blank.IsBlank); Assert.False(vm.RotateCommand.CanExecute(null)); Capture(window,"pm001-ui-sheet.png");
            blank.Document.MoveObject(blank.Document.Objects[0].Id,10,20); await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading);
            vm.ShowPrintPreview=true; await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading); Capture(window,"pm001-ui-sheet-print-preview.png");
            vm.ShowPrintPreview=false; vm.UndoCommand.Execute(null); await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading);
            Assert.Equal(new RectD(20,20,100,20),blank.Document.Objects[0].Placement.BoundsMm);
            vm.MovePage(vm.Pages[0],vm.Pages[3]); Assert.Equal(selected,vm.Pages[3]);
            vm.SelectNoneCommand.Execute(null); Assert.Equal(0,vm.SelectedCount); Assert.False(vm.PrintCommand.CanExecute(null));
            Assert.True(vm.HasUnsavedChanges);
            var project=ProjectFileTests.PathFor("pm002-ui-project"); dialogs.SavePath=project; vm.SaveProjectCommand.Execute(null);
            await WaitUntil(()=>!vm.IsBusy && File.Exists(project)); Assert.False(vm.HasUnsavedChanges); Assert.Equal(project,vm.CurrentProjectPath);
            var savedCount=vm.Pages.Count; var savedOrder=vm.Pages.Select(p=>p.Id).ToArray();
            Capture(window,"pm002-ui-project-saved.png");
            foreach(var page in vm.Pages.ToArray()) vm.RemovePageCommand.Execute(page);
            Assert.Empty(vm.Pages); dialogs.OpenPath=project; vm.OpenProjectCommand.Execute(null);
            await WaitUntil(()=>!vm.IsBusy && vm.Pages.Count==savedCount && vm.EditorPreview!=null && !vm.IsPreviewLoading);
            Assert.Equal(savedOrder,vm.Pages.Select(p=>p.Id)); Assert.Equal(3,vm.Pages.Single(p=>p.Id==selected.Id).Copies);
            Assert.False(vm.HasUnsavedChanges); Assert.Empty(dialogs.Errors); Capture(window,"pm002-ui-project-restored.png");
            var corrupt=Path.Combine(Fixtures.Root,"pm002-ui-corrupt.printmaster"); File.WriteAllText(corrupt,"bad archive");
            Assert.False(await vm.OpenProjectFromAsync(corrupt)); Assert.Equal(savedOrder,vm.Pages.Select(p=>p.Id)); Assert.Equal(project,vm.CurrentProjectPath); Assert.Single(dialogs.Errors); dialogs.Errors.Clear();
            await vm.FlushRecoveryAsync(); Assert.True(File.Exists(vm.RecoveryFilePath));
            using(var seed=new ProjectRecoveryStore(new(),Path.GetDirectoryName(vm.RecoveryFilePath))) await seed.WriteAsync(vm.CaptureProject(),default);
            dialogs.RestoreRecovery=true; await vm.OfferRecoveryAsync(); Assert.Null(vm.CurrentProjectPath); Assert.True(vm.HasUnsavedChanges); Assert.Equal(savedCount,vm.Pages.Count);
            var missingSource=PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Image,Path.Combine(Fixtures.Root,"missing-original.png"),null,0,1,new(1000,750),new(200,150)));
            vm.AddPage(missingSource); vm.SelectedPage=missingSource; missingSource.IsIncluded=true; Assert.True(vm.PrintCommand.CanExecute(null));
            using(var review=new PrintReviewViewModel(vm.Pages.Where(p=>p.IsIncluded).Select(p=>p.Snapshot()),vm.Settings,vm.Printers.ToArray()))
            { Assert.Contains("Отсутствуют исходники",review.Error); Assert.False(review.TryAccept(true)); }
            dialogs.ReplacementPath=Fixtures.CreateImage("pm002-ui-replacement.png"); await vm.RelinkSourcesCommand.ExecuteAsync(null);
            await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading); Assert.False(missingSource.HasMissingSources); Assert.True(vm.PrintCommand.CanExecute(null));
            Capture(window,"pm002-ui-relinked.png");
            // PM-003: exercise actual commands, bindings, canvas gestures and modal text entry.
            window.Width=1460; window.Height=940;
            var creation=new NewSheetDialog(vm.Settings) { Owner=window }; creation.Show();
            creation.ViewModel.Paper=PaperPreset.Custom; creation.ViewModel.WidthText="164,5"; creation.ViewModel.HeightText="204.5";
            Assert.True(creation.ViewModel.CanCreate); creation.UpdateLayout(); Capture(creation,"pm003-new-sheet-dialog.png"); creation.Close();
            vm.SelectNoneCommand.Execute(null);
            dialogs.NewSheet=new(new(165,205),false,SheetMargins.SafeDefault); var beforeCreation=vm.Pages.Count;
            vm.CreateSheetCommand.Execute(null); var created=vm.SelectedPage!;
            Assert.Equal(beforeCreation+1,vm.Pages.Count); Assert.True(created.IsBlank); Assert.True(vm.CanEdit); Assert.True(vm.ShowEditorTools);
            Assert.False(vm.CanCropImage); Assert.Equal(PaperPreset.Notebook,vm.LayoutSettings.Paper); Assert.True(vm.PrintCommand.CanExecute(null));
            created.Copies=2; Assert.Equal(2,vm.Impressions); Assert.Equal(165*96/25.4,vm.CanvasWidth,6);
            Assert.InRange(vm.SafeAreaMm.X,5,6); Assert.InRange(vm.SafeAreaMm.Y,5,6);
            var sheetEditor=(SheetCanvas)window.FindName("SheetEditor"); await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading);
            Assert.Equal(Visibility.Visible,sheetEditor.Visibility); Assert.True(sheetEditor.IsVisible); Assert.False(editor.IsVisible);
            var imageOriginal=Fixtures.CreateImage("pm003-insert-source.png"); var originalBytes=File.ReadAllBytes(imageOriginal);
            dialogs.SheetImages=[imageOriginal]; await vm.InsertSheetImagesCommand.ExecuteAsync(null);
            Assert.Single(created.Document.Objects); Assert.Single(created.Document.Snapshot().Assets);
            Assert.Equal(originalBytes,File.ReadAllBytes(imageOriginal)); Assert.Equal(1,created.Document.PrimaryAsset?.PageCount ?? 1);
            vm.UndoCommand.Execute(null); Assert.Empty(created.Document.Objects); Assert.Empty(created.Document.Snapshot().Assets);
            vm.RedoCommand.Execute(null); var photo=(ContentObject)created.Document.Objects.Single(); Assert.True(created.IsBlank);
            await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading); window.UpdateLayout();
            vm.SetToolCommand.Execute(EditorTool.Select); var origin=photo.Placement.BoundsMm;
            sheetEditor.BeginGesture(new(origin.X+origin.Width/2,origin.Y+origin.Height/2));
            await WaitUntil(()=>sheetEditor.MovePreviewReady); sheetEditor.UpdateGesture(new(origin.X+origin.Width/2+10,origin.Y+origin.Height/2+8));
            Capture(window,"pm003-moving-photo.png"); sheetEditor.CommitGesture();
            Assert.Equal(origin.X+10,created.Document.Objects.Single().Placement.BoundsMm.X,6); Assert.Equal(photo.Id,vm.SelectedSheetObjectId);
            vm.UndoCommand.Execute(null); Assert.Equal(origin,created.Document.Objects.Single().Placement.BoundsMm);
            vm.SelectedSheetObjectId=photo.Id; vm.ObjectWidth=80; Assert.Equal(60,vm.ObjectHeight,6); vm.ObjectX=70; vm.ObjectY=42;
            vm.AlignSheetObjectCommand.Execute("right"); Assert.Equal(vm.SafeAreaMm.Right,SheetEditing.VisualBounds(vm.SelectedSheetObject!).Right,6); vm.ObjectX=70;
            var textContent="PrintMaster — новый лист\nТекст, фотография и рисунки внутри приложения.";
            vm.SetToolCommand.Execute(EditorTool.Text); sheetEditor.BeginGesture(new(14,12));
            var inline=(InlineTextEditor)window.FindName("TextEditor"); Assert.True(vm.IsEditingText);
            new System.Windows.Documents.TextRange(inline.TextInputBox.Document.ContentStart,inline.TextInputBox.Document.ContentEnd).Text=textContent;
            vm.FinishTextEditCommand.Execute(null);
            var caption=created.Document.Objects.OfType<TextObject>().Single(); Assert.Equal(textContent,caption.Text); Assert.Equal(14,caption.Style.FontSizePt,6);
            vm.SetToolCommand.Execute(EditorTool.Rectangle); sheetEditor.BeginGesture(new(14,112)); sheetEditor.UpdateGesture(new(151,150)); sheetEditor.CommitGesture();
            vm.SetToolCommand.Execute(EditorTool.Arrow); sheetEditor.BeginGesture(new(140,105)); sheetEditor.UpdateGesture(new(100,105)); sheetEditor.CommitGesture();
            vm.SetToolCommand.Execute(EditorTool.Brush); sheetEditor.BeginGesture(new(22,170)); sheetEditor.UpdateGesture(new(42,180)); sheetEditor.UpdateGesture(new(65,172)); sheetEditor.CommitGesture();
            Assert.Single(created.Document.Objects.OfType<InkObject>()); Assert.Equal(5,created.Document.Objects.Count);
            vm.SetToolCommand.Execute(EditorTool.Select); await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading); window.UpdateLayout(); Capture(window,"pm003-sheet-editor.png");
            Assert.False(vm.HasSheetClipping); Assert.False(vm.IsTextMode); Assert.True(vm.EditorPreview!.PixelWidth<=1600 && vm.EditorPreview.PixelHeight<=1600);
            vm.SelectedSheetObjectId=caption.Id; vm.ObjectX=-3; Assert.True(vm.HasSheetClipping); vm.UndoCommand.Execute(null); Assert.False(vm.HasSheetClipping);
            vm.RemoveSheetObjectCommand.Execute(null); Assert.Empty(created.Document.Objects.OfType<TextObject>()); vm.UndoCommand.Execute(null); Assert.Single(created.Document.Objects.OfType<TextObject>());
            vm.ShowPrintPreview=true; await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading); Assert.False(vm.CanEdit); Assert.False(vm.InsertSheetImagesCommand.CanExecute(null));
            var objectCount=created.Document.Objects.Count; sheetEditor.BeginGesture(new(20,20)); sheetEditor.UpdateGesture(new(30,30)); sheetEditor.CommitGesture(); Assert.Equal(objectCount,created.Document.Objects.Count);
            Capture(window,"pm003-sheet-print-preview.png"); vm.ShowPrintPreview=false;
            vm.LayoutSettings.Paper=PaperPreset.A5; Assert.Equal(new SizeD(148,210),created.Document.Paper.SizeMm); vm.UndoCommand.Execute(null); Assert.Equal(PaperPreset.Notebook,vm.LayoutSettings.Paper);
            var other=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(other); Assert.True(vm.HasSheetPaperMismatch); Assert.True(vm.PrintCommand.CanExecute(null));
            using(var review=new PrintReviewViewModel(vm.Pages.Where(p=>p.IsIncluded).Select(p=>p.Snapshot()),vm.Settings,vm.Printers.ToArray()))
            {
                Assert.True(review.HasMixedPaper);
                if(review.IsPhysicalPrinter) Assert.True(review.CanSubmit,review.Error);
                else { Assert.False(review.CanSubmit); Assert.Contains("Размер бумаги",review.Error); }
            }
            other.IsIncluded=false; Assert.False(vm.HasSheetPaperMismatch);
            var demo=Path.Combine(Fixtures.Root,"pm003-demo.printmaster"); Assert.True(await vm.SaveProjectToAsync(demo));
            Assert.True(await vm.OpenProjectFromAsync(demo)); created=vm.Pages.Single(p=>p.Id==created.Id);
            Assert.Equal(5,created.Document.Objects.Count); Assert.Equal(2,created.Copies); Assert.Single(created.Document.Snapshot().Assets);
            Assert.True(vm.CanEdit); Assert.False(vm.HasUnsavedChanges); Assert.Empty(dialogs.Errors);
            await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading);
            window.Width=1180; window.Height=720; window.UpdateLayout(); Capture(window,"pm003-minimum-size.png");
            var headerCreate=Descendants(window).OfType<Button>().First(b=>b.Command==vm.CreateSheetCommand);
            Assert.True(headerCreate.IsVisible); Assert.True(headerCreate.ActualWidth>80);
            // PM-004: inline entry, formatting, width handle, grouped pagination and persistence.
            window.Width=1460; window.Height=940; window.UpdateLayout();
            vm.SelectedSheetObjectId=created.Document.Objects.OfType<TextObject>().Single().Id;
            vm.EditSelectedTextCommand.Execute(null); Assert.True(inline.IsVisible); Assert.True(vm.IsEditingText);
            var originalCaption=vm.EditingText;
            new System.Windows.Documents.TextRange(inline.TextInputBox.Document.ContentStart,inline.TextInputBox.Document.ContentEnd).Text=originalCaption+"\nНовая строка прямо на листе.";
            vm.TextFontFamily="Arial"; vm.TextFontSizePt=16; vm.TextBold=true; vm.TextItalic=true; vm.TextColor="#175E9C"; vm.TextAlignment=PrintMaster.Models.TextAlignment.Center; vm.TextLineSpacing=1.5;
            Assert.Contains("Новая строка",vm.EditingText); Capture(window,"pm004-inline-text.png");
            vm.CancelTextEditCommand.Execute(null); Assert.False(vm.IsEditingText); Assert.Equal(originalCaption,created.Document.Objects.OfType<TextObject>().Single().Text);
            Assert.Equal(14,created.Document.Objects.OfType<TextObject>().Single().Style.FontSizePt);
            vm.SelectedSheetObjectId=created.Document.Objects.OfType<TextObject>().Single().Id; vm.EditSelectedTextCommand.Execute(null);
            new System.Windows.Documents.TextRange(inline.TextInputBox.Document.ContentStart,inline.TextInputBox.Document.ContentEnd).Text="Редактирование на листе\nШрифт, цвет и интервал сохраняются.";
            vm.TextFontFamily="Arial"; vm.TextFontSizePt=16; vm.TextBold=true; vm.TextItalic=true; vm.TextColor="#175E9C"; vm.TextAlignment=PrintMaster.Models.TextAlignment.Center; vm.TextLineSpacing=1.5;
            vm.FinishTextEditCommand.Execute(null); var formatted=created.Document.Objects.OfType<TextObject>().Single(); Assert.True(formatted.Style.Bold); Assert.True(formatted.Style.Italic);
            Assert.Equal("Arial",formatted.Style.FontFamily); Assert.Equal(1.5,formatted.Style.LineSpacing);
            var originalTextBounds=formatted.Placement.BoundsMm; vm.SelectedSheetObjectId=formatted.Id;
            sheetEditor.BeginGesture(new(originalTextBounds.Right,originalTextBounds.Y+originalTextBounds.Height/2));
            sheetEditor.UpdateGesture(new(originalTextBounds.Right-55,originalTextBounds.Y+originalTextBounds.Height/2)); sheetEditor.CommitGesture();
            Assert.Equal(originalTextBounds.Width-55,vm.ObjectWidth,6); Assert.True(vm.ObjectHeight>=originalTextBounds.Height);
            vm.UndoCommand.Execute(null); Assert.Equal(originalTextBounds,created.Document.Objects.OfType<TextObject>().Single().Placement.BoundsMm);
            vm.UndoCommand.Execute(null); Assert.Equal(originalCaption,created.Document.Objects.OfType<TextObject>().Single().Text); vm.RedoCommand.Execute(null);
            vm.SelectedSheetObjectId=created.Document.Objects.OfType<TextObject>().Single().Id;
            await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading); Capture(window,"pm004-formatted-text.png");
            vm.SelectNoneCommand.Execute(null); dialogs.NewSheet=new(new(148,210),false,SheetMargins.SafeDefault); vm.CreateSheetCommand.Execute(null);
            var emptyTextSheet=vm.SelectedPage!; emptyTextSheet.Copies=1; var queueBeforeFlow=vm.Pages.Count;
            vm.BeginFlowTextCommand.Execute(null);
            var longText=string.Join("\n",Enumerable.Range(1,110).Select(i=>$"{i:D3}. Продолжение текста сохраняет все слова и исходный размер шрифта."));
            new System.Windows.Documents.TextRange(inline.TextInputBox.Document.ContentStart,inline.TextInputBox.Document.ContentEnd).Text=longText;
            await vm.FlushRecoveryAsync(); using(var checkpoint=new SessionWorkspace())
            {
                var typed=await new ProjectFileService().LoadAsync(vm.RecoveryFilePath,checkpoint,default);
                Assert.Equal(longText,typed.Session.Pages.Single(p=>p.Sheet.Id==emptyTextSheet.Id).Sheet.Objects.OfType<TextObject>().Single().Text);
            }
            vm.FinishTextEditCommand.Execute(null); var firstFlow=vm.SelectedPage!; var storyId=firstFlow.Document.Objects.OfType<TextObject>().Single().Flow!.StoryId;
            Assert.Equal(longText,vm.StoryText(storyId)); Assert.True(vm.Pages.Count>queueBeforeFlow);
            var normalPageCount=vm.Pages.Count; vm.TextLineSpacing=2; Assert.True(vm.Pages.Count>normalPageCount);
            vm.UndoCommand.Execute(null); Assert.Equal(normalPageCount,vm.Pages.Count); vm.UndoCommand.Execute(null);
            Assert.Same(emptyTextSheet,vm.SelectedPage); Assert.Equal(queueBeforeFlow,vm.Pages.Count); vm.RedoCommand.Execute(null);
            Assert.Equal(longText,vm.StoryText(storyId)); Assert.False(vm.HasSheetClipping);
            await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading); Capture(window,"pm004-text-pages.png");
            vm.ShowPrintPreview=true; await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading); Capture(window,"pm004-text-pages-print.png"); vm.ShowPrintPreview=false;
            var textProject=Path.Combine(Fixtures.Root,"pm004-demo.printmaster"); Assert.True(await vm.SaveProjectToAsync(textProject)); Assert.True(await vm.OpenProjectFromAsync(textProject));
            Assert.Equal(longText,vm.StoryText(storyId)); Assert.False(vm.HasUnsavedChanges); Assert.Empty(dialogs.Errors);
            // PM-005: route commands through the real WPF window, keeping the system clipboard untouched.
            vm.SelectNoneCommand.Execute(null); dialogs.NewSheet=SheetPaper.A4; vm.CreateSheetCommand.Execute(null); var pastedSheet=vm.SelectedPage!;
            window.UpdateLayout(); sheetEditor.Focus(); Assert.False(System.Windows.Input.ApplicationCommands.Paste.CanExecute(null,sheetEditor));
            clipboard.Content=new([],Text:"# Обычный текст из буфера\n**никакого автоматического Markdown**");
            Assert.True(System.Windows.Input.ApplicationCommands.Paste.CanExecute(null,sheetEditor));
            System.Windows.Input.ApplicationCommands.Paste.Execute(null,sheetEditor); await WaitUntil(()=>pastedSheet.Document.Objects.OfType<TextObject>().Any());
            var pastedText=pastedSheet.Document.Objects.OfType<TextObject>().Single(); Assert.Equal(clipboard.Content.Text,pastedText.Text); Assert.False(pastedText.Style.Bold);
            vm.SelectedSheetObjectId=pastedText.Id; sheetEditor.Focus(); Assert.True(System.Windows.Input.ApplicationCommands.Copy.CanExecute(null,sheetEditor));
            System.Windows.Input.ApplicationCommands.Copy.Execute(null,sheetEditor); await WaitUntil(()=>!vm.IsBusy && clipboard.Writes>0); Assert.NotNull(clipboard.Content.ObjectJson);
            System.Windows.Input.ApplicationCommands.Paste.Execute(null,sheetEditor); await WaitUntil(()=>!vm.IsBusy && pastedSheet.Document.Objects.Count==2);
            Assert.NotEqual(pastedText.Id,pastedSheet.Document.Objects[1].Id); vm.UndoCommand.Execute(null); Assert.Single(pastedSheet.Document.Objects); vm.RedoCommand.Execute(null); Assert.Equal(2,pastedSheet.Document.Objects.Count);
            clipboard.Content=new([],ClipboardTests.Image()); sheetEditor.Focus(); System.Windows.Input.ApplicationCommands.Paste.Execute(null,sheetEditor);
            await WaitUntil(()=>!vm.IsBusy && pastedSheet.Document.Objects.OfType<ContentObject>().Any()); Assert.Null(pastedSheet.Document.Snapshot().Assets.Single().OriginalPath);
            await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading); Capture(window,"pm005-clipboard-sheet.png");
            vm.SelectedSheetObjectId=pastedText.Id; vm.EditSelectedTextCommand.Execute(null);
            inline.TextInputBox.SelectAll(); Assert.True(System.Windows.Input.ApplicationCommands.Copy.CanExecute(null,inline.TextInputBox));
            var previousReads=clipboard.Reads; var previousWrites=clipboard.Writes;
            var plainData=new DataObject(DataFormats.UnicodeText,"Вставить в курсор"); var pasteEvent=new DataObjectPastingEventArgs(plainData,false,DataFormats.UnicodeText);
            inline.TextInputBox.RaiseEvent(pasteEvent); Assert.False(pasteEvent.CommandCancelled); Assert.Equal(previousReads,clipboard.Reads); Assert.Equal(previousWrites,clipboard.Writes);
            vm.CancelTextEditCommand.Execute(null); sheetEditor.Focus();
            clipboard.Content=new([],ClipboardTests.Image()); QueueListFocus(window); System.Windows.Input.ApplicationCommands.Paste.Execute(null,(ListBox)window.FindName("QueueList"));
            await WaitUntil(()=>!vm.IsBusy && vm.SelectedPage?.IsImage==true); Assert.True(pastedSheet.IsBlank);
            var clipboardProject=Path.Combine(Fixtures.Root,"pm005-demo.printmaster"); Assert.True(await vm.SaveProjectToAsync(clipboardProject)); Assert.True(await vm.OpenProjectFromAsync(clipboardProject));
            Assert.Empty(dialogs.Errors); Assert.False(vm.HasUnsavedChanges); window.Width=1180; window.Height=720; window.UpdateLayout(); Capture(window,"pm005-minimum-size.png");
            await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading); window.UpdateLayout(); Capture(window,"pm005-minimum-size.png");
            // PM-006: actual viewport coordinates across fit, zoom, pan and all preview modes.
            window.Width=1460; window.Height=940; window.UpdateLayout();
            vm.SelectedPage=vm.Pages.Single(p=>p.Id==pastedSheet.Id); await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading); window.Navigation.Refit();
            var scroll=(ScrollViewer)window.FindName("CanvasScroll"); var viewport=Descendants(scroll).OfType<ScrollContentPresenter>().First();
            var beforeSheet=vm.SelectedPage!.Document.Snapshot(); var beforePrint=vm.Settings.Snapshot(); var beforeCopies=vm.Impressions;
            var anchor=new Point(viewport.ActualWidth*0.37,viewport.ActualHeight*0.42); var logical=viewport.TranslatePoint(anchor,sheetEditor);
            Assert.True(window.Navigation.HandleWheel(anchor,120,true)); window.UpdateLayout();
            var afterAnchor=sheetEditor.TranslatePoint(logical,viewport); Assert.InRange(Math.Abs(afterAnchor.X-anchor.X),0,1.5); Assert.InRange(Math.Abs(afterAnchor.Y-anchor.Y),0,1.5);
            Assert.False(vm.AutoFitZoom); var manualZoom=vm.Zoom;
            Assert.False(window.Navigation.HandleWheel(anchor,120,false)); Assert.Equal(manualZoom,vm.Zoom);
            var oldX=scroll.HorizontalOffset; var oldY=scroll.VerticalOffset; window.Navigation.PanBy(new(-25,-30));
            Assert.InRange(scroll.HorizontalOffset-oldX,24,26); Assert.InRange(scroll.VerticalOffset-oldY,29,31);
            vm.ZoomText="175,5%"; Assert.Equal(175.5,vm.Zoom); vm.ZoomText="NaN"; Assert.Equal(175.5,vm.Zoom);
            var zoomField=(NumericField)window.FindName("ZoomInput"); zoomField.Input.Text="250"; Assert.True(zoomField.Commit()); Assert.Equal(250,vm.Zoom);
            Capture(window,"pm006-zoom-sheet.png");
            vm.ZoomInCommand.Execute(null); Assert.Equal(260,vm.Zoom); vm.ZoomOutCommand.Execute(null); Assert.Equal(250,vm.Zoom);
            Assert.Equal(beforeSheet,vm.SelectedPage.Document.Snapshot()); Assert.Equal(beforePrint,vm.Settings.Snapshot()); Assert.Equal(beforeCopies,vm.Impressions);
            vm.FitZoomCommand.Execute(null); await Task.Delay(75); window.UpdateLayout(); Assert.True(vm.AutoFitZoom);
            var center=sheetEditor.TranslatePoint(new(sheetEditor.ActualWidth/2,sheetEditor.ActualHeight/2),viewport);
            Assert.InRange(Math.Abs(center.X-viewport.ActualWidth/2),0,2); Assert.InRange(Math.Abs(center.Y-viewport.ActualHeight/2),0,2);
            vm.ShowPrintPreview=true; await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading);
            logical=viewport.TranslatePoint(anchor,sheetEditor); window.Navigation.HandleWheel(anchor,120,true); afterAnchor=sheetEditor.TranslatePoint(logical,viewport);
            Assert.InRange(Math.Abs(afterAnchor.X-anchor.X),0,2); Assert.InRange(Math.Abs(afterAnchor.Y-anchor.Y),0,2); Assert.Equal(beforeSheet,vm.SelectedPage.Document.Snapshot());
            vm.ShowPrintPreview=false; vm.SelectedPage=vm.Pages.First(p=>p.Kind==SourceKind.Pdf); await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading); window.Navigation.Refit();
            var pdfModel=vm.SelectedPage!.Document.Snapshot(); logical=viewport.TranslatePoint(anchor,editor); window.Navigation.HandleWheel(anchor,120,true); afterAnchor=editor.TranslatePoint(logical,viewport);
            Assert.InRange(Math.Abs(afterAnchor.X-anchor.X),0,2); Assert.InRange(Math.Abs(afterAnchor.Y-anchor.Y),0,2); Assert.Equal(pdfModel,vm.SelectedPage.Document.Snapshot());
            Capture(window,"pm006-zoom-pdf.png"); Assert.NotNull(window.Icon);
            vm.SelectedPage=vm.Pages.First(p=>p.IsImage); await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading); window.Navigation.Refit();
            var imageModel=vm.SelectedPage!.Document.Snapshot(); logical=viewport.TranslatePoint(anchor,editor); window.Navigation.HandleWheel(anchor,-120,true); afterAnchor=editor.TranslatePoint(logical,viewport);
            Assert.InRange(Math.Abs(afterAnchor.X-anchor.X),0,2); Assert.InRange(Math.Abs(afterAnchor.Y-anchor.Y),0,2); Assert.Equal(imageModel,vm.SelectedPage.Document.Snapshot());
            vm.SetManualZoom(800); Assert.Equal(800,vm.Zoom); window.Navigation.HandleWheel(anchor,120,true); Assert.Equal(800,vm.Zoom);
            vm.SetManualZoom(5); Assert.Equal(5,vm.Zoom); window.Navigation.HandleWheel(anchor,-120,true); Assert.Equal(5,vm.Zoom);
            vm.SetManualZoom(400); var zoomProject=Path.Combine(Fixtures.Root,"pm006-demo.printmaster"); Assert.True(await vm.SaveProjectToAsync(zoomProject)); Assert.True(await vm.OpenProjectFromAsync(zoomProject));
            Assert.Equal(400,vm.Zoom); Assert.False(vm.AutoFitZoom); Assert.Empty(dialogs.Errors); await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading);
            window.Width=1180; window.Height=720; window.UpdateLayout(); Capture(window,"pm006-minimum-size.png");
            // UX-001: a routed paste changes the editor in place, retaining source edits and focus.
            window.Width=1460; window.Height=940; vm.FitZoomCommand.Execute(null); vm.SelectNoneCommand.Execute(null);
            await vm.ImportFilesAsync([Fixtures.CreateImage("ux001-wpf-base.png")]); vm.SelectedPage=vm.Pages.Last();
            vm.Settings.Paper=PaperPreset.A4; vm.Settings.Landscape=false;
            var insertionPage=vm.SelectedPage!; insertionPage.Edits.SetCrop(new(60,50,800,600)); insertionPage.RotationQuarterTurns=1;
            insertionPage.Edits.Add(Annotation.Create(AnnotationKind.Arrow,new(100,100),new(500,300),"#175E9C",5));
            await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading); window.UpdateLayout();
            var queueCount=vm.Pages.Count; var beforeInsertion=insertionPage.Document.Snapshot(); var insertionFile=Fixtures.CreateImage("ux001-wpf-overlay.jpg");
            window.ShowFileDropFeedback([insertionFile],true,new(140,190)); window.UpdateLayout();
            Assert.Equal(Visibility.Visible,((System.Windows.Shapes.Rectangle)window.FindName("DropRectangle")).Visibility);
            Capture(window,"ux001-drop-target.png"); window.ClearFileDropFeedback();
            clipboard.Content=new([],ClipboardTests.Image()); editor.Focus();
            System.Windows.Input.ApplicationCommands.Paste.Execute(null,editor);
            await WaitUntil(()=>!vm.IsBusy && vm.IsSheetSelected && vm.EditorPreview!=null && !vm.IsPreviewLoading); window.UpdateLayout();
            Assert.Equal(queueCount,vm.Pages.Count); Assert.Equal(2,insertionPage.Document.Objects.Count); Assert.True(sheetEditor.IsKeyboardFocusWithin);
            Assert.Equal(beforeInsertion.PrimaryContent!.Edits,insertionPage.Document.PrimaryContent!.Edits);
            var pastedContent=vm.SelectedSheetObject!; var b=pastedContent.Placement.BoundsMm; var start=new PointD(b.X+b.Width/2,b.Y+b.Height/2);
            sheetEditor.BeginGesture(start);
            var movePreviewDeadline=DateTime.UtcNow.AddSeconds(20);
            while(!sheetEditor.MovePreviewReady && sheetEditor.MovePreviewError==null && DateTime.UtcNow<movePreviewDeadline) await Task.Delay(25);
            Assert.True(sheetEditor.MovePreviewReady,$"Move preview did not render. Error: {sheetEditor.MovePreviewError ?? "none"}; selected: {vm.SelectedSheetObjectId}; objects: {insertionPage.Document.Objects.Count}; status: {vm.Status}");
            sheetEditor.UpdateGesture(new(start.X+7,start.Y+4)); sheetEditor.CommitGesture();
            Assert.Equal(b.X+7,vm.SelectedSheetObject!.Placement.BoundsMm.X,5);
            vm.UndoCommand.Execute(null); vm.UndoCommand.Execute(null); Assert.Equal(beforeInsertion,insertionPage.Document.Snapshot()); Assert.False(vm.IsSheetSelected);
            await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading);
            Assert.InRange(vm.EditorPreview!.PixelWidth,600,700); Assert.InRange(vm.EditorPreview.PixelHeight,400,550);
            vm.RedoCommand.Execute(null); Assert.True(vm.IsSheetSelected);
            await vm.HandleFileDropAsync([insertionFile],true,new(110,180)); Assert.Equal(3,insertionPage.Document.Objects.Count); Assert.Equal(queueCount,vm.Pages.Count);
            await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading); window.UpdateLayout(); Capture(window,"ux001-composed-sheet.png");
            vm.ShowPrintPreview=true; await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading); window.UpdateLayout(); Capture(window,"ux001-print-preview.png");
            vm.ShowPrintPreview=false;
            var insertionSession=vm.CaptureProject(); await new ProjectFileService().SaveAsync(insertionSession with { Pages=[insertionSession.Pages.Single(p=>p.Sheet.Id==insertionPage.Id)] },Path.Combine(Fixtures.Root,"ux001-demo.printmaster"),true,default);
            await VerifyFocusShortcuts(window,vm,clipboard);
            await VerifyUnifiedHistory(window,vm);
            await VerifyClipboardMenu(window,vm,clipboard,dialogs);
            await VerifyWorkspaceAndDialogs(window,vm,dialogs);
            await VerifyFullColorEditor(window,vm);
            await VerifyDrawingInspectorInCompactWindow(window,vm);
            await VerifyContextMenus(window,vm,clipboard);
            VerifyQueueContextTargetAfterFlowCommit(window,vm);
            await VerifyQueueRightClickAfterFlowCommitAsync(window,vm);
            VerifyReviewCommandPresentation(window,vm);
            await VerifyResponsiveWorkspace(window,vm);
            await VerifyDragDoesNotRevertWhilePreviewIsPending(window);
            await VerifyObjectTransformGestures(window);
            await VerifyUnifiedTextEditor(window,vm);
            await VerifyNumericInput(window,vm);
            await VerifySheetExport(window,vm,dialogs);
            await VerifyWorkspacePersistence(window);
            await VerifyStretchMode(window,vm);
            await VerifyOperationFeedback(window,vm);
            await VerifyInteractiveCrop(window,vm);
            await VerifyPrintableSnapping(window,vm);
            await VerifyColorPickingAndAppearance(window,vm);
            await VerifyPartialInkErasure(window,vm);
            await VerifyPhotoGridDialog(window,vm);
            await VerifyMarkdownEditor(window,vm);
            await VerifyPageOptionsDialog(window,vm);
            Assert.Empty(dialogs.Errors);
            missingSource=vm.Pages.Single(p=>p.Id==missingSource.Id);
            missingSource.Copies=7; await vm.PrepareCloseAsync(); Assert.True(vm.IsClosePrepared);
            using(var check=new SessionWorkspace())
            {
                var recovery=await new ProjectFileService().LoadAsync(vm.RecoveryFilePath,check,default);
                Assert.Equal(7,recovery.Session.Pages.Single(p=>p.Sheet.Id==missingSource.Id).Copies);
            }
            Assert.DoesNotContain("System.Windows.Data Error",bindingErrors.ToString());
            File.WriteAllText(Path.Combine(Fixtures.Root,"wpf-bindings.log"),bindingErrors.ToString());
        }
        finally { await vm.PrepareCloseAsync(); window.Close(); PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); app.Shutdown(); }
    });

    private static void Capture(Window window,string name)
    {
        var root=(FrameworkElement)window.Content; root.UpdateLayout();
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),(int)Math.Ceiling(root.ActualHeight),96,96,System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(root); var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output=File.Create(Path.Combine(Fixtures.Root,name)); encoder.Save(output);
    }
    private static void QueueListFocus(Window window)=>((ListBox)window.FindName("QueueList")).Focus();
    private static async Task VerifyFocusShortcuts(MainWindow window,PrintSessionViewModel vm,FakeClipboard clipboard)
    {
        var queue=(ListBox)window.FindName("QueueList"); var editor=(SheetCanvas)window.FindName("SheetEditor");
        var page=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(page); vm.SelectedPage=page; vm.Tool=EditorTool.Select;
        var a=new ShapeObject(Guid.NewGuid(),new(new(20,25,30,35)),SheetShapeKind.Rectangle,new());
        var b=new TextObject(Guid.NewGuid(),new(new(70,80,80,30)),"Ctrl+A — текст",new());
        page.Document.AddObjects([a,b]); await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading); window.UpdateLayout();
        Assert.True(editor.Focus()); Assert.Equal(ShortcutScope.Sheet,window.CurrentShortcutScope);
        Assert.True(window.HandleShortcut(System.Windows.Input.Key.A,System.Windows.Input.ModifierKeys.Control));
        Assert.Equal(new[] {a.Id,b.Id},vm.SelectedObjectIds); var queueCount=vm.Pages.Count;
        editor.BeginGesture(new(30,35)); await WaitUntil(()=>editor.MovePreviewReady); editor.UpdateGesture(new(37,39)); editor.CommitGesture();
        Assert.Equal(new RectD(27,29,30,35),page.Document.Objects[0].Placement.BoundsMm);
        Assert.Equal(new RectD(77,84,80,30),page.Document.Objects[1].Placement.BoundsMm);
        Assert.True(window.HandleShortcut(System.Windows.Input.Key.Z,System.Windows.Input.ModifierKeys.Control));
        Assert.Equal(new SheetObject[] {a,b},page.Document.Objects);
        var beforeCopy=clipboard.Writes; System.Windows.Input.ApplicationCommands.Copy.Execute(null,editor);
        await WaitUntil(()=>!vm.IsBusy && clipboard.Writes==beforeCopy+1); Assert.Equal(ClipboardSelectionKind.Objects,clipboard.Content.SelectionKind);
        Assert.True(window.HandleShortcut(System.Windows.Input.Key.Delete,System.Windows.Input.ModifierKeys.None)); Assert.Empty(page.Document.Objects); Assert.Equal(queueCount,vm.Pages.Count);
        Assert.True(window.HandleShortcut(System.Windows.Input.Key.Z,System.Windows.Input.ModifierKeys.Control)); Assert.Equal(new SheetObject[] {a,b},page.Document.Objects);
        Assert.True(window.HandleShortcut(System.Windows.Input.Key.A,System.Windows.Input.ModifierKeys.Control));
        Assert.True(window.HandleShortcut(System.Windows.Input.Key.D,System.Windows.Input.ModifierKeys.Control)); Assert.Equal(4,page.Document.Objects.Count);
        Assert.True(window.HandleShortcut(System.Windows.Input.Key.Z,System.Windows.Input.ModifierKeys.Control)); Assert.Equal(2,page.Document.Objects.Count);
        var pages=vm.Pages.ToArray(); var flags=pages.Select(p=>p.IsIncluded).ToArray(); var copies=pages.Select(p=>p.Copies).ToArray();
        Assert.True(queue.Focus()); Assert.Equal(ShortcutScope.Queue,window.CurrentShortcutScope);
        Assert.True(window.HandleShortcut(System.Windows.Input.Key.A,System.Windows.Input.ModifierKeys.Control));
        Assert.Equal(pages.Length,queue.SelectedItems.Count); Assert.Equal(pages,vm.HighlightedPages); Assert.Equal(flags,pages.Select(p=>p.IsIncluded));
        var field=((NumericField)window.FindName("BatchCopiesInput")).Input; Assert.True(field.Focus()); field.Text="123"; field.CaretIndex=3;
        Assert.Equal(ShortcutScope.Text,window.CurrentShortcutScope);
        Assert.False(window.HandleShortcut(System.Windows.Input.Key.A,System.Windows.Input.ModifierKeys.Control));
        System.Windows.Input.ApplicationCommands.SelectAll.Execute(null,field); Assert.Equal("123",field.SelectedText);
        Assert.False(window.HandleShortcut(System.Windows.Input.Key.Delete,System.Windows.Input.ModifierKeys.None));
        Assert.False(window.HandleShortcut(System.Windows.Input.Key.Z,System.Windows.Input.ModifierKeys.Control));
        Assert.Equal(pages,vm.HighlightedPages); Assert.Equal(new SheetObject[] {a,b},page.Document.Objects); Assert.Equal(pages.Length,vm.Pages.Count);
        queue.Focus(); Assert.True(window.HandleShortcut(System.Windows.Input.Key.Delete,System.Windows.Input.ModifierKeys.None)); Assert.Empty(vm.Pages);
        Assert.True(window.HandleShortcut(System.Windows.Input.Key.Z,System.Windows.Input.ModifierKeys.Control));
        Assert.Equal(pages,vm.Pages); Assert.Equal(flags,pages.Select(p=>p.IsIncluded)); Assert.Equal(copies,pages.Select(p=>p.Copies)); Assert.Equal(pages.Length,queue.SelectedItems.Count);
        Assert.True(window.HandleShortcut(System.Windows.Input.Key.Y,System.Windows.Input.ModifierKeys.Control)); Assert.Empty(vm.Pages);
        Assert.True(window.HandleShortcut(System.Windows.Input.Key.Z,System.Windows.Input.ModifierKeys.Control)); Assert.Equal(pages,vm.Pages);
        vm.SelectedPage=page; vm.BeginTextEditing(new(70,80),b); window.UpdateLayout();
        var input=((InlineTextEditor)window.FindName("TextEditor")).TextInputBox; Assert.True(input.Focus());
        Assert.Equal(ShortcutScope.Text,window.CurrentShortcutScope); Assert.False(window.HandleShortcut(System.Windows.Input.Key.A,System.Windows.Input.ModifierKeys.Control));
        System.Windows.Input.ApplicationCommands.SelectAll.Execute(null,input); Assert.Equal(b.Text,input.Selection.Text.TrimEnd('\r','\n'));
        Assert.False(window.HandleShortcut(System.Windows.Input.Key.Delete,System.Windows.Input.ModifierKeys.None)); Assert.Equal(pages.Length,vm.Pages.Count);
        input.Selection.Text="Новый текст"; Assert.Equal("Новый текст",vm.EditingText);
        Assert.False(window.HandleShortcut(System.Windows.Input.Key.Z,System.Windows.Input.ModifierKeys.Control));
        System.Windows.Input.ApplicationCommands.Undo.Execute(null,input); Assert.Equal(b.Text,vm.EditingText);
        Assert.True(window.HandleShortcut(System.Windows.Input.Key.Z,System.Windows.Input.ModifierKeys.Control|System.Windows.Input.ModifierKeys.Shift));
        Assert.Equal("Новый текст",vm.EditingText); Assert.Equal(pages.Length,vm.Pages.Count);
        vm.CancelTextEditCommand.Execute(null); await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading);
        vm.SetObjectSelection([a.Id]); editor.Focus(); Capture(window,"ux002-focus-selection.png");
        var session=vm.CaptureProject();
        await new ProjectFileService().SaveAsync(session with { Pages=[session.Pages.Single(p=>p.Sheet.Id==page.Id)] },Path.Combine(Fixtures.Root,"ux002-demo.printmaster"),true,default);
    }
    private static async Task VerifyUnifiedHistory(MainWindow window,PrintSessionViewModel vm)
    {
        var queue=(ListBox)window.FindName("QueueList"); var undo=(Button)window.FindName("UndoButton"); var redo=(Button)window.FindName("RedoButton");
        var a=PrintPageItem.CreateBlank(SheetPaper.A4); var b=PrintPageItem.CreateBlank(SheetPaper.A4);
        var shape=new ShapeObject(Guid.NewGuid(),new(new(20,25,40,35)),SheetShapeKind.Rectangle,new("#111827",0.7));
        a.Document.AddObjects([shape,new TextObject(Guid.NewGuid(),new(new(20,80,150,40)),"История: лист A",new("Arial",18,Bold:true))]);
        b.Document.AddObject(new TextObject(Guid.NewGuid(),new(new(20,80,150,40)),"История: лист B",new("Arial",18,Bold:true)));
        vm.AddPage(a); vm.AddPage(b); vm.SelectedPage=a;
        a.Document.MoveObject(shape.Id,7,3); vm.SelectedPage=b; window.UpdateLayout(); queue.Focus();
        Assert.True(window.HandleShortcut(System.Windows.Input.Key.Z,System.Windows.Input.ModifierKeys.Control));
        Assert.Equal(shape.Placement,a.Document.Objects[0].Placement); Assert.Same(a,vm.SelectedPage);
        System.Windows.Input.ApplicationCommands.Redo.Execute(null,queue);
        Assert.Equal(new RectD(27,28,40,35),a.Document.Objects[0].Placement.BoundsMm);
        vm.SelectedPage=b; undo.Command.Execute(null); Assert.Same(a,vm.SelectedPage); Assert.Equal(shape.Placement,a.Document.Objects[0].Placement);
        queue.Focus(); Assert.True(window.HandleShortcut(System.Windows.Input.Key.Z,System.Windows.Input.ModifierKeys.Control|System.Windows.Input.ModifierKeys.Shift));
        Assert.Equal(new RectD(27,28,40,35),a.Document.Objects[0].Placement.BoundsMm);
        b.Copies=4; vm.SetQueueSelection([a,b],b); var pages=vm.Pages.ToArray(); var copies=pages.Select(p=>p.Copies).ToArray(); var flags=pages.Select(p=>p.IsIncluded).ToArray();
        queue.Focus(); Assert.True(window.HandleShortcut(System.Windows.Input.Key.A,System.Windows.Input.ModifierKeys.Control));
        Assert.True(window.HandleShortcut(System.Windows.Input.Key.Delete,System.Windows.Input.ModifierKeys.None)); window.UpdateLayout();
        Assert.Empty(vm.Pages); Assert.True(undo.IsVisible); Assert.True(undo.IsEnabled); Assert.Contains("Удалить",vm.UndoDescription);
        undo.Command.Execute(null); Assert.Equal(pages,vm.Pages); Assert.Equal(copies,pages.Select(p=>p.Copies)); Assert.Equal(flags,pages.Select(p=>p.IsIncluded)); Assert.Equal(pages.Length,queue.SelectedItems.Count);
        redo.Command.Execute(null); Assert.Empty(vm.Pages); window.UpdateLayout(); Assert.True(undo.IsVisible); Assert.True(undo.IsEnabled);
        queue.Focus(); Assert.True(window.HandleShortcut(System.Windows.Input.Key.Z,System.Windows.Input.ModifierKeys.Control)); Assert.Equal(pages,vm.Pages);
        var field=((NumericField)window.FindName("BatchCopiesInput")).Input; field.Focus(); var count=vm.UndoCount; var initial=field.Text;
        System.Windows.Input.ApplicationCommands.SelectAll.Execute(null,field); field.SelectedText="12";
        Assert.False(window.HandleShortcut(System.Windows.Input.Key.Z,System.Windows.Input.ModifierKeys.Control));
        System.Windows.Input.ApplicationCommands.Undo.Execute(null,field); Assert.Equal(initial,field.Text); Assert.Equal(count,vm.UndoCount);
        vm.SelectedPage=a; vm.SetQueueSelection([a],a); await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading); window.UpdateLayout();
        Capture(window,"ux003-history-controls.png");
        var saved=vm.CaptureProject(); await new ProjectFileService().SaveAsync(saved with { Pages=saved.Pages.Where(p=>p.Sheet.Id==a.Id || p.Sheet.Id==b.Id).ToImmutableArray() },Path.Combine(Fixtures.Root,"ux003-demo.printmaster"),true,default);
        window.Width=1180; window.Height=720; window.UpdateLayout(); Capture(window,"ux003-small-window.png");
        Assert.True(undo.IsVisible); Assert.True(redo.IsVisible);
        var root=(FrameworkElement)window.Content;
        Assert.InRange(undo.TranslatePoint(new(0,0),root).X,0,root.ActualWidth-undo.ActualWidth);
        Assert.InRange(redo.TranslatePoint(new(0,0),root).X,0,root.ActualWidth-redo.ActualWidth);
        window.Width=1460; window.Height=940;
    }
    private static async Task VerifyFullColorEditor(MainWindow window,PrintSessionViewModel vm)
    {
        vm.ShowPrintPreview=false; var page=vm.Pages.First(p=>p.IsBlank); vm.SelectedPage=page;
        var text=page.Document.Objects.OfType<TextObject>().FirstOrDefault();
        if(text==null) { text=SheetEditing.CreateText("RGB на листе",new(),new(20,20),page.Document.Paper.SafeAreaMm); page.Document.AddObject(text); }
        vm.SelectedSheetObjectId=text.Id; window.UpdateLayout();
        var colorControls=Descendants<TextColorEditor>((DependencyObject)window.Content).ToArray();
        var control=colorControls.FirstOrDefault(x=>x.IsVisible && x.ColorLabel=="Цвет текста");
        Assert.True(control!=null,$"Visible color controls: {string.Join("; ",colorControls.Select(x=>$"{x.ColorLabel}, visible={x.IsVisible}"))}");
        var field=(TextBox)control.FindName("HexInput"); var palette=(Button)control.FindName("PaletteButton");
        var original=vm.TextColor; var before=vm.UndoCount;
        field.Focus(); field.Text="2a73c5";
        field.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),0,System.Windows.Input.Key.Enter) { RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent });
        Assert.Equal("#2A73C5",vm.TextColor); Assert.Equal(Math.Min(before+1,100),vm.UndoCount);
        field.Text="NOTRGB"; palette.Focus(); window.UpdateLayout();
        Assert.Equal("#2A73C5",vm.TextColor); Assert.True(((TextBlock)control.FindName("ValidationMessage")).IsVisible);
        field.Focus(); field.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),0,System.Windows.Input.Key.Escape) { RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent });
        Assert.Equal("#2A73C5",field.Text);
        control.Picker=new StubColorPicker(null); palette.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(Math.Min(before+1,100),vm.UndoCount); Assert.Equal("#2A73C5",vm.TextColor);
        control.Picker=new StubColorPicker("#31A26F"); palette.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("#31A26F",vm.TextColor); Assert.Equal(Math.Min(before+2,100),vm.UndoCount);
        var swatch=(Border)control.FindName("Swatch"); Assert.Equal(System.Windows.Media.Color.FromRgb(0x31,0xA2,0x6F),((System.Windows.Media.SolidColorBrush)swatch.Background).Color);
        vm.UndoCommand.Execute(null); Assert.Equal("#2A73C5",vm.TextColor); vm.UndoCommand.Execute(null); Assert.Equal(original,vm.TextColor);
        await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading); Capture(window,"qa13-text-palette.png");
    }
    private static T? Descendant<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T found) return found;
        for(var i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++)
        { var child=Descendant<T>(System.Windows.Media.VisualTreeHelper.GetChild(root,i)); if(child!=null) return child; }
        return null;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T found) yield return found;
        for(var i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++)
        foreach(var child in Descendants<T>(System.Windows.Media.VisualTreeHelper.GetChild(root,i))) yield return child;
    }
    private sealed class StubColorPicker(string? result) : ITextColorPicker
    { public string? Pick(IntPtr owner,string current) { Assert.NotEqual(IntPtr.Zero,owner); return result; } }
    private static async Task VerifyWorkspaceAndDialogs(MainWindow window,PrintSessionViewModel vm,TestDialogs dialogs)
    {
        vm.SelectedPage=vm.Pages.First(p=>p.IsBlank);
        await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading);
        window.Width=1460; window.Height=940; window.UpdateLayout(); Capture(window,"ux005-editor-wide.png");
        foreach(var size in new[] { (1180d,720d),(900d,600d),(780d,560d) })
        {
            window.Width=size.Item1; window.Height=size.Item2; window.UpdateLayout(); await Task.Delay(40);
            var print=(Button)window.FindName("PrintButton"); Assert.True(print.IsVisible); Assert.True(print.ActualWidth>100);
            var printTop=print.TransformToAncestor(window).Transform(new Point(0,0));
            Assert.True(printTop.X>=0 && printTop.Y>=0); Assert.True(printTop.X+print.ActualWidth<=window.ActualWidth);
            var canvas=(FrameworkElement)window.FindName("CanvasPane"); Assert.True(canvas.ActualWidth>480); Assert.True(canvas.ActualHeight>280);
            if(!((FrameworkElement)window.FindName("InspectorPane")).IsVisible)
                ((Button)window.FindName("InspectorToggle")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout(); Assert.True(((FrameworkElement)window.FindName("InspectorPane")).IsVisible);
            Assert.True(canvas.ActualWidth>450); Capture(window,$"ux005-editor-{size.Item1:0}.png");
        }
        window.Width=1460; window.Height=940; window.UpdateLayout();
        using(var model=new PrintReviewViewModel([PrintPageItem.CreateBlank(SheetPaper.A4).Snapshot()],
            new PrintSettings { PrinterName="Microsoft Print to PDF" },["Microsoft Print to PDF"]))
        {
            var review=new PrintReviewDialog(model) { Owner=window }; review.Show();
            await WaitUntil(()=>!model.IsLoading); Assert.Empty(model.Error); Assert.NotNull(model.Preview); Assert.True(model.CanSubmit);
            var button=(Button)review.FindName("SubmitPrintButton"); Assert.True(button.IsVisible);
            var place=button.TransformToAncestor(review).Transform(new Point(0,0)); Assert.True(place.Y+button.ActualHeight<review.ActualHeight);
            Capture(review,"ux005-print-dialog.png"); review.Close(); Assert.Null(model.AcceptedSettings);
        }
        var settingsModel=new ApplicationSettingsViewModel(new(),vm.Printers.ToArray(),dialogs);
        var settings=new ApplicationSettingsDialog(settingsModel) { Owner=window }; settings.Show(); settings.UpdateLayout();
        Assert.True(DialogInputValidation.CommitAndCheck(settings)); Capture(settings,"ux005-settings-dialog.png");
        settings.Close(); Assert.Null(settingsModel.Result);
    }
    private static async Task VerifyClipboardMenu(MainWindow window,PrintSessionViewModel vm,FakeClipboard clipboard,TestDialogs dialogs)
    {
        var sheet=(SheetCanvas)window.FindName("SheetEditor"); var queue=(ListBox)window.FindName("QueueList");
        var source=PrintPageItem.CreateBlank(SheetPaper.A4); var target=PrintPageItem.CreateBlank(SheetPaper.A4);
        var text=new TextObject(Guid.NewGuid(),new(new(20,25,130,40)),"Перенос через ПКМ",new("Arial",18,"#175E9C",true));
        var figure=new ShapeObject(Guid.NewGuid(),new(new(25,90,60,40)),SheetShapeKind.Rectangle,new("#111827",0.6));
        source.Document.AddObjects([text,figure]); vm.AddPage(source); vm.AddPage(target); vm.SelectedPage=source;
        await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading); window.UpdateLayout(); vm.SelectAllSheetObjects();
        sheet.SelectClipboardAt(new(35*SheetEditing.DipPerMm,35*SheetEditing.DipPerMm)); Assert.Equal(2,vm.SelectedObjectIds.Length); // Right-click inside the selected group keeps it.
        queue.Focus(); Assert.Equal(ShortcutScope.Queue,window.CurrentShortcutScope); Assert.Equal(ShortcutScope.Sheet,window.ClipboardCommandScope(sheet));
        var menu=window.PrepareCanvasContextMenu(sheet); Assert.NotNull(menu); menu.PlacementTarget=sheet; menu.IsOpen=true; await Task.Delay(50);
        var cut=MenuEntry(menu,"Cut"); var copy=MenuEntry(menu,"Copy"); var paste=MenuEntry(menu,"Paste");
        Assert.Same(sheet,cut.CommandTarget); Assert.Same(sheet,copy.CommandTarget); Assert.Same(sheet,paste.CommandTarget);
        Assert.True(((System.Windows.Input.RoutedCommand)cut.Command).CanExecute(null,cut.CommandTarget)); Capture(window,"ux004-menu-source.png");
        var pages=vm.Pages.Count; var before=source.Document.Snapshot(); ((System.Windows.Input.RoutedCommand)cut.Command).Execute(null,cut.CommandTarget);
        await WaitUntil(()=>!vm.IsBusy && source.Document.Objects.Count==0); menu.IsOpen=false;
        Assert.Equal(pages,vm.Pages.Count); Assert.NotNull(clipboard.Content.SelectionArchive); Assert.Equal(ClipboardSelectionKind.Objects,clipboard.Content.SelectionKind);
        vm.SelectedPage=target; await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading); window.UpdateLayout();
        window.PrepareCanvasContextMenu(sheet); menu.PlacementTarget=sheet; menu.IsOpen=true; await Task.Delay(50);
        ((System.Windows.Input.RoutedCommand)paste.Command).Execute(null,paste.CommandTarget);
        await WaitUntil(()=>!vm.IsBusy && target.Document.Objects.Count==2); menu.IsOpen=false;
        Assert.Equal(text.Text,target.Document.Objects.OfType<TextObject>().Single().Text); Assert.Equal(text.Style,target.Document.Objects.OfType<TextObject>().Single().Style);
        Assert.Equal(2,vm.SelectedObjectIds.Length); vm.UndoCommand.Execute(null); Assert.Empty(target.Document.Objects);
        vm.UndoCommand.Execute(null); Assert.Equal(before,source.Document.Snapshot()); Assert.Same(source,vm.SelectedPage);
        vm.SetObjectSelection([text.Id,figure.Id]); clipboard.WriteError=new IOException("Буфер занят");
        var count=clipboard.Writes; System.Windows.Input.ApplicationCommands.Cut.Execute(null,sheet);
        await WaitUntil(()=>!vm.IsBusy); Assert.Equal(before,source.Document.Snapshot()); Assert.Equal(new[] {text.Id,figure.Id},vm.SelectedObjectIds); Assert.Equal(count,clipboard.Writes);
        Assert.Single(dialogs.Errors); dialogs.Errors.Clear();
        clipboard.WriteError=null;
        vm.SetObjectSelection([text.Id,figure.Id]); sheet.SelectClipboardAt(new(180*SheetEditing.DipPerMm,240*SheetEditing.DipPerMm)); Assert.Empty(vm.SelectedObjectIds);
        window.PrepareCanvasContextMenu(sheet); menu.PlacementTarget=sheet; menu.IsOpen=true; await Task.Delay(50);
        Assert.False(((System.Windows.Input.RoutedCommand)cut.Command).CanExecute(null,cut.CommandTarget)); Assert.True(((System.Windows.Input.RoutedCommand)paste.Command).CanExecute(null,paste.CommandTarget)); menu.IsOpen=false;
        var session=vm.CaptureProject();
        await new ProjectFileService().SaveAsync(session with { Pages=session.Pages.Where(p=>p.Sheet.Id==source.Id || p.Sheet.Id==target.Id).ToImmutableArray() },Path.Combine(Fixtures.Root,"ux004-demo.printmaster"),true,default);
        await WaitUntil(()=>vm.EditorPreview!=null && !vm.IsPreviewLoading); window.UpdateLayout(); Capture(window,"ux004-transfer.png");
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        var limit=DateTime.UtcNow.AddSeconds(20);
        while(!condition()) { if(DateTime.UtcNow>limit) throw new TimeoutException("WPF preview timed out."); await Task.Delay(25); }
    }
    private static Task OnSta(Func<Task> body)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            SetThreadDpiAwarenessContext(new nint(-4));
            var dispatcher=Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async ()=>{ try { await body(); completion.SetResult(); } catch(Exception ex) { completion.SetException(ex); } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } });
            Dispatcher.Run();
        }) { IsBackground=true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    private sealed class TestDialogs : IUserDialogs
    {
        public string? SavePath { get; set; } public string? OpenPath { get; set; } public bool RestoreRecovery { get; set; } public string? ReplacementPath { get; set; }
        public SheetPaper? NewSheet { get; set; } public string[] SheetImages { get; set; }=[];
        public Func<ExportViewModel,bool>? ExportReview { get; set; }
        public bool ReviewExport(ExportViewModel model)=>ExportReview?.Invoke(model) ?? false;
        public bool ConfirmExportOverwrite(IReadOnlyList<string> files)=>false;
        public SheetPaper? PickNewSheet(PrintSettings settings)=>NewSheet;
        public string[] PickSheetImages()=>SheetImages;
        public string? PickProjectToSave(string suggestedName)=>SavePath; public string? PickProjectToOpen()=>OpenPath;
        public bool ConfirmRecovery(string path)=>RestoreRecovery;
        public string? PickMissingSource(MissingProjectSource source)=>ReplacementPath;
        public List<string> Errors { get; }=new(); public string[] PickFiles()=>[]; public string? PickPdfOutput()=>null; public string? PickLibreOffice()=>null;
        public bool ConfirmCropping(int count)=>false; public void Error(string message)=>Errors.Add(message);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        for(var i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);i++)
        {
            var child=System.Windows.Media.VisualTreeHelper.GetChild(node,i); yield return child;
            foreach(var nested in Descendants(child)) yield return nested;
        }
    }
}
