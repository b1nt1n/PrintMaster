using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Xunit;

namespace PrintMaster.Tests;

public sealed class MarkdownLayoutVersionTests
{
    private const string Source="# Заметка\n\nТекст со сноской[^about].\n\n[^about]: Исходное пояснение.";
    private static MarkdownDocument Definition(int version=MarkdownDocument.CurrentLayoutEngineVersion)
        =>new(Guid.NewGuid(),Source,MarkdownStyle.Default,[],"Сохранённая заметка") {LayoutEngineVersion=version};
    private static PrintPageItem Page(MarkdownDocument document,int index=0)
    {
        var paper=SheetPaper.A4;
        var markdown=new MarkdownObject(Guid.NewGuid(),new(paper.SafeAreaMm),document.Id,index) {Document=document};
        return new(SheetDocument.Restore(new(Guid.NewGuid(),paper,[],[markdown],null)));
    }
    private static PrintSettingsSnapshot Settings(bool duplex=false)=>new PrintSettings {PrinterName="Test printer",ManualDuplex=duplex}.Snapshot();

    [Theory] [InlineData(7)] [InlineData(8)]
    public async Task ExistingProjectsRequireReviewedReflowWithoutChangingSourceOrSelectedPageIndices(int version)
    {
        var document=Definition();var page=Page(document);var path=ProjectFileTests.PathFor("markdown-layout-legacy");var service=new ProjectFileService();
        await service.SaveAsync(ProjectFileTests.Session(page),path,true,default);
        Mutate(path,json=>
        {
            json["version"]=version;
            json["session"]!["markdownDocuments"]![0]!.AsObject().Remove("layoutEngineVersion");
        });
        using var workspace=new SessionWorkspace();var loaded=await service.LoadAsync(path,workspace,default);
        var restored=Assert.Single(loaded.Session.MarkdownDocuments);var sheet=Assert.Single(loaded.Session.Pages).Sheet;
        Assert.Equal(1,restored.LayoutEngineVersion);Assert.Equal(Source,restored.Source);Assert.Equal(document.Id,restored.Id);
        var item=Assert.IsType<MarkdownObject>(Assert.Single(sheet.Objects));Assert.Same(restored,item.Document);Assert.Equal(0,item.PageIndex);
        var error=Assert.Throws<ArgumentException>(()=>ManualDuplexPlanner.ValidateQueue([new PrintPageItem(SheetDocument.Restore(sheet)).Snapshot()],Settings()));
        Assert.Contains(MarkdownDocument.LayoutUpgradeMessage,error.Message);
        // The editor can inspect/reflow old source; only printing the old page indices is blocked.
        using var layoutService=new MarkdownLayoutService();var layout=await layoutService.LayoutAsync(restored,sheet.Paper,sheet.Assets,default);
        Assert.NotEmpty(layout.Pages);Assert.Equal(Source,restored.Source);
        var saved=ProjectFileTests.PathFor("markdown-layout-legacy-resaved");await service.SaveAsync(loaded.Session,saved,true,default);
        using var rereadWorkspace=new SessionWorkspace();var reread=await service.LoadAsync(saved,rereadWorkspace,default);
        Assert.Equal(1,Assert.Single(reread.Session.MarkdownDocuments).LayoutEngineVersion);
    }

