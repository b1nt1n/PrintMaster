using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace PrintMaster.Tests;

public sealed class PhotoAspectTests
{
    private static ContentObject GeometryPhoto(double rotation,bool keepAspect)=>new(Guid.NewGuid(),
        new(new(25,35,40,20),rotation),Guid.NewGuid(),
        new(new(80,40,600,300),2,[Annotation.Create(AnnotationKind.Arrow,new(100,100),new(350,220),"#175E9C",4)]))
        { KeepAspectRatio=keepAspect };
    private static ObjectHandle Opposite(ObjectHandle handle)=>handle switch
    {
        ObjectHandle.NorthWest=>ObjectHandle.SouthEast,ObjectHandle.North=>ObjectHandle.South,
        ObjectHandle.NorthEast=>ObjectHandle.SouthWest,ObjectHandle.East=>ObjectHandle.West,
        ObjectHandle.SouthEast=>ObjectHandle.NorthWest,ObjectHandle.South=>ObjectHandle.North,
        ObjectHandle.SouthWest=>ObjectHandle.NorthEast,_=>ObjectHandle.East
    };
    private static PrintPageItem Photo(int turns=0)
    {
        var page=DrawingStyleTests.ImagePage(); var content=page.Document.PrimaryContent!;
        page.Document.ReplaceObject(content with
        {
            Placement=new(new(25,35,40,turns%2==0 ? 20 : 80),37),
            Edits=content.Edits with { Crop=new(80,40,600,300),QuarterTurns=turns,
                Annotations=[Annotation.Create(AnnotationKind.Arrow,new(100,100),new(350,220),"#175E9C",4)] }
        });
        return page;
    }
    private static ContentObject Selected(PrintSessionViewModel session)=>(ContentObject)session.SelectedSheetObject!;
    private static string Annotations(ContentObject item)=>JsonSerializer.Serialize(item.Edits.Annotations);
    private static void AssertPhoto(ContentObject expected,ContentObject actual)
    {
        Assert.Equal(expected.Id,actual.Id); Assert.Equal(expected.AssetId,actual.AssetId);
        Assert.Equal(expected.Placement,actual.Placement); Assert.Equal(expected.KeepAspectRatio,actual.KeepAspectRatio);
        Assert.Equal(expected.Visible,actual.Visible); Assert.Equal(expected.Locked,actual.Locked);
        Assert.Equal(expected.Edits.Crop,actual.Edits.Crop); Assert.Equal(expected.Edits.QuarterTurns,actual.Edits.QuarterTurns);
        Assert.Equal(Annotations(expected),Annotations(actual));
    }

    public static IEnumerable<object[]> ResizeCases()=>
        from rotation in new[] {0d,37d,90d}
        from handle in ObjectTransform.Handles(GeometryPhoto(rotation,true)).Where(h=>h!=ObjectHandle.Rotate)
        from keepAspect in new[] {true,false}
        select new object[] {rotation,handle,keepAspect};

    [Theory] [MemberData(nameof(ResizeCases))]
    public void Resize_AllHandlesAndRotations_HonoursThePhotoFlagAndKeepsTheOppositeAnchor(
        double rotation,ObjectHandle handle,bool keepAspect)
    {
        var original=GeometryPhoto(rotation,keepAspect); var start=ObjectTransform.HandlePoint(original,handle);
        var delta=ObjectTransform.RotateVector(new(17,5),rotation);
        var resized=(ContentObject)ObjectTransform.Resize(original,handle,start,new(start.X+delta.X,start.Y+delta.Y));

        var anchor=ObjectTransform.HandlePoint(original,Opposite(handle));
        var nextAnchor=ObjectTransform.HandlePoint(resized,Opposite(handle));
        Assert.Equal(anchor.X,nextAnchor.X,7); Assert.Equal(anchor.Y,nextAnchor.Y,7);
        if(keepAspect) Assert.Equal(2,resized.Placement.BoundsMm.Width/resized.Placement.BoundsMm.Height,7);
        else Assert.NotEqual(2,resized.Placement.BoundsMm.Width/resized.Placement.BoundsMm.Height);
        Assert.Equal(original.Id,resized.Id); Assert.Equal(original.AssetId,resized.AssetId);
        Assert.Same(original.Edits,resized.Edits); Assert.Equal(rotation,resized.Placement.RotationDegrees);
        Assert.Equal(keepAspect,resized.KeepAspectRatio);
        Assert.True(resized.Placement.BoundsMm.Width>=1); Assert.True(resized.Placement.BoundsMm.Height>=1);
    }

