using PrintMaster.Models;
using PrintMaster.ViewModels;
using System.IO;
using Xunit;

namespace PrintMaster.Tests;

public sealed class ContextActionTests
{
    private static PrintSessionViewModel Session()=>new(new ClipboardTests.TestDialogs(),Path.Combine(Fixtures.Root,"ux006-recovery-"+Guid.NewGuid().ToString("N")),new FakeClipboard());
    private static ShapeObject Shape(double x=10,double y=20)=>new(Guid.NewGuid(),new(new(x,y,30,40)),SheetShapeKind.Rectangle,new("#175E9C",1));

    [Theory]
    [InlineData(1,"acbed")]
    [InlineData(-1,"badce")]
    [InlineData(2,"acebd")]
    [InlineData(-2,"bdace")]
    public void GroupLayerOrderPreservesMembersAndOtherObjectsInOneUndoStep(int direction,string expected)
    {
        using var vm=Session(); var page=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(page); vm.SelectedPage=page;
        var items=Enumerable.Range(0,5).Select(i=>Shape(i*8)).ToArray(); page.Document.AddObjects(items); vm.SetObjectSelection([items[1].Id,items[3].Id]);
        var before=vm.UndoCount;
        Assert.True(vm.OrderSheetSelectionCommand.CanExecute(direction)); vm.OrderSheetSelectionCommand.Execute(direction);
        Assert.Equal(expected.Select(c=>items[c-'a']),page.Document.Objects);
        Assert.Equal(new[] {items[1].Id,items[3].Id},vm.SelectedObjectIds); Assert.Equal(before+1,vm.UndoCount);
        vm.UndoCommand.Execute(null); Assert.Equal(items,page.Document.Objects); vm.RedoCommand.Execute(null);
        Assert.Equal(expected.Select(c=>items[c-'a']),page.Document.Objects);
    }
    [Fact]
    public void BoundaryLayersAreUnavailableAndLockedObjectsAreNotReorderedByTheirOwnSelection()
    {
        using var vm=Session(); var page=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(page); vm.SelectedPage=page;
        var back=Shape(); var front=Shape(50); var locked=Shape(100) with { Locked=true };
        page.Document.AddObjects([back,locked,front]); vm.SetObjectSelection([back.Id,locked.Id]);
        Assert.Equal(new[] {back.Id},vm.SelectedObjectIds); Assert.False(vm.OrderSheetSelectionCommand.CanExecute(-1));
        Assert.False(vm.OrderSheetSelectionCommand.CanExecute(-2));
        vm.SetObjectSelection([front.Id]); Assert.False(vm.OrderSheetSelectionCommand.CanExecute(1)); Assert.False(vm.OrderSheetSelectionCommand.CanExecute(2));
        var count=vm.UndoCount; page.Document.SetObjectsOrder([locked.Id],2);
        Assert.Equal(new SheetObject[] {back,locked,front},page.Document.Objects); Assert.Equal(count,vm.UndoCount);
    }
    [Theory]
    [InlineData("left")][InlineData("center")][InlineData("right")][InlineData("top")][InlineData("middle")][InlineData("bottom")]
    public void AligningAGroupKeepsItsSpacingAndUsesVisualBounds(string alignment)
    {
        using var vm=Session(); var page=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(page); vm.SelectedPage=page;
        var a=Shape(25,30) with { Placement=new(new(25,30,30,40),30) }; var b=Shape(85,95); var locked=Shape(5,8) with { Locked=true };
        page.Document.AddObjects([a,b,locked]); vm.SetObjectSelection([a.Id,b.Id]); var before=vm.UndoCount;
        vm.AlignSheetSelectionCommand.Execute(alignment);
        var moved=vm.SelectedObjects.ToArray(); var bounds=moved.Select(PrintMaster.Services.SheetEditing.VisualBounds).ToArray(); var safe=vm.SafeAreaMm;
        Assert.Equal(b.Placement.BoundsMm.X-a.Placement.BoundsMm.X,moved[1].Placement.BoundsMm.X-moved[0].Placement.BoundsMm.X,8);
        Assert.Equal(b.Placement.BoundsMm.Y-a.Placement.BoundsMm.Y,moved[1].Placement.BoundsMm.Y-moved[0].Placement.BoundsMm.Y,8);
        var actual=alignment switch { "left"=>bounds.Min(x=>x.X),"right"=>bounds.Max(x=>x.Right),"center"=>(bounds.Min(x=>x.X)+bounds.Max(x=>x.Right))/2,"top"=>bounds.Min(x=>x.Y),"bottom"=>bounds.Max(x=>x.Bottom),_=>(bounds.Min(x=>x.Y)+bounds.Max(x=>x.Bottom))/2 };
        var expected=alignment switch { "left"=>safe.X,"right"=>safe.Right,"center"=>safe.X+safe.Width/2,"top"=>safe.Y,"bottom"=>safe.Bottom,_=>safe.Y+safe.Height/2 };
        Assert.Equal(expected,actual,8); Assert.Equal(locked,page.Document.Objects[2]); Assert.Equal(before+1,vm.UndoCount);
        vm.UndoCommand.Execute(null); Assert.Equal(new SheetObject[] {a,b,locked},page.Document.Objects);
    }
    [Fact]
    public void QueueIncludeExcludeCopiesAndBoundaryMovesUseTheHighlightedGroupOnly()
    {
        using var vm=Session(); var pages=Enumerable.Range(0,3).Select(_=>PrintPageItem.CreateBlank(SheetPaper.A4)).ToArray(); foreach(var p in pages) vm.AddPage(p);
        vm.SetQueueSelection(pages.Take(2),pages[0]);
        Assert.False(vm.MoveHighlightedPagesCommand.CanExecute(-1)); Assert.True(vm.MoveHighlightedPagesCommand.CanExecute(1));
        vm.SetHighlightedPrintingCommand.Execute(false); Assert.False(pages[0].IsIncluded); Assert.False(pages[1].IsIncluded); Assert.True(pages[2].IsIncluded);
        vm.UndoCommand.Execute(null); Assert.All(pages,p=>Assert.True(p.IsIncluded)); vm.RedoCommand.Execute(null);
        vm.BatchCopies=7; vm.SetHighlightedCopiesCommand.Execute(null); Assert.Equal(new[] {7,7,1},pages.Select(p=>p.Copies));
        vm.MoveHighlightedPagesCommand.Execute(1); Assert.Equal(new[] {pages[2],pages[0],pages[1]},vm.Pages); Assert.False(vm.MoveHighlightedPagesCommand.CanExecute(1));
        vm.SetHighlightedPrintingCommand.Execute(true); Assert.All(pages,p=>Assert.True(p.IsIncluded));
    }
    [Theory]
    [InlineData(0,120d)][InlineData(1,30d)]
    public void PhotoCropKeepsOriginalAndAnnotationsAndCanBeUndoneAsOneAction(int quarterTurns,double height)
    {
        using var vm=Session(); var page=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(page); vm.SelectedPage=page;
        var path=Fixtures.CreateImage("ux006-crop-"+quarterTurns+".png"); var asset=new SourceAsset(Guid.NewGuid(),SourceKind.Image,path,path,0,1,new(800,600),new(160,120));
        var annotation=Annotation.Create(AnnotationKind.Arrow,new(100,100),new(200,200),"#777777",4);
        var edits=ContentEdits.Empty(asset.LogicalSize) with { QuarterTurns=quarterTurns,Annotations=[annotation] };
        var content=new ContentObject(Guid.NewGuid(),new(new(20,30,80,60),15),asset.Id,edits); page.Document.AddContent(asset,content); vm.SetObjectSelection([content.Id]);
        var original=page.Document.Snapshot(); var count=vm.UndoCount; var crop=new RectD(200,0,400,600);
        vm.ApplyImageCrop(page,content.Id,crop);
        var changed=Assert.IsType<ContentObject>(page.Document.Objects.Single()); Assert.Equal(crop,changed.Edits.Crop);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(Assert.IsType<ContentObject>(original.Objects.Single()).Edits.Annotations),System.Text.Json.JsonSerializer.Serialize(changed.Edits.Annotations));
        Assert.Equal(new RectD(20,30,80,height),changed.Placement.BoundsMm); Assert.Equal(15,changed.Placement.RotationDegrees); Assert.Equal(asset,page.Document.Snapshot().Assets.Single());
        Assert.Equal(count+1,vm.UndoCount); vm.UndoCommand.Execute(null); Assert.Equal(original,page.Document.Snapshot());
        vm.RedoCommand.Execute(null); Assert.Equal(crop,Assert.IsType<ContentObject>(page.Document.Objects.Single()).Edits.Crop);
    }
    [Fact]
    public async Task AddingTextToASourcePhotoCanCancelAndUndoTheEntireLayoutConversion()
    {
        using var vm=Session(); await vm.ImportFilesAsync([Fixtures.CreateImage("ux006-text-in-photo.png")]); var page=vm.SelectedPage!;
        var before=page.Document.Snapshot(); var count=vm.UndoCount;
        vm.BeginTextEditing(new(25,35)); Assert.True(vm.IsEditingText); Assert.True(page.IsSheetLayout);
        vm.EditingText="Отмена"; vm.FinishTextEditing(false); Assert.Equal(before,page.Document.Snapshot()); Assert.Equal(count,vm.UndoCount);
        vm.BeginTextEditing(new(25,35)); vm.EditingText="Текст поверх фото"; vm.FinishTextEditing(); Assert.True(page.IsSheetLayout); Assert.Equal(2,page.Document.Objects.Count);
        Assert.Equal(count+1,vm.UndoCount); vm.UndoCommand.Execute(null); Assert.Equal(before,page.Document.Snapshot());
        vm.RedoCommand.Execute(null); Assert.Equal("Текст поверх фото",page.Document.Objects.OfType<TextObject>().Single().Text);
    }
    [Theory]
    [InlineData(0,"left",22,80)][InlineData(1,"left",60,228)][InlineData(2,"left",318,80)][InlineData(3,"left",60,42)]
    [InlineData(0,"top",60,42)][InlineData(1,"top",22,80)][InlineData(2,"top",60,228)][InlineData(3,"top",318,80)]
    public void SourceAnnotationAlignmentUsesVisibleEdgesAfterRotationAndIncludesStroke(int turns,string alignment,double x,double y)
    {
        var annotation=Annotation.Create(AnnotationKind.Rectangle,new(60,80),new(160,140),"#175E9C",4);
        var aligned=PrintMaster.Services.AnnotationLayout.Align(annotation,new(20,40,400,250),turns,alignment);
        Assert.Equal(new PointD(x,y),aligned.Start); Assert.Equal(new PointD(x+100,y+60),aligned.End); Assert.Equal(annotation.Id,aligned.Id);
        Assert.Equal(annotation.Color,aligned.Color); Assert.Equal(annotation.Thickness,aligned.Thickness);
    }
    [Fact]
    public void RepeatingAlignmentAtTheBoundaryAddsNoUndoStepEvenForPhotoWithAnnotations()
    {
        using var vm=Session(); var page=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(page); vm.SelectedPage=page;
        var path=Fixtures.CreateImage("ux006-noop.png"); var asset=new SourceAsset(Guid.NewGuid(),SourceKind.Image,path,path,0,1,new(800,600),new(160,120));
        var content=new ContentObject(Guid.NewGuid(),new(new(20,30,80,60)),asset.Id,ContentEdits.Empty(asset.LogicalSize) with { Annotations=[Annotation.Create(AnnotationKind.Rectangle,new(50,50),new(80,80),"#175E9C",4)] });
        page.Document.AddContent(asset,content); vm.SetObjectSelection([content.Id]); vm.AlignSheetSelectionCommand.Execute("left");
        var count=vm.UndoCount; var state=page.Document.Snapshot(); vm.AlignSheetSelectionCommand.Execute("left");
        Assert.Equal(count,vm.UndoCount); Assert.Equal(state,page.Document.Snapshot());
        var annotation=Annotation.Create(AnnotationKind.Rectangle,new(22,80),new(122,140),"#175E9C",4);
        Assert.Same(annotation,PrintMaster.Services.AnnotationLayout.Align(annotation,new(20,40,400,250),0,"left"));
    }
}