    [Theory] [InlineData(1)] [InlineData(2)]
    public async Task ApplyingReviewedLegacySourceCreatesVersionTenProjectWithCurrentPagination(int legacyVersion)
    {
        var legacy=Definition(legacyVersion);using var vm=ClipboardTests.CreateVm(new ClipboardTests.TestDialogs(),new());
        var original=Page(legacy);vm.AddPage(original);
        await vm.ApplyMarkdownAsync(new(legacy,[]),original.Document.Paper,legacy.Id,default);
        var current=Assert.Single(vm.CaptureProject().MarkdownDocuments);
        Assert.Equal(MarkdownDocument.CurrentLayoutEngineVersion,current.LayoutEngineVersion);Assert.Equal(Source,current.Source);Assert.Equal(legacyVersion,legacy.LayoutEngineVersion);
        ManualDuplexPlanner.ValidateQueue(vm.Pages.Select(p=>p.Snapshot()).ToArray(),Settings());
        var path=ProjectFileTests.PathFor("markdown-layout-current");var service=new ProjectFileService();await service.SaveAsync(vm.CaptureProject(),path,true,default);
        using(var archive=ZipFile.OpenRead(path))
        {
            using var reader=new StreamReader(archive.GetEntry("manifest.json")!.Open());var json=JsonNode.Parse(await reader.ReadToEndAsync())!;
            Assert.Equal(10,json["version"]!.GetValue<int>());
            Assert.Equal(3,json["session"]!["markdownDocuments"]![0]!["layoutEngineVersion"]!.GetValue<int>());
        }
        using var workspace=new SessionWorkspace();var loaded=await service.LoadAsync(path,workspace,default);
        var reopened=Assert.Single(loaded.Session.MarkdownDocuments);
        Assert.Equal(current.Id,reopened.Id);Assert.Equal(current.Source,reopened.Source);Assert.Equal(current.Style,reopened.Style);Assert.Equal(current.LayoutEngineVersion,reopened.LayoutEngineVersion);
        ManualDuplexPlanner.ValidateQueue(loaded.Session.Pages.Select(p=>new PrintPageItem(SheetDocument.Restore(p.Sheet)).Snapshot()).ToArray(),Settings());
    }

    [Fact]
    public async Task CurrentGenerationMayIntentionallyKeepOnlyOneLaterPage()
    {
        var current=Definition() with {Source=string.Join("\n\n",Enumerable.Range(1,100).Select(i=>$"Сохранённый абзац {i}: достаточно текста для нескольких страниц."))};
        var layout=await MarkdownLayoutService.Shared.LayoutAsync(current,SheetPaper.A4,ImmutableArray<SourceAsset>.Empty,default);Assert.True(layout.Pages.Length>2);
        var selected=Page(current,2);var snapshot=selected.Snapshot();
        ManualDuplexPlanner.ValidateQueue([snapshot],Settings());
        var path=ProjectFileTests.PathFor("markdown-layout-subset");var service=new ProjectFileService();await service.SaveAsync(ProjectFileTests.Session(selected),path,true,default);
        using var workspace=new SessionWorkspace();var loaded=await service.LoadAsync(path,workspace,default);
        var restored=Assert.Single(loaded.Session.Pages);Assert.Equal(2,Assert.IsType<MarkdownObject>(Assert.Single(restored.Sheet.Objects)).PageIndex);
        Assert.Equal(3,Assert.Single(loaded.Session.MarkdownDocuments).LayoutEngineVersion);
        ManualDuplexPlanner.ValidateQueue([new PrintPageItem(SheetDocument.Restore(restored.Sheet)).Snapshot()],Settings());
    }

    [Theory] [InlineData(false,1)] [InlineData(true,1)] [InlineData(false,2)] [InlineData(true,2)]
    public async Task LegacyQueueIsRejectedBeforeAnySimplexOrDuplexJobCanReachRunner(bool duplex,int legacyVersion)
    {
        var runner=new RecordingRunner();var prompts=new ClipboardTests.TestDialogs();
        var workflow=new PaperPrintWorkflow(runner,prompts,_=>null,_=>true,_=>Task.CompletedTask);
        var error=await Assert.ThrowsAsync<ArgumentException>(()=>workflow.StartAsync([PrintPageItem.CreateBlank(SheetPaper.A4).Snapshot(),Page(Definition(legacyVersion)).Snapshot()],Settings(duplex),null,default));
        Assert.Contains(MarkdownDocument.LayoutUpgradeMessage,error.Message);Assert.Equal(0,runner.Calls);Assert.False(workflow.HasPending);
    }

    [Theory] [InlineData(1)] [InlineData(2)]
    public void DirectRasterizationCannotBypassLegacyGuard(int legacyVersion)
    {
        var sheet=Page(Definition(legacyVersion)).Document.Snapshot();
        var error=Assert.Throws<InvalidOperationException>(()=>new PageRasterizer(new()).RenderSheet(sheet,120,false,default,300));
        Assert.Equal(MarkdownDocument.LayoutUpgradeMessage,error.Message);
    }

