using PrintMaster.Models;
using PrintMaster.ViewModels;
using PrintMaster.Services;
using System.IO;
using System.Security.Cryptography;
using Xunit;

namespace PrintMaster.Tests;

public sealed class PhotoInsertionTests
{
    [Fact]
    public async Task UndoReloadsTheSourcePreviewAfterAComposedPreviewWasAlreadyDisplayed()
    {
        var clipboard=new FakeClipboard { Content=new([],ClipboardTests.Image()) }; var dialogs=new ClipboardTests.TestDialogs();
        var vm=ClipboardTests.CreateVm(dialogs,clipboard);
        async Task WaitPreview()
        {
            var limit=DateTime.UtcNow.AddSeconds(10);
            while(vm.EditorPreview==null || vm.IsPreviewLoading) { if(DateTime.UtcNow>limit) throw new TimeoutException("Preview timeout"); await Task.Delay(20); }
        }
        try
        {
            await vm.ImportFilesAsync([Fixtures.CreateImage("ux001-mode-switch.png")]); await WaitPreview();
            var original=vm.EditorPreview!;
            await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto); await WaitPreview();
            Assert.NotEqual(original.PixelWidth,vm.EditorPreview!.PixelWidth);
            vm.UndoCommand.Execute(null); await WaitPreview();
            Assert.False(vm.IsSheetSelected); Assert.Equal(original.PixelWidth,vm.EditorPreview!.PixelWidth); Assert.Equal(original.PixelHeight,vm.EditorPreview.PixelHeight);
            Assert.Empty(dialogs.Errors);
        }
        finally { await vm.PrepareCloseAsync(); vm.Dispose(); }
    }
    [Fact]
    public async Task ClipboardPhotoIsInsertedIntoTheImportedPhotoWithoutAddingAQueuePage()
    {
        var path=Fixtures.CreateImage("ux001-original.png"); var hash=SHA256.HashData(File.ReadAllBytes(path));
        var clipboard=new FakeClipboard { Content=new([],ClipboardTests.Image()) }; var dialogs=new ClipboardTests.TestDialogs();
        var vm=ClipboardTests.CreateVm(dialogs,clipboard);
        try
        {
            await vm.ImportFilesAsync([path]); var page=vm.SelectedPage!;
            page.Edits.SetCrop(new(100,60,600,480)); page.RotationQuarterTurns=1;
            page.Edits.Add(Annotation.Create(AnnotationKind.Arrow,new(120,100),new(420,200),"#175E9C",4));
            var before=page.Document.Snapshot();
            await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto);
            Assert.Single(vm.Pages); Assert.Same(page,vm.SelectedPage); Assert.Equal(2,page.Document.Objects.Count);
            Assert.True(vm.IsSheetSelected); Assert.Equal(before.PrimaryContent!.Edits,page.Document.PrimaryContent!.Edits);
            Assert.Equal(before.PrimaryAsset,page.Document.PrimaryAsset); Assert.Equal(hash,SHA256.HashData(File.ReadAllBytes(path)));
            vm.UndoCommand.Execute(null); Assert.Equal(before,page.Document.Snapshot());
            vm.RedoCommand.Execute(null); Assert.Equal(2,page.Document.Objects.Count);
            Assert.Empty(dialogs.Errors);
        }
        finally { await vm.PrepareCloseAsync(); vm.Dispose(); }
    }
    [Fact]
    public async Task CompositionKeepsA5LandscapeAndSurvivesPortableProjectReload()
    {
        var clipboard=new FakeClipboard { Content=new([],ClipboardTests.Image()) }; var dialogs=new ClipboardTests.TestDialogs();
        var vm=ClipboardTests.CreateVm(dialogs,clipboard); var path=ProjectFileTests.PathFor("ux001-portable");
        try
        {
            await vm.ImportFilesAsync([Fixtures.CreateImage("ux001-portable-source.png")]);
            vm.Settings.Paper=PaperPreset.A5; vm.Settings.Landscape=true; var page=vm.SelectedPage!;
            await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto);
            Assert.Equal(new SizeD(210,148),page.Document.Paper.OrientedSizeMm);
            var photo=(ContentObject)page.Document.Objects.Last(); vm.ObjectWidth=60;
            Assert.Equal(60,page.Document.Objects.Last().Placement.BoundsMm.Width);
            Assert.True(vm.RotateCommand.CanExecute(null)); vm.RotateCommand.Execute(null);
            Assert.Equal(1,((ContentObject)page.Document.Objects.Last()).Edits.QuarterTurns);
            Assert.Equal(60,page.Document.Objects.Last().Placement.BoundsMm.Height);
            Assert.True(await vm.SaveProjectToAsync(path)); Assert.Empty(dialogs.Errors);
        }
        finally { await vm.PrepareCloseAsync(); vm.Dispose(); }
        var reopened=ClipboardTests.CreateVm(dialogs,new FakeClipboard());
        try
        {
            Assert.True(await reopened.OpenProjectFromAsync(path)); Assert.True(reopened.IsSheetSelected);
            Assert.Equal(new SizeD(210,148),reopened.SelectedPage!.Document.Paper.OrientedSizeMm);
            Assert.Equal(2,reopened.SelectedPage.Document.Objects.Count); Assert.False(reopened.SelectedPage.HasMissingSources);
            Assert.True(reopened.Settings.Landscape); Assert.Equal(PaperPreset.A5,reopened.Settings.Paper); Assert.Empty(dialogs.Errors);
        }
        finally { await reopened.PrepareCloseAsync(); reopened.Dispose(); }
    }
    [Fact]
    public async Task DroppingPhotosOnCanvasAndQueueHasDifferentResultsAndInvalidFilesKeepTheSheet()
    {
        var dialogs=new ClipboardTests.TestDialogs(); var vm=ClipboardTests.CreateVm(dialogs,new FakeClipboard());
        try
        {
            await vm.ImportFilesAsync([Fixtures.CreateImage("ux001-drop-base.png")]); var page=vm.SelectedPage!;
            var source=Fixtures.CreateImage("ux001-drop-overlay.jpg");
            await vm.HandleFileDropAsync([source],true,new PointD(160,220));
            Assert.Single(vm.Pages); Assert.Same(page,vm.SelectedPage); Assert.Equal(2,page.Document.Objects.Count);
            var overlay=page.Document.Objects.Last(); Assert.False(SheetEditing.Outside(overlay,vm.SafeAreaMm));
            Assert.True(overlay.Placement.BoundsMm.Y>vm.SafeAreaMm.Y);
            var composed=page.Document.Snapshot();
            await vm.HandleFileDropAsync([Path.Combine(Fixtures.Root,"does-not-exist.png")],true);
            Assert.Equal(composed,page.Document.Snapshot()); Assert.Single(dialogs.Errors); dialogs.Errors.Clear();
            await vm.HandleFileDropAsync([source],false); Assert.Equal(2,vm.Pages.Count); Assert.Equal(composed,page.Document.Snapshot());
            vm.SelectedPage=page;
            await vm.HandleFileDropAsync([Fixtures.CreatePdf("ux001-drop-doc.pdf",1)],true);
            Assert.Equal(3,vm.Pages.Count); Assert.Equal(composed,page.Document.Snapshot()); vm.SelectedPage=vm.Pages.Last(); Assert.True(vm.IsDocumentSelected); Assert.False(vm.CanEdit);
            Assert.Empty(dialogs.Errors);
        }
        finally { await vm.PrepareCloseAsync(); vm.Dispose(); }
    }
    [Fact]
    public async Task DocumentAndEmptyCanvasFallbackToSeparatePagesAndQueueObjectPasteRespectsDestination()
    {
        var clipboard=new FakeClipboard { Content=new([],ClipboardTests.Image()) }; var dialogs=new ClipboardTests.TestDialogs();
        var vm=ClipboardTests.CreateVm(dialogs,clipboard);
        try
        {
            await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto); Assert.Single(vm.Pages);
            await vm.ImportFilesAsync([Fixtures.CreatePdf("ux001-readonly.pdf",1)]); vm.SelectedPage=vm.Pages.Last();
            var document=vm.SelectedPage!; var before=document.Document.Snapshot();
            await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto); Assert.Equal(3,vm.Pages.Count); Assert.Equal(before,document.Document.Snapshot());
            await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto); Assert.Equal(3,vm.Pages.Count);
            var sheet=vm.SelectedPage!; var state=sheet.Document.Snapshot(); await vm.CopySelectionCommand.ExecuteAsync(null);
            await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Queue);
            Assert.Equal(4,vm.Pages.Count); Assert.Equal(state,sheet.Document.Snapshot()); Assert.Single(vm.SelectedPage!.Document.Objects); Assert.Empty(dialogs.Errors);
        }
        finally { await vm.PrepareCloseAsync(); vm.Dispose(); }
    }
    [Fact]
    public async Task FrozenOriginalRendersTheSameAndDoesNotSilentlyAcceptDifferentPaperForPrinting()
    {
        var clipboard=new FakeClipboard { Content=new([],ClipboardTests.Image()) }; var dialogs=new ClipboardTests.TestDialogs();
        var vm=ClipboardTests.CreateVm(dialogs,clipboard);
        try
        {
            await vm.ImportFilesAsync([Fixtures.CreateImage("ux001-raster.png")]); var page=vm.SelectedPage!;
            page.Edits.SetCrop(new(40,60,800,500)); page.RotationQuarterTurns=1;
            page.Edits.Add(Annotation.Create(AnnotationKind.Rectangle,new(100,100),new(500,300),"#175E9C",6));
            var before=page.Snapshot(); var safe=vm.SafeAreaMm;
            var height=(int)Math.Round(400*safe.Height/safe.Width);
            var rasterizer=new PageRasterizer(new PdfiumRenderer()); var destination=PageLayout.Place(before.ContentSize,new RectD(0,0,400,height),ScaleMode.Fit);
            using var original=rasterizer.RenderViewport(before,400,height,destination,false,default,safe);
            Assert.Equal(System.Drawing.Color.White.ToArgb(),original.GetPixel(5,height/2).ToArgb());
            await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto);
            var alone=page.Snapshot() with { Sheet=page.Document.Snapshot() with { Objects=[page.Document.PrimaryContent!] } };
            using var frozen=rasterizer.RenderViewport(alone,400,height,destination,false,default,safe);
            original.Save(Path.Combine(Fixtures.Root,"ux001-original-raster.png")); frozen.Save(Path.Combine(Fixtures.Root,"ux001-composed-raster.png"));
            double difference=0; var samples=0;
            for(var y=10;y<height-10;y+=8) for(var x=10;x<390;x+=8)
            {
                var a=original.GetPixel(x,y); var b=frozen.GetPixel(x,y);
                difference+=Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B); samples+=3;
            }
            Assert.True(difference/samples<2,$"Mean rendering difference: {difference/samples:F3}");
            vm.AddPage(PrintPageItem.CreateBlank(new(new(148,210),false,SheetMargins.SafeDefault))); vm.SelectedPage=vm.Pages.Last();
            Assert.True(vm.HasSheetPaperMismatch); Assert.True(vm.PrintCommand.CanExecute(null));
            using var review=new PrintReviewViewModel(vm.Pages.Where(p=>p.IsIncluded).Select(p=>p.Snapshot()),vm.Settings,vm.Printers.ToArray());
            review.Settings.PrinterName="Microsoft Print to PDF";
            Assert.Contains("Размер бумаги",review.Error); Assert.False(review.TryAccept(true)); Assert.Empty(dialogs.Errors);
        }
        finally { await vm.PrepareCloseAsync(); vm.Dispose(); }
    }
}
