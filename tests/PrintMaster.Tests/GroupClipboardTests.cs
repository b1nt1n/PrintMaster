using PrintMaster.Models;
using PrintMaster.Services;
using System.IO;
using Xunit;

namespace PrintMaster.Tests;

public sealed class GroupClipboardTests
{
    [Fact]
    public async Task CuttingObjectsTransfersThemToAnotherSheetAndEachSheetCanUndoItsOwnOperation()
    {
        var dialogs=new ClipboardTests.TestDialogs(); var clipboard=new FakeClipboard { Content=new([],ClipboardTests.Image()) };
        using var vm=ClipboardTests.CreateVm(dialogs,clipboard);
        var original=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(original);
        await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto);
        var text=new TextObject(Guid.NewGuid(),new(new(15,25,80,30)),"Перенос",new("Arial",16,"#175E9C",true)); original.Document.AddObject(text);
        var target=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(target);
        var before=original.Document.Snapshot(); vm.SelectAllSheetObjects(); await vm.CopyContextAsync(false,true);
        Assert.Empty(original.Document.Objects); Assert.Equal(2,vm.Pages.Count); Assert.NotNull(clipboard.Content.SelectionArchive);
        vm.SelectedPage=target; await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto);
        Assert.Equal(2,target.Document.Objects.Count);
        var copiedText=target.Document.Objects.OfType<TextObject>().Single(); Assert.Equal("Перенос",copiedText.Text); Assert.Equal(text.Style,copiedText.Style); Assert.NotEqual(text.Id,copiedText.Id);
        var asset=target.Document.Snapshot().Assets.Single(); Assert.True(File.Exists(asset.WorkingPath));
        vm.UndoCommand.Execute(null); Assert.Empty(target.Document.Objects); Assert.Empty(target.Document.Snapshot().Assets);
        vm.SelectedPage=original; vm.UndoCommand.Execute(null); Assert.Equal(before,original.Document.Snapshot()); Assert.Equal(2,vm.Pages.Count); Assert.Empty(dialogs.Errors);
    }
    [Fact]
    public async Task ObjectGroupSurvivesSourceSessionClosingAndPastesAsOneUndoStep()
    {
        var dialogs=new ClipboardTests.TestDialogs(); var clipboard=new FakeClipboard { Content=new([],ClipboardTests.Image()) };
        Guid photoId,textId; TextStyle style=new("Arial",17,"#175E9C",true,true,PrintMaster.Models.TextAlignment.Right,1.4);
        using(var source=ClipboardTests.CreateVm(dialogs,clipboard))
        {
            source.AddPage(PrintPageItem.CreateBlank(SheetPaper.A4)); await source.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto);
            var page=source.SelectedPage!; var photo=(ContentObject)page.Document.Objects.Single();
            var edits=photo.Edits with { Crop=new(120,160,500,600),QuarterTurns=3,Annotations=[Annotation.Create(AnnotationKind.Arrow,new(200,210),new(400,420),"#123456",6)] };
            page.Document.ReplaceObject(photo with { Edits=edits });
            var text=new TextObject(Guid.NewGuid(),new(new(17,23,80,30)),"Группа\nс оформлением",style);
            page.Document.AddObject(text); photoId=photo.Id; textId=text.Id; source.SetObjectSelection([photo.Id,text.Id]);
            await source.CopyContextAsync(false); Assert.NotNull(clipboard.Content.SelectionArchive);
            Assert.Equal(ClipboardSelectionKind.Objects,clipboard.Content.SelectionKind); Assert.Equal(1,clipboard.Writes);
        }
        using var target=ClipboardTests.CreateVm(dialogs,clipboard); var blank=PrintPageItem.CreateBlank(SheetPaper.A4); target.AddPage(blank);
        var before=blank.Document.Snapshot(); await target.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto);
        Assert.Single(target.Pages); Assert.Equal(2,blank.Document.Objects.Count); Assert.Equal(2,target.SelectedObjectIds.Length);
        var copiedPhoto=Assert.IsType<ContentObject>(blank.Document.Objects[0]); var copiedText=Assert.IsType<TextObject>(blank.Document.Objects[1]);
        Assert.NotEqual(photoId,copiedPhoto.Id); Assert.NotEqual(textId,copiedText.Id);
        Assert.Equal(new RectD(120,160,500,600),copiedPhoto.Edits.Crop); Assert.Equal(3,copiedPhoto.Edits.QuarterTurns);
        Assert.Equal("#123456",copiedPhoto.Edits.Annotations.Single().Color); Assert.Equal(new PointD(200,210),copiedPhoto.Edits.Annotations.Single().Start);
        Assert.Equal("Группа\nс оформлением",copiedText.Text); Assert.Equal(style,copiedText.Style); Assert.Equal(20,copiedText.Placement.BoundsMm.X);
        var material=Assert.Single(blank.Document.Snapshot().Assets); Assert.Equal(material.Id,copiedPhoto.AssetId); Assert.True(File.Exists(material.WorkingPath));
        using(var bitmap=System.Drawing.Image.FromFile(material.WorkingPath!)) { Assert.Equal(2,bitmap.Width); Assert.Equal(2,bitmap.Height); }
        target.UndoCommand.Execute(null); Assert.Equal(before,blank.Document.Snapshot());
        target.RedoCommand.Execute(null); Assert.Equal(new[] {copiedPhoto.Id,copiedText.Id},blank.Document.Objects.Select(o=>o.Id));
        Assert.Empty(dialogs.Errors);
    }
    [Fact]
    public async Task QueueCutAndPasteRetainPaperCopiesInclusionAndIndependentIdentity()
    {
        var clipboard=new FakeClipboard(); var dialogs=new ClipboardTests.TestDialogs(); using var vm=ClipboardTests.CreateVm(dialogs,clipboard);
        await vm.ImportFilesAsync([Fixtures.CreateImage("ux002-queue-copy.png")]);
        var image=vm.Pages[0]; image.Copies=6; image.Edits.SetCrop(new(100,80,700,600)); image.RotationQuarterTurns=1;
        var notebook=PrintPageItem.CreateBlank(new(new(165,205),true,SheetMargins.SafeDefault)); notebook.IsIncluded=false; notebook.Copies=2;
        notebook.Document.AddObject(new TextObject(Guid.NewGuid(),new(new(10,10,80,30)),"Тетрадь",new())); vm.AddPage(notebook);
        vm.SetQueueSelection([image,notebook],notebook); await vm.CopyContextAsync(true,true);
        Assert.Empty(vm.Pages); Assert.Equal(ClipboardSelectionKind.Pages,clipboard.Content.SelectionKind); Assert.True(vm.UndoQueueCommand.CanExecute(null));
        vm.UndoQueueCommand.Execute(null); Assert.Equal(new[] {image,notebook},vm.Pages); Assert.Equal(6,image.Copies); Assert.False(notebook.IsIncluded);
        await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Queue); Assert.Equal(4,vm.Pages.Count);
        var copies=vm.HighlightedPages.ToArray(); Assert.Equal(2,copies.Length);
        Assert.NotEqual(image.Id,copies[0].Id); Assert.Equal(new RectD(100,80,700,600),copies[0].Edits.Crop); Assert.Equal(1,copies[0].RotationQuarterTurns); Assert.Equal(6,copies[0].Copies);
        Assert.NotEqual(image.Document.PrimaryAsset!.Id,copies[0].Document.PrimaryAsset!.Id); Assert.True(File.Exists(copies[0].Document.PrimaryAsset!.WorkingPath));
        Assert.Equal(new SizeD(165,205),copies[1].Document.Paper.SizeMm); Assert.True(copies[1].Document.Paper.Landscape); Assert.False(copies[1].IsIncluded); Assert.Equal(2,copies[1].Copies);
        vm.UndoQueueCommand.Execute(null); Assert.Equal(new[] {image,notebook},vm.Pages);
        vm.RedoQueueCommand.Execute(null); Assert.Equal(4,vm.Pages.Count); Assert.Equal(copies,vm.HighlightedPages); Assert.Empty(dialogs.Errors);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FailedClipboardWriteNeverCutsPagesOrObjects(bool queue)
    {
        var clipboard=new FakeClipboard { WriteError=new IOException("Занято") }; var dialogs=new ClipboardTests.TestDialogs(); using var vm=ClipboardTests.CreateVm(dialogs,clipboard);
        var page=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(page);
        page.Document.AddObject(new TextObject(Guid.NewGuid(),new(new(10,10,80,30)),"Сохранить меня",new()));
        vm.SetObjectSelection(page.Document.Objects.Select(o=>o.Id)); vm.SetQueueSelection([page]);
        var before=page.Document.Snapshot(); await vm.CopyContextAsync(queue,true);
        Assert.Single(vm.Pages); Assert.Equal(before,page.Document.Snapshot()); Assert.Equal(0,clipboard.Writes); Assert.Single(dialogs.Errors); Assert.False(vm.IsBusy);
    }
    [Fact]
    public async Task InvalidGroupAndExternalOnlyMaterialsLeaveCurrentSheetUntouched()
    {
        var clipboard=new FakeClipboard { Content=new([],SelectionArchive:[1,2,3,4]) }; var dialogs=new ClipboardTests.TestDialogs(); using var vm=ClipboardTests.CreateVm(dialogs,clipboard);
        var page=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(page); var before=page.Document.Snapshot();
        await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto); Assert.Single(dialogs.Errors); Assert.Equal(before,page.Document.Snapshot()); Assert.Single(vm.Pages); dialogs.Errors.Clear();
        using var source=ClipboardTests.CreateVm(new(),new()); await source.ImportFilesAsync([Fixtures.CreateImage("ux002-linked.png")]);
        var linked=ProjectFileTests.PathFor("ux002-linked"); await new ProjectFileService().SaveAsync(source.CaptureProject(),linked,false,default);
        clipboard.Content=new([],SelectionArchive:await File.ReadAllBytesAsync(linked),SelectionKind:ClipboardSelectionKind.Pages);
        await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Queue);
        Assert.Single(dialogs.Errors); Assert.Equal(before,page.Document.Snapshot()); Assert.Single(vm.Pages); Assert.False(vm.IsBusy);
    }
    [Fact]
    public async Task GroupPasteOnAnOriginalPhotoFreezesLayoutAndUndoRestoresOriginal()
    {
        var dialogs=new ClipboardTests.TestDialogs(); var clipboard=new FakeClipboard(); using var vm=ClipboardTests.CreateVm(dialogs,clipboard);
        var blank=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(blank);
        blank.Document.AddObjects([new TextObject(Guid.NewGuid(),new(new(15,20,80,30)),"Подпись",new()),new ShapeObject(Guid.NewGuid(),new(new(80,80,40,40)),SheetShapeKind.Ellipse,new())]);
        vm.SelectAllSheetObjects(); await vm.CopyContextAsync(false);
        await vm.ImportFilesAsync([Fixtures.CreateImage("ux002-group-target.png")]); vm.SelectedPage=vm.Pages.Last(); var photo=vm.SelectedPage;
        photo.Edits.SetCrop(new(120,100,600,500)); photo.RotationQuarterTurns=2; var before=photo.Document.Snapshot();
        await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto); Assert.Equal(2,vm.Pages.Count); Assert.Equal(3,photo.Document.Objects.Count); Assert.True(photo.IsSheetLayout);
        Assert.Equal(before.PrimaryContent!.Edits,photo.Document.PrimaryContent!.Edits); Assert.Equal(PlacementMode.Fixed,photo.Document.PrimaryContent.Placement.Mode);
        vm.UndoCommand.Execute(null); Assert.Equal(before,photo.Document.Snapshot()); Assert.False(photo.IsSheetLayout); Assert.Empty(dialogs.Errors);
    }
}