    [Theory] [InlineData("0")] [InlineData("-1")] [InlineData("4")] [InlineData("null")] [InlineData("1.5")] [InlineData("\"NaN\"")]
    public async Task InvalidSavedLayoutMetadataIsRejectedBeforePrinting(string value)
    {
        var path=ProjectFileTests.PathFor("markdown-layout-invalid");var service=new ProjectFileService();
        await service.SaveAsync(ProjectFileTests.Session(Page(Definition())),path,true,default);
        Mutate(path,json=>json["session"]!["markdownDocuments"]![0]!["layoutEngineVersion"]=JsonNode.Parse(value));
        using var workspace=new SessionWorkspace();await Assert.ThrowsAsync<InvalidDataException>(()=>service.LoadAsync(path,workspace,default));
    }

    [Fact]
    public async Task NewProjectSchemaCannotOmitLayoutMetadataAndInheritCurrentConstructorDefault()
    {
        var path=ProjectFileTests.PathFor("markdown-layout-missing");var service=new ProjectFileService();
        await service.SaveAsync(ProjectFileTests.Session(Page(Definition())),path,true,default);
        Mutate(path,json=>json["session"]!["markdownDocuments"]![0]!.AsObject().Remove("layoutEngineVersion"));
        using var workspace=new SessionWorkspace();
        var error=await Assert.ThrowsAsync<InvalidDataException>(()=>service.LoadAsync(path,workspace,default));
        Assert.Contains("версия верстки",error.Message);
    }

    [Fact]
    public void PreviousClipboardWithoutLayoutMetadataAlsoRequiresReflowAndNewCopiesPreserveGeneration()
    {
        var document=Definition();var page=Page(document);var sheet=page.Document.Snapshot();
        var raw=JsonNode.Parse(ClipboardObjectService.Serialize(ClipboardObjectService.Capture(sheet,sheet.Objects[0])))!;
        raw["version"]=3;
        raw["markdownDocuments"]![0]!.AsObject().Remove("layoutEngineVersion");
        var old=ClipboardObjectService.Deserialize(raw.ToJsonString());var restored=Assert.IsType<MarkdownObject>(old.Object);
        Assert.Equal(1,restored.Document!.LayoutEngineVersion);Assert.Equal(Source,restored.Document.Source);
        var current=ClipboardObjectService.Deserialize(ClipboardObjectService.Serialize(ClipboardObjectService.Capture(sheet,sheet.Objects[0])));
        Assert.Equal(3,Assert.IsType<MarkdownObject>(current.Object).Document!.LayoutEngineVersion);
        var copied=MarkdownGraphCloner.ClonePages([new(sheet,true,1)]);Assert.Equal(3,Assert.Single(copied[0].Sheet.Objects.OfType<MarkdownObject>()).Document!.LayoutEngineVersion);
    }

    [Fact]
    public void NewMarkdownClipboardCannotBeMistakenForThePreviousReadersProtocol()
    {
        var current=Definition();var currentPage=Page(current);var sheet=currentPage.Document.Snapshot();
        var packet=ClipboardObjectService.Capture(sheet,sheet.Objects[0]);Assert.Equal(5,packet.Version);
        var raw=JsonNode.Parse(ClipboardObjectService.Serialize(packet))!;Assert.Equal(5,raw["version"]!.GetValue<int>());
        raw["markdownDocuments"]![0]!.AsObject().Remove("layoutEngineVersion");
        Assert.Throws<InvalidDataException>(()=>ClipboardObjectService.Deserialize(raw.ToJsonString()));
        var legacy=Definition(1);var legacyPage=Page(legacy);var legacySheet=legacyPage.Document.Snapshot();
        var legacyPacket=ClipboardObjectService.Capture(legacySheet,legacySheet.Objects[0]);Assert.Equal(3,legacyPacket.Version);
        Assert.Equal(1,Assert.IsType<MarkdownObject>(ClipboardObjectService.Deserialize(ClipboardObjectService.Serialize(legacyPacket)).Object).Document!.LayoutEngineVersion);
        var text=new TextObject(Guid.NewGuid(),new(new(10,10,50,30)),"Обычный текст",new());
        Assert.Throws<InvalidDataException>(()=>ClipboardObjectService.Serialize(new(5,text,[])));
    }