    [Theory] [InlineData(ObjectHandle.East)] [InlineData(ObjectHandle.North)] [InlineData(ObjectHandle.SouthEast)]
    public void Resize_ShiftOnAnUnlockedPhoto_TemporarilyPreservesProportionsWithoutChangingTheFlag(ObjectHandle handle)
    {
        var original=GeometryPhoto(37,false); var start=ObjectTransform.HandlePoint(original,handle);
        var delta=ObjectTransform.RotateVector(new(17,5),37);
        var resized=(ContentObject)ObjectTransform.Resize(original,handle,start,new(start.X+delta.X,start.Y+delta.Y),true);

        Assert.Equal(2,resized.Placement.BoundsMm.Width/resized.Placement.BoundsMm.Height,7);
        Assert.False(resized.KeepAspectRatio); Assert.Same(original.Edits,resized.Edits);
    }

    [Theory] [InlineData(true,"width",80,40)] [InlineData(false,"width",80,20)]
    [InlineData(true,"height",60,30)] [InlineData(false,"height",40,30)]
    public void NumericSize_UsesThePhotoFlagWithoutChangingPositionRotationOrCrop(bool keepAspect,
        string dimension,double expectedWidth,double expectedHeight)
    {
        using var session=DrawingStyleTests.Session(); var page=Photo(); session.AddPage(page);
        session.SetObjectSelection([page.Document.PrimaryContent!.Id]); session.KeepPhotoAspectRatio=keepAspect;
        var before=Selected(session); var undo=session.UndoCount;

        if(dimension=="width") session.ObjectWidth=80; else session.ObjectHeight=30;

        var after=Selected(session); Assert.Equal(expectedWidth,after.Placement.BoundsMm.Width);
        Assert.Equal(expectedHeight,after.Placement.BoundsMm.Height); Assert.Equal(25,after.Placement.BoundsMm.X);
        Assert.Equal(35,after.Placement.BoundsMm.Y); Assert.Equal(37,after.Placement.RotationDegrees);
        Assert.Equal(before.Edits.Crop,after.Edits.Crop); Assert.Equal(before.Edits.QuarterTurns,after.Edits.QuarterTurns);
        Assert.Equal(Annotations(before),Annotations(after)); Assert.Equal(before.KeepAspectRatio,after.KeepAspectRatio);
        Assert.Equal(undo+1,session.UndoCount);
        session.UndoCommand.Execute(null); AssertPhoto(before,Selected(session));
    }

    [Fact]
    public void Toggle_IsOneUndoStepAndNotifiesTheSelectedPhotoWithoutResizing()
    {
        using var session=DrawingStyleTests.Session(); var page=Photo(); session.AddPage(page);
        session.SetObjectSelection([page.Document.PrimaryContent!.Id]); var before=Selected(session); var undo=session.UndoCount;
        var changed=new List<string?>(); session.PropertyChanged+=(_,e)=>changed.Add(e.PropertyName);
        Assert.True(session.CanSetPhotoAspectRatio); Assert.True(session.KeepPhotoAspectRatio);

        session.KeepPhotoAspectRatio=false;

        Assert.False(session.KeepPhotoAspectRatio); AssertPhoto(before with { KeepAspectRatio=false },Selected(session));
        Assert.Equal(undo+1,session.UndoCount); Assert.Contains(nameof(PrintSessionViewModel.KeepPhotoAspectRatio),changed);
        Assert.Contains(nameof(PrintSessionViewModel.CanSetPhotoAspectRatio),changed);
        session.KeepPhotoAspectRatio=false; Assert.Equal(undo+1,session.UndoCount);
        session.UndoCommand.Execute(null); Assert.True(session.KeepPhotoAspectRatio); AssertPhoto(before,Selected(session));
        session.RedoCommand.Execute(null); Assert.False(session.KeepPhotoAspectRatio);
    }

