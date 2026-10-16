using PrintMaster.Models;
using PrintMaster.Services;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Xunit;

namespace PrintMaster.Tests;

public sealed class PagePrintOptionsPersistenceTests
{
    internal static PagePrintOptions PhotoA5=>new(PaperPreset.A5,new(148,210),true,ScaleMode.Fill,MonochromeMode.Photo,300,182);
    private static PrintSettingsSnapshot SessionSettings()=>new("Session printer",PaperPreset.A4,new(210,297),false,
        ScaleMode.Fit,MonochromeMode.Auto,600,160,5,"result.pdf",true,null,true,2,-3,true,180);

    [Fact]
    public void CaptureApplyFreezeOnlyPageOptionsAndPreserveSessionPrinterDuplexCalibrationAndConsents()
    {
        var session=SessionSettings(); var options=PagePrintOptions.Capture(session);
        Assert.Equal(session,options.ApplyTo(session));
        var actual=PhotoA5.ApplyTo(session);
        Assert.Equal(session with {Paper=PaperPreset.A5,PaperSizeMm=new(148,210),Landscape=true,
            Scaling=ScaleMode.Fill,Monochrome=MonochromeMode.Photo,Dpi=300,Threshold=182},actual);
        Assert.Equal(PhotoA5,PagePrintOptions.Capture(actual));
    }

    [Theory]
    [InlineData("paper")] [InlineData("scale")] [InlineData("mono")] [InlineData("small")]
    [InlineData("large")] [InlineData("nan")] [InlineData("dpi")] [InlineData("threshold-low")]
    [InlineData("threshold-high")] [InlineData("a4-size")] [InlineData("a5-size")]
    [InlineData("notebook-size")] [InlineData("notebook-kind")]
    public void InvalidPageOptionsAreRejectedBeforeChangingTheModel(string variant)
    {
        var notebook=NotebookProfiles.Default(NotebookVariant.GridSpread);
        var invalid=variant switch
        {
            "paper"=>PhotoA5 with {Paper=(PaperPreset)400},
            "scale"=>PhotoA5 with {Scaling=(ScaleMode)400},
            "mono"=>PhotoA5 with {Monochrome=(MonochromeMode)400},
            "small"=>PhotoA5 with {Paper=PaperPreset.Custom,PaperSizeMm=new(24,200)},
            "large"=>PhotoA5 with {Paper=PaperPreset.Custom,PaperSizeMm=new(200,1001)},
            "nan"=>PhotoA5 with {Paper=PaperPreset.Custom,PaperSizeMm=new(double.NaN,200)},
            "dpi"=>PhotoA5 with {Dpi=120},
            "threshold-low"=>PhotoA5 with {Threshold=-1},
            "threshold-high"=>PhotoA5 with {Threshold=256},
            "a4-size"=>PhotoA5 with {Paper=PaperPreset.A4},
            "a5-size"=>PhotoA5 with {PaperSizeMm=new(210,297)},
            "notebook-size"=>PhotoA5 with {Paper=PaperPreset.Notebook,Notebook=notebook},
            _=>PhotoA5 with {Notebook=notebook}
        };
        var page=PrintPageItem.CreateBlank(SheetPaper.A4);
        Assert.Throws<ArgumentException>(()=>page.PrintOptions=invalid); Assert.Null(page.PrintOptions);
        Assert.Throws<ArgumentException>(()=>invalid.ApplyTo(SessionSettings()));
    }

    [Fact]
    public void ValidCustomAndNotebookValuesIncludeBoundarySizes()
    {
        (PhotoA5 with {Paper=PaperPreset.Custom,PaperSizeMm=new(25,1000)}).Validate();
        var notebook=NotebookProfiles.Default(NotebookVariant.RuledSpread) with {WidthMm=336,RightMarginMm=19};
        var options=PhotoA5 with {Paper=PaperPreset.Notebook,PaperSizeMm=notebook.FeedSize,Notebook=notebook};
        options.Validate(); Assert.Same(notebook,options.ApplyTo(SessionSettings()).Notebook);
    }

    [Fact]
    public async Task PortableProjectRetainsPageOverridesWithoutChangingOrDuplicatingMaterials()
    {
        var image=Fixtures.CreateImage("pm020-persist.png");
        var source=new SourceAsset(Guid.NewGuid(),SourceKind.Image,image,image,0,1,new(1000,750),new(200,150));
        var first=PrintPageItem.FromSource(source); first.PrintOptions=PhotoA5;first.Copies=3;
        var inherited=PrintPageItem.FromSource(source with {Id=Guid.NewGuid()}); inherited.IsIncluded=false;
        var path=ProjectFileTests.PathFor("pm020-persist"); var service=new ProjectFileService();
        await service.SaveAsync(ProjectFileTests.Session(first,inherited),path,true,default);
        using(var zip=ZipFile.OpenRead(path))
        {
            Assert.Single(zip.Entries,e=>e.FullName.StartsWith("assets/"));
            using var reader=new StreamReader(zip.GetEntry("manifest.json")!.Open());
            var manifest=JsonNode.Parse(reader.ReadToEnd())!;Assert.Equal(10,manifest["version"]!.GetValue<int>());
            Assert.Equal("A5",manifest["session"]!["pages"]![0]!["printOptions"]!["paper"]!.GetValue<string>());
        }
        using var workspace=new SessionWorkspace();var loaded=await service.LoadAsync(path,workspace,default);
        Assert.Equal(PhotoA5,loaded.Session.Pages[0].PrintOptions);Assert.Null(loaded.Session.Pages[1].PrintOptions);
        Assert.Equal(3,loaded.Session.Pages[0].Copies);Assert.False(loaded.Session.Pages[1].IsIncluded);
        Assert.Equal(loaded.Session.Pages[0].Sheet.PrimaryAsset!.WorkingPath,loaded.Session.Pages[1].Sheet.PrimaryAsset!.WorkingPath);
    }