    [Fact]
    public async Task VersionNineProjectPreservesGenerationTwoUntilReviewedApply()
    {
        var original=Definition(2);var service=new ProjectFileService();var path=ProjectFileTests.PathFor("markdown-layout-generation-two");
        await service.SaveAsync(ProjectFileTests.Session(Page(original)),path,true,default);
        Mutate(path,json=>json["version"]=9);
        using var workspace=new SessionWorkspace();var loaded=await service.LoadAsync(path,workspace,default);
        var legacy=Assert.Single(loaded.Session.MarkdownDocuments);
        Assert.Equal(2,legacy.LayoutEngineVersion);Assert.Equal(Source,legacy.Source);Assert.Equal(original.Id,legacy.Id);
        AssertLegacyOutputBlocked(legacy);
        var saved=ProjectFileTests.PathFor("markdown-layout-generation-two-resaved");await service.SaveAsync(loaded.Session,saved,true,default);
        using var reopenedWorkspace=new SessionWorkspace();var reopened=await service.LoadAsync(saved,reopenedWorkspace,default);
        var preserved=Assert.Single(reopened.Session.MarkdownDocuments);
        Assert.Equal(2,preserved.LayoutEngineVersion);Assert.Equal(Source,preserved.Source);AssertLegacyOutputBlocked(preserved);
        using var vm=ClipboardTests.CreateVm(new ClipboardTests.TestDialogs(),new());var page=Page(preserved);vm.AddPage(page);
        await vm.ApplyMarkdownAsync(new(preserved,[]),page.Document.Paper,preserved.Id,default);
        var current=Assert.Single(vm.CaptureProject().MarkdownDocuments);Assert.Equal(3,current.LayoutEngineVersion);Assert.Equal(Source,current.Source);
        ManualDuplexPlanner.ValidateQueue(vm.Pages.Select(p=>p.Snapshot()).ToArray(),Settings());
    }

    [Fact]
    public async Task VersionFourClipboardPreservesGenerationTwoUntilReviewedApply()
    {
        var legacy=Definition(2);var sheet=Page(legacy).Document.Snapshot();
        var packet=ClipboardObjectService.Capture(sheet,sheet.Objects[0]);Assert.Equal(4,packet.Version);
        var raw=ClipboardObjectService.Serialize(packet);var parsed=JsonNode.Parse(raw)!;
        Assert.Equal(4,parsed["version"]!.GetValue<int>());Assert.Equal(2,parsed["markdownDocuments"]![0]!["layoutEngineVersion"]!.GetValue<int>());
        var decoded=ClipboardObjectService.Deserialize(raw);var restored=Assert.IsType<MarkdownObject>(decoded.Object).Document!;
        Assert.Equal(2,restored.LayoutEngineVersion);Assert.Equal(Source,restored.Source);AssertLegacyOutputBlocked(restored);
        var recopied=ClipboardObjectService.Deserialize(ClipboardObjectService.Serialize(decoded));
        Assert.Equal(4,recopied.Version);var preserved=Assert.IsType<MarkdownObject>(recopied.Object).Document!;
        Assert.Equal(2,preserved.LayoutEngineVersion);Assert.Equal(Source,preserved.Source);AssertLegacyOutputBlocked(preserved);
        using var vm=ClipboardTests.CreateVm(new ClipboardTests.TestDialogs(),new());var page=Page(preserved);vm.AddPage(page);
        await vm.ApplyMarkdownAsync(new(preserved,[]),page.Document.Paper,preserved.Id,default);
        var current=Assert.Single(vm.CaptureProject().MarkdownDocuments);Assert.Equal(3,current.LayoutEngineVersion);Assert.Equal(Source,current.Source);
        ManualDuplexPlanner.ValidateQueue(vm.Pages.Select(p=>p.Snapshot()).ToArray(),Settings());
    }

    [Theory] [InlineData(4,3)] [InlineData(5,2)] [InlineData(5,4)]
    public void ClipboardProtocolCannotMislabelLayoutGeneration(int packetVersion,int layoutVersion)
    {
        var sheet=Page(Definition()).Document.Snapshot();
        var raw=JsonNode.Parse(ClipboardObjectService.Serialize(ClipboardObjectService.Capture(sheet,sheet.Objects[0])))!;
        raw["version"]=packetVersion;raw["markdownDocuments"]![0]!["layoutEngineVersion"]=layoutVersion;
        Assert.Throws<InvalidDataException>(()=>ClipboardObjectService.Deserialize(raw.ToJsonString()));
    }