    [Theory] [InlineData("busy")] [InlineData("print-preview")] [InlineData("locked")]
    [InlineData("hidden")] [InlineData("multiple")] [InlineData("shape")]
    public void Toggle_WithoutOneEditablePhoto_DoesNotMutateTheSheet(string condition)
    {
        using var session=DrawingStyleTests.Session(); var page=Photo(); session.AddPage(page);
        var photo=page.Document.PrimaryContent!;
        var shape=new ShapeObject(Guid.NewGuid(),new(new(90,90,20,20)),SheetShapeKind.Rectangle,new());
        page.Document.AddObject(shape); session.SetObjectSelection([photo.Id]);
        switch(condition)
        {
            case "busy": session.IsBusy=true; break;
            case "print-preview": session.ShowPrintPreview=true; break;
            case "locked": page.Document.ReplaceObject(photo with { Locked=true }); break;
            case "hidden": page.Document.ReplaceObject(photo with { Visible=false }); break;
            case "multiple": session.SetObjectSelection([photo.Id,shape.Id]); break;
            default: session.SetObjectSelection([shape.Id]); break;
        }
        var before=page.Document.Snapshot(); var undo=session.UndoCount;
        Assert.False(session.CanSetPhotoAspectRatio);

        session.KeepPhotoAspectRatio=false;

        Assert.Same(before,page.Document.Snapshot()); Assert.Equal(undo,session.UndoCount);
        if(condition=="busy") session.IsBusy=false;
    }

