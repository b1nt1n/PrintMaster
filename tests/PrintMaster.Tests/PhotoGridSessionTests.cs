using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Xunit;

namespace PrintMaster.Tests;

public sealed class PhotoGridSessionTests
{
    private sealed class Dialogs:IUserDialogs
    {
        public Func<PhotoGridRequest,PhotoGridOptions?> Pick {get;set;}=_=>new(2,2,3,0,ScaleMode.Fit,true);
        public PhotoGridRequest? Last {get;private set;}
        public PhotoGridOptions? PickPhotoGrid(PhotoGridRequest request){Last=request;return Pick(request);}
        public string[]PickFiles()=>[];public string?PickPdfOutput()=>null;public string?PickLibreOffice()=>null;
        public bool ConfirmCropping(int count)=>false;public void Error(string message)=>throw new Exception(message);
    }
    private static PrintSessionViewModel Session(Dialogs dialogs)=>new(dialogs,Path.Combine(Fixtures.Root,"pm013-recovery-"+Guid.NewGuid().ToString("N")));
    [Fact]
    public void SourceRepeat_BecomesFixedSheetInCurrentPaper_WithOneUndoAndUnchangedAsset()
    {
        var dialogs=new Dialogs();using var vm=Session(dialogs);var page=StretchTests.Scene();vm.AddPage(page);vm.Settings.Paper=PaperPreset.A5;
        var original=page.Document.Snapshot();var undo=vm.UndoCount;var bytes=File.ReadAllBytes(page.WorkingPath);
        vm.ArrangePhotoGridCommand.Execute(null);
        Assert.Equal(new SizeD(148,210),page.Document.Paper.SizeMm);Assert.True(page.IsSheetLayout);Assert.Equal(4,page.Document.Objects.Count);
        Assert.Single(page.Document.Snapshot().Assets);Assert.Equal(original.Assets[0],page.Document.Snapshot().Assets[0]);
        Assert.Equal(undo+1,vm.UndoCount);Assert.Equal(4,vm.SelectedObjectIds.Length);Assert.Equal(original.PrimaryContentId,page.Document.PrimaryContent!.Id);
        Assert.All(page.Document.Objects,o=>Assert.Equal(PlacementMode.Fixed,o.Placement.Mode));Assert.Equal(bytes,File.ReadAllBytes(page.WorkingPath));
        vm.UndoCommand.Execute(null);Assert.False(page.IsSheetLayout);Assert.Equal(original,page.Document.Snapshot());
        vm.RedoCommand.Execute(null);Assert.Equal(4,page.Document.Objects.Count);Assert.Equal(new SizeD(148,210),page.Document.Paper.SizeMm);
    }
    [Fact]
    public void CancelAndStaleDialog_NeverApplyOldPlan()
    {
        var dialogs=new Dialogs{Pick=_=>null};using var vm=Session(dialogs);var page=StretchTests.Scene();vm.AddPage(page);
        var original=page.Document.Snapshot();var undo=vm.UndoCount;vm.ArrangePhotoGridCommand.Execute(null);
        Assert.Same(original,page.Document.Snapshot());Assert.Equal(undo,vm.UndoCount);
        dialogs.Pick=_=>{page.Edits.SetCrop(new(0,0,200,100));return new(2,2,3,0,ScaleMode.Fit,true);};vm.ArrangePhotoGridCommand.Execute(null);
        Assert.Single(page.Document.Objects);Assert.Equal(new RectD(0,0,200,100),page.Edits.Crop);Assert.Contains("изменились",vm.Status);
    }
    [Fact]
    public void InvalidAndMixedSelection_ReportWithoutChangingDocument()
    {
        var dialogs=new Dialogs{Pick=_=>new(0,2,3,0,ScaleMode.Fit,true)};using var vm=Session(dialogs);var page=StretchTests.Scene();vm.AddPage(page);
        var before=page.Document.Snapshot();vm.ArrangePhotoGridCommand.Execute(null);Assert.Same(before,page.Document.Snapshot());Assert.False(string.IsNullOrWhiteSpace(vm.Status));
        vm.PlaceSourceImageCommand.Execute(null);var photo=page.Document.PrimaryContent!;var text=new TextObject(Guid.NewGuid(),new(new(10,10,50,10)),"Оставить",new());page.Document.AddObject(text);
        vm.SetObjectSelection([photo.Id,text.Id]);Assert.False(vm.CanArrangePhotoGrid);Assert.False(vm.ArrangePhotoGridCommand.CanExecute(null));
        vm.SetObjectSelection([photo.Id]);Assert.True(vm.CanArrangePhotoGrid);vm.ShowPrintPreview=true;Assert.False(vm.CanArrangePhotoGrid);vm.ShowPrintPreview=false;
        page.Document.ReplaceObject(photo with {Locked=true});Assert.False(vm.CanArrangePhotoGrid);
    }
    [Fact]
    public async Task RepeatPortableProject_EmbedsOneSource_AndPreservesEditsAndCopies()
    {
        var dialogs=new Dialogs{Pick=_=>new(2,3,2,1,ScaleMode.Fill,true)};using var vm=Session(dialogs);var page=StretchTests.Scene();
        var annotation=Annotation.Create(AnnotationKind.Arrow,new(20,20),new(130,70),"#111827",4) with {Opacity=.5};page.Edits.Add(annotation);page.Copies=3;
        vm.AddPage(page);vm.ArrangePhotoGridCommand.Execute(null);var objects=page.Document.Objects.OfType<ContentObject>().ToArray();Assert.Equal(6,objects.Length);Assert.Equal(3,page.Copies);
        var path=ProjectFileTests.PathFor("pm013-repeat");await new ProjectFileService().SaveAsync(vm.CaptureProject(),path,true,default);
        using(var zip=ZipFile.OpenRead(path)){using var reader=new StreamReader(zip.GetEntry("manifest.json")!.Open());Assert.Single(JsonNode.Parse(await reader.ReadToEndAsync())!["resources"]!.AsArray());}
        using var workspace=new SessionWorkspace();var loaded=await new ProjectFileService().LoadAsync(path,workspace,default);
        var restored=loaded.Session.Pages[0];Assert.Equal(3,restored.Copies);Assert.Equal(6,restored.Sheet.Objects.Length);Assert.Single(restored.Sheet.Assets);
        Assert.All(restored.Sheet.Objects.OfType<ContentObject>(),p=>{Assert.Equal(annotation.Id,Assert.Single(p.Edits.Annotations).Id);Assert.Equal(.5,p.Edits.Annotations[0].Opacity);});
    }
}
