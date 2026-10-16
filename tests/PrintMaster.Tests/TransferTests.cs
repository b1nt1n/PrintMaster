using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.IO;
using System.Collections.Immutable;
using System.Security.Cryptography;
using Xunit;

namespace PrintMaster.Tests;

public sealed class TransferTests
{
    private sealed class TransferClipboard : IClipboardService
    {
        public ClipboardSnapshot Content { get; private set; }=ClipboardSnapshot.Empty;
        public int Writes { get; private set; }
        public Func<CancellationToken,Task>? AfterWrite { get; set; }
        public Func<CancellationToken,Task>? BeforeRead { get; set; }
        public async Task<ClipboardSnapshot> ReadAsync(CancellationToken token)
        { if(BeforeRead!=null) await BeforeRead(token); token.ThrowIfCancellationRequested(); return Content; }
        public async Task WriteAsync(ClipboardSnapshot content,CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Content=content; Writes++;
            if(AfterWrite!=null) await AfterWrite(token);
        }
    }
    private static TextObject Text(string text="Перенос")=>new(Guid.NewGuid(),new(new(15,20,100,30)),text,new("Arial",18,"#175E9C",true));
    private static PrintSessionViewModel Session(TransferClipboard clipboard,ClipboardTests.TestDialogs dialogs)
        =>new(dialogs,Path.Combine(Fixtures.Root,"ux004-"+Guid.NewGuid().ToString("N")),clipboard);
    [Fact]
    public async Task SwitchingToDestinationWhileCopyingStillCutsCapturedSourceOnly()
    {
        var clipboard=new TransferClipboard(); var dialogs=new ClipboardTests.TestDialogs(); using var vm=Session(clipboard,dialogs);
        var source=PrintPageItem.CreateBlank(SheetPaper.A4); var destination=PrintPageItem.CreateBlank(SheetPaper.A4);
        var first=Text(); var target=Text("Оставить на другом листе"); source.Document.AddObject(first); destination.Document.AddObject(target);
        vm.AddPage(source); vm.AddPage(destination); vm.SetObjectSelection([first.Id]);
        clipboard.AfterWrite=_=>{ vm.SelectedPage=destination; vm.SetObjectSelection([target.Id]); return Task.CompletedTask; };
        await vm.CopyContextAsync(false,true);
        Assert.Empty(source.Document.Objects); Assert.Equal(target,Assert.Single(destination.Document.Objects)); Assert.Same(destination,vm.SelectedPage);
        Assert.Equal(new[] {target.Id},vm.SelectedObjectIds); Assert.Equal(1,clipboard.Writes);
        vm.UndoCommand.Execute(null); Assert.Equal(first,Assert.Single(source.Document.Objects)); Assert.Empty(dialogs.Errors);
    }
    [Fact]
    public async Task MissingQueueMaterialNeverCutsAnUnpasteablePage()
    {
        var clipboard=new TransferClipboard(); var dialogs=new ClipboardTests.TestDialogs(); using var vm=Session(clipboard,dialogs);
        var asset=new SourceAsset(Guid.NewGuid(),SourceKind.Image,"missing.png",null,0,1,new(1000,800),new(100,80));
        var page=PrintPageItem.FromSource(asset); vm.AddPage(page); vm.SetQueueSelection([page]);
        var before=page.Document.Snapshot(); var undo=vm.UndoCount; await vm.CopyContextAsync(true,true);
        Assert.Same(page,Assert.Single(vm.Pages)); Assert.Equal(before,page.Document.Snapshot()); Assert.Equal(undo,vm.UndoCount);
        Assert.Equal(0,clipboard.Writes); Assert.Single(dialogs.Errors);
    }
    [Fact]
    public async Task ChangedQueueSourceDuringClipboardWriteRemainsInQueue()
    {
        var clipboard=new TransferClipboard(); var dialogs=new ClipboardTests.TestDialogs(); using var vm=Session(clipboard,dialogs);
        var page=PrintPageItem.CreateBlank(SheetPaper.A4); page.Document.AddObject(Text()); vm.AddPage(page); vm.SetQueueSelection([page]);
        clipboard.AfterWrite=_=>{ page.Copies=7; return Task.CompletedTask; }; await vm.CopyContextAsync(true,true);
        Assert.Same(page,Assert.Single(vm.Pages)); Assert.Equal(7,page.Copies); Assert.Equal("Перенос",page.Document.Objects.OfType<TextObject>().Single().Text); Assert.False(vm.IsBusy);
    }
    [Fact]
    public async Task CancellationAfterClipboardWriteDoesNotCutTheObject()
    {
        var clipboard=new TransferClipboard(); var dialogs=new ClipboardTests.TestDialogs(); using var vm=Session(clipboard,dialogs);
        var page=PrintPageItem.CreateBlank(SheetPaper.A4); var item=Text(); page.Document.AddObject(item); vm.AddPage(page); vm.SetObjectSelection([item.Id]);
        var count=vm.UndoCount; clipboard.AfterWrite=_=>{ vm.CancelCommand.Execute(null); return Task.CompletedTask; };
        await vm.CopyContextAsync(false,true); Assert.Equal(item,Assert.Single(page.Document.Objects)); Assert.Equal(count,vm.UndoCount); Assert.False(vm.IsBusy); Assert.Empty(dialogs.Errors);
    }
    [Fact]
    public async Task PasteKeepsItsDestinationIfPageChangesDuringClipboardRead()
    {
        var clipboard=new TransferClipboard(); var dialogs=new ClipboardTests.TestDialogs(); using var vm=Session(clipboard,dialogs);
        var source=PrintPageItem.CreateBlank(SheetPaper.A4); var destination=PrintPageItem.CreateBlank(SheetPaper.A4); var other=PrintPageItem.CreateBlank(SheetPaper.A4);
        var item=Text(); source.Document.AddObject(item); vm.AddPage(source); vm.AddPage(destination); vm.AddPage(other);
        vm.SetObjectSelection([item.Id]); await vm.CopyContextAsync(false); vm.SelectedPage=destination;
        clipboard.BeforeRead=_=>{ vm.SelectedPage=other; return Task.CompletedTask; };
        await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto);
        Assert.Equal("Перенос",destination.Document.Objects.OfType<TextObject>().Single().Text); Assert.Empty(other.Document.Objects); Assert.Same(destination,vm.SelectedPage); Assert.Empty(dialogs.Errors);
    }
    [Fact]
    public async Task ChangedObjectDuringWriteIsKeptAndCopiedDataStillContainsItsOldVersion()
    {
        var clipboard=new TransferClipboard(); var dialogs=new ClipboardTests.TestDialogs(); using var vm=Session(clipboard,dialogs);
        var page=PrintPageItem.CreateBlank(SheetPaper.A4); var item=Text(); page.Document.AddObject(item); vm.AddPage(page); vm.SetObjectSelection([item.Id]);
        clipboard.AfterWrite=_=>{ page.Document.ReplaceObject(item with { Text="Новая правка" }); return Task.CompletedTask; };
        Assert.False(await vm.CopyContextAsync(false,true)); Assert.Equal("Новая правка",page.Document.Objects.OfType<TextObject>().Single().Text);
        Assert.Equal("Перенос",((TextObject)ClipboardObjectService.Deserialize(clipboard.Content.ObjectJson!).Object!).Text); Assert.Empty(dialogs.Errors);
    }
    [Fact]
    public async Task PublicCopyCommandCopiesTheWholeSelection()
    {
        var clipboard=new TransferClipboard(); var dialogs=new ClipboardTests.TestDialogs(); using var vm=Session(clipboard,dialogs);
        var page=PrintPageItem.CreateBlank(SheetPaper.A4); page.Document.AddObjects([Text("Первый"),Text("Второй")]); vm.AddPage(page); vm.SelectAllSheetObjects();
        await vm.CopySelectionCommand.ExecuteAsync(null); Assert.NotNull(clipboard.Content.SelectionArchive); Assert.Equal(ClipboardSelectionKind.Objects,clipboard.Content.SelectionKind);
        var target=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(target); vm.SelectedPage=target; await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto);
        Assert.Equal(new[] {"Первый","Второй"},target.Document.Objects.OfType<TextObject>().Select(t=>t.Text)); Assert.Empty(dialogs.Errors);
    }
    [Fact]
    public async Task OversizedAndCorruptQueueMaterialsNeverReachTheClipboardOrRemovePages()
    {
        var clipboard=new TransferClipboard(); var dialogs=new ClipboardTests.TestDialogs(); using var vm=Session(clipboard,dialogs);
        var path=Path.Combine(Fixtures.Root,"ux004-oversized.bmp");
        using(var file=File.Create(path)) file.SetLength(ClipboardService.MaxSelectionBytes+1L);
        var page=PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Image,path,path,0,1,new(1000,800),new(100,80))); vm.AddPage(page); vm.SetQueueSelection([page]);
        Assert.False(await vm.CopyContextAsync(true,true)); Assert.Same(page,Assert.Single(vm.Pages)); Assert.Equal(0,clipboard.Writes); Assert.Single(dialogs.Errors); dialogs.Errors.Clear();
        File.WriteAllBytes(path,[1,2,3,4]); Assert.False(await vm.CopyContextAsync(true,true));
        Assert.Same(page,Assert.Single(vm.Pages)); Assert.Equal(0,clipboard.Writes); Assert.Single(dialogs.Errors); Assert.False(vm.IsBusy);
    }
    [Fact]
    public async Task PhotoTextShapesAndInkTransferWithoutRasterizingTheOriginal()
    {
        var clipboard=new TransferClipboard(); var dialogs=new ClipboardTests.TestDialogs(); using var vm=Session(clipboard,dialogs);
        var image=Fixtures.CreateImage("ux004-quality.png"); var hash=SHA256.HashData(File.ReadAllBytes(image));
        await vm.ImportFilesAsync([image]); var page=vm.SelectedPage!; page.Edits.SetCrop(new(100,75,700,600)); page.RotationQuarterTurns=3;
        var annotation=Annotation.Create(AnnotationKind.Arrow,new(180,180),new(400,350),"#175E9C",5); page.Edits.Add(annotation);
        var content=page.Document.PrimaryContent!; var text=Text("Текст со стилем");
        page.Document.InsertIntoLayout(text,null,SheetPaper.A4,new(12,15,150,100));
        var shape=new ShapeObject(Guid.NewGuid(),new(new(30,120,60,40),27),SheetShapeKind.Ellipse,new("#C62828",0.8,0.7,"#EEEEEE"));
        var ink=new InkObject(Guid.NewGuid(),new(new(100,120,50,30)),[new(0,0),new(0.4,0.8),new(1,1)],new("#008000",1.1,0.3));
        page.Document.AddObjects([shape,ink]); vm.SelectAllSheetObjects(); var before=page.Document.Snapshot();
        Assert.True(await vm.CopyContextAsync(false,true)); Assert.Empty(page.Document.Objects);
        var target=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(target); vm.SelectedPage=target; var empty=target.Document.Snapshot();
        await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto); Assert.Equal(4,target.Document.Objects.Count);
        var copied=target.Document.Objects.OfType<ContentObject>().Single();
        Assert.NotEqual(content.Id,copied.Id); Assert.Equal(new RectD(100,75,700,600),copied.Edits.Crop); Assert.Equal(3,copied.Edits.QuarterTurns);
        Assert.NotEqual(annotation.Id,copied.Edits.Annotations.Single().Id); Assert.Equal(annotation.Start,copied.Edits.Annotations.Single().Start); Assert.Equal(annotation.Color,copied.Edits.Annotations.Single().Color);
        Assert.Equal(text.Style,target.Document.Objects.OfType<TextObject>().Single().Style); Assert.Equal(shape.Style,target.Document.Objects.OfType<ShapeObject>().Single().Style);
        Assert.Equal(27,target.Document.Objects.OfType<ShapeObject>().Single().Placement.RotationDegrees);
        Assert.Equal(ink.Style,target.Document.Objects.OfType<InkObject>().Single().Style); Assert.Equal(ink.Points.ToArray(),target.Document.Objects.OfType<InkObject>().Single().Points.ToArray());
        var asset=target.Document.Snapshot().Assets.Single(); Assert.Equal(hash,SHA256.HashData(File.ReadAllBytes(asset.WorkingPath!))); Assert.Equal(hash,SHA256.HashData(File.ReadAllBytes(image)));
        vm.UndoCommand.Execute(null); Assert.Equal(empty,target.Document.Snapshot()); // Paste is one action.
        vm.UndoCommand.Execute(null); Assert.DoesNotContain(target,vm.Pages); // Creating the target is an independent action.
        vm.UndoCommand.Execute(null); Assert.Equal(before,page.Document.Snapshot()); // Cut restores the complete original.
        Assert.Equal(hash,SHA256.HashData(File.ReadAllBytes(image))); Assert.Empty(dialogs.Errors);
    }
}