    [Fact]
    public void Toggle_OnAPdfContentObject_IsUnavailable()
    {
        using var session=DrawingStyleTests.Session(); var source=Fixtures.CreatePdf();
        var page=PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Pdf,source,source,0,2,
            new(595.276,841.89),new(210,297)));
        page.Document.SetPlacement(page.Document.PrimaryContent!.Id,new(new(25,35,40,60)));
        session.AddPage(page); session.SetObjectSelection([page.Document.PrimaryContent.Id]);
        var before=page.Document.Snapshot(); var undo=session.UndoCount;

        Assert.False(session.CanSetPhotoAspectRatio); session.KeepPhotoAspectRatio=false;

        Assert.Same(before,page.Document.Snapshot()); Assert.Equal(undo,session.UndoCount);
    }

    [Theory] [InlineData(true,0)] [InlineData(false,0)] [InlineData(true,1)] [InlineData(false,1)]
    public void Crop_LockedPhotoAdjustsHeightWhileUnlockedPhotoRetainsItsPhysicalBox(bool keepAspect,int turns)
    {
        using var session=DrawingStyleTests.Session(); var page=Photo(turns); session.AddPage(page);
        session.SetObjectSelection([page.Document.PrimaryContent!.Id]); session.KeepPhotoAspectRatio=keepAspect;
        var before=Selected(session); var undo=session.UndoCount; var crop=new RectD(100,100,300,300);

        session.ApplyImageCrop(page,before.Id,crop);

        var after=Selected(session); Assert.Equal(crop,after.Edits.Crop);
        Assert.Equal(before.Edits.QuarterTurns,after.Edits.QuarterTurns);
        Assert.Equal(Annotations(before),Annotations(after));
        Assert.Equal(before.KeepAspectRatio,after.KeepAspectRatio);
        Assert.Equal(before.Placement.RotationDegrees,after.Placement.RotationDegrees);
        Assert.Equal(before.Placement.BoundsMm.X,after.Placement.BoundsMm.X);
        Assert.Equal(before.Placement.BoundsMm.Y,after.Placement.BoundsMm.Y);
        Assert.Equal(before.Placement.BoundsMm.Width,after.Placement.BoundsMm.Width);
        if(keepAspect) Assert.Equal(40,after.Placement.BoundsMm.Height,7);
        else Assert.Equal(before.Placement,after.Placement);
        Assert.Equal(undo+1,session.UndoCount);
        session.UndoCommand.Execute(null); AssertPhoto(before,Selected(session));
        session.RedoCommand.Execute(null); AssertPhoto(after,Selected(session));
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task ProjectAndClipboard_RoundTripThePhotoFlagAndExactPlacement(bool keepAspect)
    {
        using var session=DrawingStyleTests.Session(); var page=Photo(); session.AddPage(page);
        session.SetObjectSelection([page.Document.PrimaryContent!.Id]); session.KeepPhotoAspectRatio=keepAspect;
        var before=Selected(session); var path=ProjectFileTests.PathFor("pm009-photo-aspect");
        var files=new ProjectFileService(); await files.SaveAsync(session.CaptureProject(),path,true,default);
        using var workspace=new SessionWorkspace(); var loaded=await files.LoadAsync(path,workspace,default);
        var restored=loaded.Session.Pages.Single().Sheet.PrimaryContent!;

        Assert.Equal(keepAspect,restored.KeepAspectRatio); Assert.Equal(before.Placement,restored.Placement);
        Assert.Equal(before.Edits.Crop,restored.Edits.Crop); Assert.Equal(before.Edits.QuarterTurns,restored.Edits.QuarterTurns);
        Assert.Empty(loaded.Missing);
        var json=ClipboardObjectService.Serialize(ClipboardObjectService.Capture(page.Document.Snapshot(),before));
        var copied=(ContentObject)ClipboardObjectService.Deserialize(json).Object!;
        var cloned=(ContentObject)ClipboardObjectService.CloneObject(copied,new(3,4));
        Assert.Equal(keepAspect,copied.KeepAspectRatio); Assert.Equal(before.Placement,copied.Placement);
        Assert.Equal(keepAspect,cloned.KeepAspectRatio); Assert.Equal(before.AssetId,cloned.AssetId);
        Assert.Equal(new RectD(28,39,40,20),cloned.Placement.BoundsMm);
        Assert.Equal(before.Placement.RotationDegrees,cloned.Placement.RotationDegrees);
    }

    [Fact]
    public async Task OldVersionFiveManifest_WithoutThePhotoFlag_DefaultsToProportionalResizing()
    {
        var page=Photo(); var content=page.Document.PrimaryContent!;
        page.Document.ReplaceObject(content with { KeepAspectRatio=false });
        var path=ProjectFileTests.PathFor("pm009-old-aspect"); var files=new ProjectFileService();
        await files.SaveAsync(ProjectFileTests.Session(page),path,true,default);
        using(var archive=ZipFile.Open(path,ZipArchiveMode.Update))
        {
            var entry=archive.GetEntry("manifest.json")!; JsonObject manifest;
            using(var reader=new StreamReader(entry.Open())) manifest=JsonNode.Parse(reader.ReadToEnd())!.AsObject();
            Assert.Equal(ProjectFileService.CurrentVersion,manifest["version"]!.GetValue<int>());manifest["version"]=5;
            var stored=manifest["session"]!["pages"]![0]!["sheet"]!["objects"]![0]!.AsObject();
            Assert.False(stored["keepAspectRatio"]!.GetValue<bool>()); Assert.True(stored.Remove("keepAspectRatio"));
            entry.Delete(); using var writer=new StreamWriter(archive.CreateEntry("manifest.json").Open());
            writer.Write(manifest.ToJsonString());
        }
        using var workspace=new SessionWorkspace(); var loaded=await files.LoadAsync(path,workspace,default);
        var restored=loaded.Session.Pages.Single().Sheet.PrimaryContent!;

        Assert.True(restored.KeepAspectRatio); Assert.Equal(content.Placement,restored.Placement);
        var start=ObjectTransform.HandlePoint(restored,ObjectHandle.East);
        var delta=ObjectTransform.RotateVector(new(20,0),restored.Placement.RotationDegrees);
        var resized=ObjectTransform.Resize(restored,ObjectHandle.East,start,new(start.X+delta.X,start.Y+delta.Y));
        Assert.Equal(2,resized.Placement.BoundsMm.Width/resized.Placement.BoundsMm.Height,7);
    }
}