    [Theory] [InlineData(4)] [InlineData(5)]
    public void ExplicitMetadataClipboardProtocolsCannotInheritCurrentConstructorDefault(int packetVersion)
    {
        var sheet=Page(Definition()).Document.Snapshot();
        var raw=JsonNode.Parse(ClipboardObjectService.Serialize(ClipboardObjectService.Capture(sheet,sheet.Objects[0])))!;
        raw["version"]=packetVersion;raw["markdownDocuments"]![0]!.AsObject().Remove("layoutEngineVersion");
        Assert.Throws<InvalidDataException>(()=>ClipboardObjectService.Deserialize(raw.ToJsonString()));
    }

    [Fact]
    public async Task VersionNineProjectCannotClaimLaterGenerationThree()
    {
        var path=ProjectFileTests.PathFor("markdown-layout-legacy-schema-future");var service=new ProjectFileService();
        await service.SaveAsync(ProjectFileTests.Session(Page(Definition())),path,true,default);Mutate(path,json=>json["version"]=9);
        using var workspace=new SessionWorkspace();await Assert.ThrowsAsync<InvalidDataException>(()=>service.LoadAsync(path,workspace,default));
    }

    [Theory] [InlineData(ExportFormat.Png,1)] [InlineData(ExportFormat.Pdf,1)] [InlineData(ExportFormat.Png,2)] [InlineData(ExportFormat.Pdf,2)]
    public async Task LegacyGenerationCannotExportAnUnreviewedPageOrLeaveOutputFiles(ExportFormat format,int legacyVersion)
    {
        var folder=SheetExportTests.Folder("markdown-layout-legacy-export");var extension=format==ExportFormat.Pdf ? ".pdf" : ".png";
        var page=SheetExportRenderer.Prepare(Page(Definition(legacyVersion)).Snapshot(),SheetPaper.A4);
        var plan=ExportFilePlan.Build([page],SheetExportTests.Options(format),Path.Combine(folder,"unreviewed"+extension));
        var service=new PageExportService(new(new(new())));
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ExportAsync(plan,false,null,default));
        Assert.Equal(MarkdownDocument.LayoutUpgradeMessage,error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
    }

    [Theory] [InlineData(1)] [InlineData(2)]
    public void ExportValidationRejectsLegacyBeforeAnyOutputFileWork(int legacyVersion)
    {
        var page=SheetExportRenderer.Prepare(Page(Definition(legacyVersion)).Snapshot(),SheetPaper.A4);
        var error=Assert.Throws<InvalidOperationException>(()=>SheetExportRenderer.ValidatePages([page],SheetExportTests.Options()));
        Assert.Equal(MarkdownDocument.LayoutUpgradeMessage,error.Message);
    }

    private static void AssertLegacyOutputBlocked(MarkdownDocument document)
    {
        var page=Page(document);var printError=Assert.Throws<ArgumentException>(()=>ManualDuplexPlanner.ValidateQueue([page.Snapshot()],Settings()));
        Assert.Contains(MarkdownDocument.LayoutUpgradeMessage,printError.Message);
        var rasterError=Assert.Throws<InvalidOperationException>(()=>new PageRasterizer(new()).RenderSheet(page.Document.Snapshot(),120,false,default,300));
        Assert.Equal(MarkdownDocument.LayoutUpgradeMessage,rasterError.Message);
    }

    private static void Mutate(string path,Action<JsonNode> change)
    {
        using var archive=ZipFile.Open(path,ZipArchiveMode.Update);var entry=archive.GetEntry("manifest.json")!;JsonNode json;
        using(var reader=new StreamReader(entry.Open()))json=JsonNode.Parse(reader.ReadToEnd())!;
        change(json);entry.Delete();using var writer=new StreamWriter(archive.CreateEntry("manifest.json").Open());writer.Write(json.ToJsonString());
    }
    private sealed class RecordingRunner : IPrintJobRunner
    {
        public int Calls;
        public Task PrintAsync(IReadOnlyList<PageSnapshot> pages,PrintSettingsSnapshot settings,IProgress<PrintProgress>? progress,CancellationToken token)
        {Calls++;return Task.CompletedTask;}
    }
}
