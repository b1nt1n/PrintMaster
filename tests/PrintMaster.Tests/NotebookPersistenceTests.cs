using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Xunit;

namespace PrintMaster.Tests;

public sealed class NotebookPersistenceTests
{
    [Fact]
    public async Task ProjectKeepsFractionalProfileFullStoryOverflowAndBothFrameIds()
    {
        var profile=NotebookProfiles.Default(NotebookVariant.RuledSpread) with { Id=Guid.NewGuid(),Name="Тетрадь Б",WidthMm=332.4,HeightMm=206.7,FoldMm=166.1,LeftMarginMm=19.3,RightMarginMm=20.1,LineStepMm=7.9 };
        var paper=profile.ToPaper(); var page=PrintPageItem.CreateBlank(paper); page.Copies=3;
        var story=new NotebookTextStory(Guid.NewGuid(),string.Concat(Enumerable.Repeat("Текст 🙂\r\n",1000)),new("Arial",14,"#123456",true));
        var result=NotebookTextLayout.Layout(paper,story,paper.SafeAreaMm); page.Document.SetNotebookText(story,result.Objects);
        var settings=new PrintSettings { PrinterName="HP LaserJet Professional P1102",Paper=PaperPreset.Notebook,Notebook=profile,Landscape=true,ManualDuplex=true,PrintOffsetXmm=0.6 };
        var session=new ProjectSession(ProjectPrintSettings.Capture(settings),new(page.Id,50,true,false,"#111827",4,24,0,300),[new(page.Document.Snapshot(),true,page.Copies)]);
        var path=ProjectFileTests.PathFor("nb001-project"); await new ProjectFileService().SaveAsync(session,path,true,default);
        using var workspace=new SessionWorkspace(); var loaded=await new ProjectFileService().LoadAsync(path,workspace,default);
        var restored=Assert.Single(loaded.Session.Pages);
        Assert.Equal(profile,restored.Sheet.Paper.Notebook); Assert.Equal(story,restored.Sheet.NotebookText);
        Assert.Equal(page.Document.Objects,restored.Sheet.Objects); Assert.Equal(result.Remainder,NotebookTextLayout.Remainder(restored.Sheet));
        Assert.Equal(3,restored.Copies); Assert.True(loaded.Session.Print.ManualDuplex); Assert.Equal(0.6,loaded.Session.Print.PrintOffsetXmm);
    }
    [Fact]
    public async Task VersionThreeLegacyNotebookRemains165By205WithoutInventedRuling()
    {
        var page=PrintPageItem.CreateBlank(new(new(165,205),false,SheetMargins.SafeDefault));
        var session=new ProjectSession(ProjectPrintSettings.Capture(new() { Paper=PaperPreset.Notebook }),new(page.Id,50,true,false,"#111827",4,24,0,300),[new(page.Document.Snapshot(),true,1)]);
        var path=ProjectFileTests.PathFor("nb001-legacy"); await new ProjectFileService().SaveAsync(session,path,true,default);
        using(var zip=ZipFile.Open(path,ZipArchiveMode.Update))
        {
            var entry=zip.GetEntry("manifest.json")!; JsonNode node;
            using(var reader=new StreamReader(entry.Open())) node=JsonNode.Parse(reader.ReadToEnd())!;
            node["version"]=3; node["session"]!["pages"]![0]!["sheet"]!["paper"]!.AsObject().Remove("notebook");
            node["session"]!["pages"]![0]!["sheet"]!.AsObject().Remove("notebookText"); entry.Delete();
            using var writer=new StreamWriter(zip.CreateEntry("manifest.json").Open()); writer.Write(node.ToJsonString());
        }
        using var workspace=new SessionWorkspace(); var restored=await new ProjectFileService().LoadAsync(path,workspace,default);
        Assert.Equal(new SizeD(165,205),restored.Session.Pages[0].Sheet.Paper.SizeMm); Assert.Null(restored.Session.Pages[0].Sheet.Paper.Notebook);
    }
    [Fact]
    public async Task PreferenceAndApplicationSettingsEditsPreserveIndependentProfilesAndCalibration()
    {
        var a=NotebookProfiles.Default(NotebookVariant.GridSingle) with { Id=Guid.NewGuid(),Name="А",RightMarginMm=19.5 };
        var b=a with { Id=Guid.NewGuid(),Name="Б",RightMarginMm=20.5 };
        var calibration=new PrinterPaperCalibration("HP",new(167,208),false,0.1,-0.2);
        var original=new ApplicationPreferences { NotebookProfiles=[a,b],PaperCalibrations=[calibration],HiddenPaperGuides=["key"] };
        var store=new ApplicationPreferencesStore(ProjectFileTests.PathFor("nb001-prefs.json")); await store.SaveAsync(original,default);
        var loaded=await store.LoadAsync(default); Assert.Equal(new[] { a,b },loaded.NotebookProfiles); Assert.Equal(calibration,Assert.Single(loaded.PaperCalibrations));
        var vm=new ApplicationSettingsViewModel(loaded,[],new Quiet()); vm.Dpi=300; Assert.True(vm.TryAccept());
        Assert.Equal(loaded.NotebookProfiles,vm.Result!.NotebookProfiles); Assert.Equal(loaded.PaperCalibrations,vm.Result.PaperCalibrations); Assert.Equal(new[] { "key" },vm.Result.HiddenPaperGuides);
        vm.ResetPaperGuidesCommand.Execute(null); Assert.True(vm.TryAccept()); Assert.Empty(vm.Result!.HiddenPaperGuides); Assert.Equal(new[] { a,b },vm.Result.NotebookProfiles);
    }
    [Fact]
    public void NotebookEditingUndoAndCancelKeepWholeOverflowingSource()
    {
        using var vm=new PrintSessionViewModel(new Quiet(),ProjectFileTests.PathFor("nb001-undo-recovery"));
        var page=PrintPageItem.CreateBlank(NotebookProfiles.Default(NotebookVariant.GridSpread).ToPaper()); vm.AddPage(page);
        var source=string.Concat(Enumerable.Repeat("Исходная строка\n",600)); vm.SetNotebookStory(page,source,new()); var before=page.Document.Snapshot();
        vm.BeginTextEditing(new(20,10),page.Document.Objects.OfType<TextObject>().First()); vm.EditingText=source+"конец"; vm.FinishTextEditing(false);
        Assert.Equal(before,page.Document.Snapshot());
        vm.BeginTextEditing(new(20,10),page.Document.Objects.OfType<TextObject>().First()); vm.EditingText="Новый текст"; vm.FinishTextEditing();
        Assert.Equal("Новый текст",page.Document.NotebookText!.Source); vm.UndoCommand.Execute(null); Assert.Equal(source,vm.SelectedPage!.Document.NotebookText!.Source);
        vm.RedoCommand.Execute(null); Assert.Equal("Новый текст",vm.SelectedPage.Document.NotebookText!.Source);
    }
    [Fact]
    public async Task UnconfirmedNotebookOverflowIsRejectedBeforeAnyDriverAccess()
    {
        var page=PrintPageItem.CreateBlank(NotebookProfiles.Default(NotebookVariant.RuledSpread).ToPaper()); var story=new NotebookTextStory(Guid.NewGuid(),"Текст",new(FontSizePt:1000));
        var result=NotebookTextLayout.Layout(page.Document.Paper,story,page.Document.Paper.SafeAreaMm); page.Document.SetNotebookText(story,result.Objects);
        var settings=new PrintSettings { PrinterName="This printer does not exist",Paper=PaperPreset.Notebook,Notebook=page.Document.Paper.Notebook,Landscape=true }.Snapshot();
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>new PrintEngine(new(new()),new()).PrintAsync([page.Snapshot()],settings,null,default));
        Assert.Contains("отдельное подтверждение",error.Message);
    }
    private sealed class Quiet : IUserDialogs
    {
        public string[] PickFiles()=>[]; public string? PickPdfOutput()=>null; public string? PickLibreOffice()=>null;
        public bool ConfirmCropping(int count)=>false; public void Error(string message) { }
    }
}
