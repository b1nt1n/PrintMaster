using PrintMaster.Models;
using PrintMaster.Services;
using System.Collections.Immutable;
using System.IO;
using Xunit;

namespace PrintMaster.Tests;

public sealed class MarkdownClipboardIntegrationTests
{
    [Fact]
    public async Task PortableQueueCopyPastesLinkedPagesAfterOriginalSessionIsDisposed()
    {
        var clipboard=new FakeClipboard();var dialogs=new ClipboardTests.TestDialogs();var source=ClipboardTests.CreateVm(dialogs,clipboard);
        var document=new MarkdownDocument(Guid.NewGuid(),string.Join("\n\n",Enumerable.Range(1,90).Select(i=>$"Строка {i} с $x^2$")),MarkdownStyle.Default,[],"Копия");
        await source.ApplyMarkdownAsync(new(document,[]),new(new(148,210),false,SheetMargins.SafeDefault),null,default);
        var count=source.Pages.Count;source.SetQueueSelection(source.Pages.ToArray(),source.Pages[0]);Assert.True(await source.CopyContextAsync(true));
        Assert.NotNull(clipboard.Content.SelectionArchive);source.Dispose();using var target=ClipboardTests.CreateVm(dialogs,clipboard);
        await target.PasteClipboardCommand.ExecuteAsync(PasteDestination.Queue);Assert.Equal(count,target.Pages.Count);Assert.Single(target.CaptureProject().MarkdownDocuments);
        var definition=target.CaptureProject().MarkdownDocuments[0];Assert.NotEqual(document.Id,definition.Id);Assert.Equal(document.Source,definition.Source);
        Assert.All(target.Pages,p=>Assert.Same(definition,p.Document.Objects.OfType<MarkdownObject>().Single().Document));
        using var bitmap=new PageRasterizer(new()).RenderSheet(target.Pages[0].Document.Snapshot(),120,false,default,600);Assert.True(bitmap.Width>0);
    }
    [Fact]
    public async Task SingleMarkdownPacketImportsEveryImageAndRemapsDocumentReferences()
    {
        var clipboard=new FakeClipboard();var dialogs=new ClipboardTests.TestDialogs();using var target=ClipboardTests.CreateVm(dialogs,clipboard);
        var paths=new[]{Fixtures.CreateImage("md-clip-a.png"),Fixtures.CreateImage("md-clip-b.png")};
        var assets=paths.Select(p=>new SourceAsset(Guid.NewGuid(),SourceKind.Image,p,p,0,1,new(1000,750),new(100,75))).ToImmutableArray();
        var definition=new MarkdownDocument(Guid.NewGuid(),"![A](a.png)\n\n![B](b.png)",MarkdownStyle.Default,[new("a.png",assets[0].Id),new("b.png",assets[1].Id)],"Две картинки");
        var item=new MarkdownObject(Guid.NewGuid(),new(SheetPaper.A4.SafeAreaMm),definition.Id,0){Document=definition};
        var sheet=new SheetSnapshot(Guid.NewGuid(),SheetPaper.A4,assets,[item],null);
        clipboard.Content=new([],ObjectJson:ClipboardObjectService.Serialize(ClipboardObjectService.Capture(sheet,item)));
        await target.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto);var stored=target.Pages.Single().Document.Snapshot();
        Assert.Equal(2,stored.Assets.Length);Assert.All(stored.Assets,a=>Assert.DoesNotContain(assets,old=>old.Id==a.Id));
        var copy=Assert.Single(stored.Objects.OfType<MarkdownObject>());Assert.NotEqual(definition.Id,copy.DocumentId);Assert.Equal(copy.DocumentId,copy.Document!.Id);
        Assert.All(copy.Document.Images,image=>Assert.Contains(stored.Assets,a=>a.Id==image.AssetId && File.Exists(a.WorkingPath)));
        Assert.Equal(2,target.CaptureProject().Pages[0].Sheet.Assets.Length);Assert.Single(target.CaptureProject().MarkdownDocuments);
    }
}
