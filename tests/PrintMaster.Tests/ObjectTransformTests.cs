using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.IO;
using Xunit;

namespace PrintMaster.Tests;

public sealed class ObjectTransformTests
{
    private static ShapeObject Shape(double rotation=0)=>new(Guid.NewGuid(),new(new(25,35,40,20),rotation),SheetShapeKind.Rectangle,new("#175E9C",0.6,0.7,"#EEEEEE"));
    public static IEnumerable<object[]> ResizeCases()=>from angle in new[] {0d,30d,90d,179d} from handle in ObjectTransform.Handles(Shape()).Where(h=>h!=ObjectHandle.Rotate) select new object[] {angle,handle};
    private static ObjectHandle Opposite(ObjectHandle handle)=>handle switch
    {
        ObjectHandle.NorthWest=>ObjectHandle.SouthEast,ObjectHandle.North=>ObjectHandle.South,ObjectHandle.NorthEast=>ObjectHandle.SouthWest,ObjectHandle.East=>ObjectHandle.West,
        ObjectHandle.SouthEast=>ObjectHandle.NorthWest,ObjectHandle.South=>ObjectHandle.North,ObjectHandle.SouthWest=>ObjectHandle.NorthEast,_=>ObjectHandle.East
    };
    [Theory] [MemberData(nameof(ResizeCases))]
    public void EveryResizeHandleKeepsTheOppositeAnchorAfterRotation(double angle,ObjectHandle handle)
    {
        var item=Shape(angle); var start=ObjectTransform.HandlePoint(item,handle); var center=ObjectTransform.Center(item);
        var local=ObjectTransform.RotateVector(new(start.X-center.X,start.Y-center.Y),-angle);
        var delta=ObjectTransform.RotateVector(new(Math.Sign(local.X)*12,Math.Sign(local.Y)*9),angle);
        var resized=ObjectTransform.Resize(item,handle,start,new(start.X+delta.X,start.Y+delta.Y));
        var anchor=ObjectTransform.HandlePoint(item,Opposite(handle)); var next=ObjectTransform.HandlePoint(resized,Opposite(handle));
        Assert.Equal(anchor.X,next.X,7); Assert.Equal(anchor.Y,next.Y,7); Assert.Equal(angle,resized.Placement.RotationDegrees);
        Assert.Equal(item.Style,((ShapeObject)resized).Style); Assert.Equal(item.Id,resized.Id);
        Assert.True(resized.Placement.BoundsMm.Width>=item.Placement.BoundsMm.Width); Assert.True(resized.Placement.BoundsMm.Height>=item.Placement.BoundsMm.Height);
        Assert.NotEqual(item.Placement,resized.Placement);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void ShiftPreservesShapeProportionsButFreeResizeDoesNot(bool keepAspect)
    {
        var item=Shape(27); var start=ObjectTransform.HandlePoint(item,ObjectHandle.SouthEast); var delta=ObjectTransform.RotateVector(new(20,4),27);
        var result=ObjectTransform.Resize(item,ObjectHandle.SouthEast,start,new(start.X+delta.X,start.Y+delta.Y),keepAspect);
        var ratio=result.Placement.BoundsMm.Width/result.Placement.BoundsMm.Height;
        if(keepAspect) Assert.Equal(2,ratio,7); else Assert.NotEqual(2,ratio);
    }
    [Theory] [InlineData(ObjectHandle.East)] [InlineData(ObjectHandle.North)] [InlineData(ObjectHandle.SouthEast)]
    public void PhotosPreserveCropAspectAndAnnotationsDuringResize(ObjectHandle handle)
    {
        var original=new ContentObject(Guid.NewGuid(),new(new(25,35,40,20),37),Guid.NewGuid(),ContentEdits.Empty(new(800,600)) with { Crop=new(80,40,600,300),QuarterTurns=2,Annotations=[Annotation.Create(AnnotationKind.Arrow,new(100,100),new(350,220),"#B94712",8)] });
        var start=ObjectTransform.HandlePoint(original,handle); var delta=ObjectTransform.RotateVector(new(17,-8),37);
        var result=(ContentObject)ObjectTransform.Resize(original,handle,start,new(start.X+delta.X,start.Y+delta.Y));
        Assert.Equal(2,result.Placement.BoundsMm.Width/result.Placement.BoundsMm.Height,7); Assert.Same(original.Edits,result.Edits); Assert.Equal(original.AssetId,result.AssetId);
    }
    [Fact]
    public void CrossingTheOppositeCornerClampsWithoutFlippingOrLosingTheObject()
    {
        var item=Shape(42); var start=ObjectTransform.HandlePoint(item,ObjectHandle.NorthWest); var delta=ObjectTransform.RotateVector(new(100,100),42);
        var result=ObjectTransform.Resize(item,ObjectHandle.NorthWest,start,new(start.X+delta.X,start.Y+delta.Y));
        Assert.True(result.Placement.BoundsMm.Width>0); Assert.True(result.Placement.BoundsMm.Height>0);
        var oldAnchor=ObjectTransform.HandlePoint(item,ObjectHandle.SouthEast); var anchor=ObjectTransform.HandlePoint(result,ObjectHandle.SouthEast);
        Assert.Equal(oldAnchor.X,anchor.X,7); Assert.Equal(oldAnchor.Y,anchor.Y,7);
    }
    [Theory] [InlineData(false,50)] [InlineData(true,45)]
    public void RotationKeepsTheCenterAndShiftSnapsTo15Degrees(bool snap,double expected)
    {
        var item=Shape(13); var start=ObjectTransform.HandlePoint(item,ObjectHandle.Rotate); var c=ObjectTransform.Center(item); var v=ObjectTransform.RotateVector(new(start.X-c.X,start.Y-c.Y),37);
        var result=ObjectTransform.Rotate(item,start,new(c.X+v.X,c.Y+v.Y),snap);
        Assert.Equal(expected,result.Placement.RotationDegrees,7); Assert.Equal(item.Placement.BoundsMm,result.Placement.BoundsMm); Assert.Equal(item.Style,((ShapeObject)result).Style);
    }
    [Fact]
    public void TextWidthReflowsAndKeepsAllTextAndTheTopLeftCorner()
    {
        var text=SheetEditing.CreateText(string.Join(" ",Enumerable.Repeat("Текст для переноса строк",12)),new(FontSizePt:14),new(25,35),SheetPaper.A4.SafeAreaMm);
        var start=ObjectTransform.HandlePoint(text,ObjectHandle.TextWidth); var result=(TextObject)ObjectTransform.Resize(text,ObjectHandle.TextWidth,start,new(start.X-90,start.Y));
        Assert.Equal(text.Text,result.Text); Assert.Equal(text.Style,result.Style); Assert.Equal(text.Placement.BoundsMm.X,result.Placement.BoundsMm.X,7); Assert.Equal(text.Placement.BoundsMm.Y,result.Placement.BoundsMm.Y,7);
        Assert.True(result.Placement.BoundsMm.Height>text.Placement.BoundsMm.Height); Assert.False(TextLayoutService.Overflows(result));
        Assert.Empty(ObjectTransform.Handles(text with { Flow=new(Guid.NewGuid(),0,false) }));
    }
    [Fact]
    public void HitTestingUsesTheRotatedRectangleAndCyclesOnlyVisibleUnlockedLayers()
    {
        var rotated=Shape(45); var visual=SheetEditing.VisualBounds(rotated);
        Assert.False(ObjectTransform.Contains(rotated,new(visual.X+0.1,visual.Y+0.1)));
        var bottom=Shape(); var top=Shape(); var locked=Shape() with { Locked=true }; var hidden=Shape() with { Visible=false }; var center=ObjectTransform.Center(bottom);
        var objects=new SheetObject[] {bottom,locked,hidden,top};
        Assert.Equal(top.Id,ObjectTransform.NextHit(objects,center,null)); Assert.Equal(bottom.Id,ObjectTransform.NextHit(objects,center,top.Id)); Assert.Equal(top.Id,ObjectTransform.NextHit(objects,center,bottom.Id));
        Assert.Empty(ObjectTransform.Hits(objects,new(200,200))); Assert.Empty(ObjectTransform.Handles(locked));
    }
    [Fact]
    public async Task TransformHasUndoRedoAndSurvivesThePortableProject()
    {
        using var vm=DrawingStyleTests.Session(); var page=PrintPageItem.CreateBlank(SheetPaper.A4); var item=Shape(); page.Document.AddObject(item); vm.AddPage(page); vm.SetObjectSelection([item.Id]);
        var before=vm.UndoCount; vm.ObjectAngle=37.25; Assert.Equal(before+1,vm.UndoCount); Assert.Equal(37.25,vm.ObjectAngle);
        vm.UndoCommand.Execute(null); Assert.Equal(0,vm.ObjectAngle); vm.RedoCommand.Execute(null); Assert.Equal(37.25,vm.ObjectAngle);
        var path=Path.Combine(Fixtures.Root,"ux009-"+Guid.NewGuid().ToString("N")+".printmaster"); await new ProjectFileService().SaveAsync(vm.CaptureProject(),path,true,default);
        using var workspace=new SessionWorkspace(); var restored=await new ProjectFileService().LoadAsync(path,workspace,default); Assert.Equal(37.25,restored.Session.Pages[0].Sheet.Objects[0].Placement.RotationDegrees);
        var count=vm.UndoCount; vm.ObjectAngle=397.25; vm.ObjectAngle=double.NaN; Assert.Equal(count,vm.UndoCount);
        page.Document.ReplaceObject(page.Document.Objects[0] with { Locked=true }); var frozen=page.Document.Snapshot(); vm.MoveSheetSelection(1,0); vm.ObjectAngle=90; Assert.Equal(frozen,page.Document.Snapshot());
    }
    [Fact]
    public void SourcePhotoPlacementIsOneUndoStepAndPreservesOriginalEdits()
    {
        using var vm=DrawingStyleTests.Session(); var page=DrawingStyleTests.ImagePage(); page.Edits.SetCrop(new(60,50,650,500)); page.RotationQuarterTurns=1;
        page.Edits.Add(Annotation.Create(AnnotationKind.Ellipse,new(100,100),new(300,300),"#C62828",6)); vm.AddPage(page); var source=page.Document.Snapshot(); var count=vm.UndoCount;
        Assert.True(vm.CanPlaceSourceImage); vm.PlaceSourceImageCommand.Execute(null); Assert.True(page.IsSheetLayout); Assert.Equal(count+1,vm.UndoCount);
        Assert.Equal(source.PrimaryContent!.Edits,page.Document.PrimaryContent!.Edits); Assert.Equal(source.PrimaryContent.Id,vm.SelectedSheetObjectId); Assert.Equal(source.PrimaryAsset,page.Document.PrimaryAsset);
        vm.UndoCommand.Execute(null); Assert.False(page.IsSheetLayout); Assert.Equal(source.PrimaryContent!.Placement,page.Document.PrimaryContent!.Placement);
        vm.RedoCommand.Execute(null); Assert.True(page.IsSheetLayout); Assert.False(vm.PlaceSourceImageCommand.CanExecute(null));
    }
}