    [Theory] [InlineData(1)] [InlineData(3)] [InlineData(6)] [InlineData(7)]
    public async Task LegacyProjectsInheritSessionSettingsAndRejectOverridesTaggedAsOldVersion(int version)
    {
        var service=new ProjectFileService(); var page=PrintPageItem.CreateBlank(SheetPaper.A4);
        var path=ProjectFileTests.PathFor("pm020-legacy");await service.SaveAsync(ProjectFileTests.Session(page),path,true,default);
        Mutate(path,json=>json["version"]=version);
        using var workspace=new SessionWorkspace();var loaded=await service.LoadAsync(path,workspace,default);
        Assert.Null(Assert.Single(loaded.Session.Pages).PrintOptions);
        page.PrintOptions=PhotoA5;await service.SaveAsync(ProjectFileTests.Session(page),path,true,default);
        Mutate(path,json=>json["version"]=version);
        await Assert.ThrowsAsync<InvalidDataException>(()=>service.LoadAsync(path,workspace,default));
    }

    [Theory] [InlineData("threshold")] [InlineData("dpi")] [InlineData("future-version")]
    public async Task MalformedSavedOptionsOrUnsupportedProjectVersionAreRejected(string variant)
    {
        var service=new ProjectFileService();var page=PrintPageItem.CreateBlank(SheetPaper.A4);page.PrintOptions=PhotoA5;
        var path=ProjectFileTests.PathFor("pm020-invalid");await service.SaveAsync(ProjectFileTests.Session(page),path,true,default);
        Mutate(path,json=>
        {
            if(variant=="future-version")json["version"]=11;
            else json["session"]!["pages"]![0]!["printOptions"]![variant]=variant=="dpi" ? 150 : -1;
        });
        using var workspace=new SessionWorkspace();await Assert.ThrowsAsync<InvalidDataException>(()=>service.LoadAsync(path,workspace,default));
    }

    [Fact]
    public async Task SnapshotUndoRedoSaveAndLoadRetainPageOptionsAndDirtyCheckpoint()
    {
        var dialogs=new ClipboardTests.TestDialogs();using var vm=ClipboardTests.CreateVm(dialogs,new());
        var page=PrintPageItem.CreateBlank(SheetPaper.A4);vm.AddPage(page);
        var path=ProjectFileTests.PathFor("pm020-history");Assert.True(await vm.SaveProjectToAsync(path));Assert.False(vm.HasUnsavedChanges);
        var undo=vm.UndoCount;page.PrintOptions=PhotoA5;
        Assert.Equal(PhotoA5,page.Snapshot().PrintOptions);Assert.Equal(PhotoA5,Assert.Single(vm.CaptureProject().Pages).PrintOptions);
        Assert.Equal(undo+1,vm.UndoCount);Assert.True(vm.HasUnsavedChanges);
        vm.UndoCommand.Execute(null);Assert.Null(page.PrintOptions);Assert.False(vm.HasUnsavedChanges);
        vm.RedoCommand.Execute(null);Assert.Equal(PhotoA5,page.PrintOptions);Assert.True(vm.HasUnsavedChanges);
        Assert.True(await vm.SaveProjectToAsync(path));Assert.False(vm.HasUnsavedChanges);
        using var target=ClipboardTests.CreateVm(dialogs,new());Assert.True(await target.OpenProjectFromAsync(path));
        Assert.Equal(PhotoA5,Assert.Single(target.Pages).PrintOptions);Assert.False(target.HasUnsavedChanges);Assert.Empty(dialogs.Errors);
    }

    [Fact]
    public async Task QueueDuplicateAndClipboardRetainOverridesAfterOriginalSessionCloses()
    {
        var clipboard=new FakeClipboard();var dialogs=new ClipboardTests.TestDialogs();Guid originalId;
        using(var source=ClipboardTests.CreateVm(dialogs,clipboard))
        {
            var page=PrintPageItem.CreateBlank(SheetPaper.A4);source.AddPage(page);page.PrintOptions=PhotoA5;
            originalId=page.Id;source.SetQueueSelection([page],page);source.DuplicateHighlightedPagesCommand.Execute(null);
            Assert.Equal(PhotoA5,source.Pages[1].PrintOptions);Assert.NotEqual(page.Id,source.Pages[1].Id);
            source.SetQueueSelection([page],page);Assert.True(await source.CopyContextAsync(true));
        }
        using var target=ClipboardTests.CreateVm(dialogs,clipboard);await target.PasteClipboardCommand.ExecuteAsync(PasteDestination.Queue);
        var pasted=Assert.Single(target.Pages);Assert.Equal(PhotoA5,pasted.PrintOptions);Assert.NotEqual(originalId,pasted.Id);
        var copies=MarkdownGraphCloner.ClonePages([new(pasted.Document.Snapshot(),false,5){PrintOptions=PhotoA5}]);
        Assert.Equal(PhotoA5,Assert.Single(copies).PrintOptions);Assert.False(copies[0].IsIncluded);Assert.Equal(5,copies[0].Copies);
        Assert.Empty(dialogs.Errors);
    }

    private static void Mutate(string path,Action<JsonNode> change)
    {
        using var zip=ZipFile.Open(path,ZipArchiveMode.Update);var entry=zip.GetEntry("manifest.json")!;JsonNode json;
        using(var reader=new StreamReader(entry.Open()))json=JsonNode.Parse(reader.ReadToEnd())!;
        change(json);entry.Delete();using var output=new StreamWriter(zip.CreateEntry("manifest.json").Open());output.Write(json.ToJsonString());
    }
}
