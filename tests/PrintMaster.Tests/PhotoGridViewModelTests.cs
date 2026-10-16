using PrintMaster.Models;
using PrintMaster.ViewModels;
using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace PrintMaster.Tests;

public sealed class PhotoGridViewModelTests
{
    private static PhotoGridRequest Request(int count=1,string? path=null)
    {
        var asset=new SourceAsset(Guid.NewGuid(),SourceKind.Image,path ?? "grid-model.png",path ?? "grid-model.png",0,1,new(1000,750),new(200,150));
        var objects=Enumerable.Range(0,count).Select(_=>new ContentObject(Guid.NewGuid(),new(new(10,10,40,30)),asset.Id,ContentEdits.Empty(asset.LogicalSize))).Cast<SheetObject>().ToImmutableArray();
        return new(new(Guid.NewGuid(),SheetPaper.A4,[asset],objects,null),objects.Select(o=>o.Id).ToImmutableArray(),new(5,5,200,287));
    }
    private static BitmapSource Pixel(byte value)
    {
        var image=BitmapSource.Create(1,1,96,96,PixelFormats.Bgra32,null,new byte[] {value,value,value,255},4); image.Freeze(); return image;
    }

    [Fact]
    public void OnePhotoDefaultsToFourRepeatsWithoutCreatingExtraAssets()
    {
        var request=Request(); using var vm=new PhotoGridViewModel(request,false);
        Assert.True(vm.CanApply); Assert.True(vm.CanRepeat); Assert.True(vm.Repeat); Assert.Equal("2x2",vm.SelectedPreset);
        Assert.Equal(4,vm.Plan!.Photos.Length); Assert.Equal(4,vm.DraftSheet!.Objects.Length); Assert.Single(vm.DraftSheet.Assets);
        Assert.Equal(request.PhotoIds[0],vm.Plan.Photos[0].Id); Assert.All(vm.Plan.Photos,p=>Assert.Equal(request.Sheet.Assets[0].Id,p.AssetId));
        Assert.Equal(4,vm.Plan.Photos.Select(p=>p.Id).Distinct().Count()); Assert.Contains("Копии печати",vm.Summary); Assert.False(vm.IsPreviewLoading); Assert.Null(vm.Preview);
        Assert.Same(request.Sheet,vm.Request.Sheet); Assert.Single(request.Sheet.Objects);
    }

    [Theory]
    [InlineData(2,1,2)] [InlineData(3,2,2)] [InlineData(6,2,3)] [InlineData(9,3,3)] [InlineData(100,10,10)]
    public void MultiplePhotosChooseACompactGridAndDisableRepeat(int count,int rows,int columns)
    {
        using var vm=new PhotoGridViewModel(Request(count),false);
        Assert.True(vm.CanApply); Assert.Equal(rows,vm.Rows); Assert.Equal(columns,vm.Columns); Assert.False(vm.CanRepeat); Assert.False(vm.Repeat);
        Assert.Equal(count,vm.Plan!.Photos.Length); vm.Repeat=true; Assert.False(vm.Repeat); Assert.Equal(count,vm.Plan.Photos.Length);
        Assert.Equal(count,vm.DraftSheet!.Objects.Length);
    }

    [Theory]
    [InlineData("2x2",2,2)] [InlineData("2x3",2,3)] [InlineData("3x3",3,3)]
    public void PresetsChangeBothDimensionsAndCustomDimensionsDoNotResetTheOtherAxis(string preset,int rows,int columns)
    {
        using var vm=new PhotoGridViewModel(Request(),false); vm.SelectedPreset=preset;
        Assert.Equal(rows,vm.Rows); Assert.Equal(columns,vm.Columns); Assert.Equal(rows*columns,vm.Plan!.Photos.Length);
        vm.Rows=4; Assert.Equal("custom",vm.SelectedPreset); Assert.Equal(columns,vm.Columns); Assert.Equal(4*columns,vm.Plan.Photos.Length);
        vm.Columns=5; Assert.Equal(4,vm.Rows); Assert.Equal(20,vm.Plan.Photos.Length);
    }

    [Fact]
    public void InvalidGeometryBlocksApplyThenRecoversWithoutDiscardingPhotos()
    {
        var request=Request(6); using var vm=new PhotoGridViewModel(request,false); vm.SelectedPreset="2x2";
        Assert.False(vm.CanApply); Assert.Null(vm.Plan); Assert.Null(vm.DraftSheet); Assert.Contains("6",vm.Error); Assert.Equal(6,request.Sheet.Objects.Length);
        vm.SelectedPreset="2x3"; Assert.True(vm.CanApply); Assert.Empty(vm.Error); Assert.Equal(6,vm.Plan!.Photos.Length);
        vm.GapMm=500; Assert.False(vm.CanApply); Assert.NotEmpty(vm.Error); vm.GapMm=3; Assert.True(vm.CanApply);
        vm.Rows=0; Assert.False(vm.CanApply); vm.Rows=2; Assert.True(vm.CanApply);
        vm.InsetMm=double.NaN; Assert.False(vm.CanApply); vm.InsetMm=0; Assert.True(vm.CanApply);
        vm.Scaling=ScaleMode.Stretch; Assert.False(vm.CanApply); vm.Scaling=ScaleMode.Fill; Assert.True(vm.CanApply); Assert.True(vm.ShowFillNotice);
    }

    [Fact]
    public void EmptyOrDefaultSelectionsExplainTheProblemInsteadOfCrashingTheEditor()
    {
        var request=Request(); using var empty=new PhotoGridViewModel(request with { PhotoIds=[] },false);
        using var missing=new PhotoGridViewModel(request with { PhotoIds=default },false);
        Assert.False(empty.CanApply); Assert.NotEmpty(empty.Error); Assert.False(empty.CanRepeat);
        Assert.False(missing.CanApply); Assert.NotEmpty(missing.Error); Assert.False(missing.CanRepeat);
    }

    [Fact]
    public void DraftKeepsEveryPhotoInItsOriginalStackSlotWithOtherObjectsBetweenThem()
    {
        var request=Request(2); var photoA=request.Sheet.Objects[0]; var photoB=request.Sheet.Objects[1];
        var before=new ShapeObject(Guid.NewGuid(),new(new(5,5,10,10)),SheetShapeKind.Rectangle,new());
        var between=new TextObject(Guid.NewGuid(),new(new(10,50,50,20)),"Текст между фото",new());
        var after=new InkObject(Guid.NewGuid(),new(new(20,90,20,20)),[new(0,0),new(1,1)],new());
        request=request with {Sheet=request.Sheet with {Objects=[before,photoA,between,photoB,after]}};
        using var vm=new PhotoGridViewModel(request,false);
        Assert.Equal(new[] {before.Id,photoA.Id,between.Id,photoB.Id,after.Id},vm.DraftSheet!.Objects.Select(o=>o.Id));
        Assert.Equal(before,vm.DraftSheet.Objects[0]); Assert.Equal(between,vm.DraftSheet.Objects[2]); Assert.Equal(after,vm.DraftSheet.Objects[4]);
        Assert.Equal(vm.Plan!.Photos[0],vm.DraftSheet.Objects[1]); Assert.Equal(vm.Plan.Photos[1],vm.DraftSheet.Objects[3]);
        Assert.Equal(new[] {before.Id,photoA.Id,between.Id,photoB.Id,after.Id},request.Sheet.Objects.Select(o=>o.Id));
    }

    [Fact]
    public void RepeatedCopiesOccupyTheirOriginalPhotoSlotWithoutMovingOtherPhotos()
    {
        var request=Request(2); var photoA=request.Sheet.Objects[0]; var photoB=request.Sheet.Objects[1];
        var between=new TextObject(Guid.NewGuid(),new(new(10,50,50,20)),"Над вторым фото",new());
        var after=new ShapeObject(Guid.NewGuid(),new(new(20,90,20,20)),SheetShapeKind.Ellipse,new());
        request=request with {PhotoIds=[photoB.Id],Sheet=request.Sheet with {Objects=[photoA,between,photoB,after]}};
        using var vm=new PhotoGridViewModel(request,false);
        Assert.True(vm.Repeat); Assert.Equal(photoA,vm.DraftSheet!.Objects[0]); Assert.Equal(between,vm.DraftSheet.Objects[1]);
        Assert.Equal(vm.Plan!.Photos.Select(p=>p.Id),vm.DraftSheet.Objects.Skip(2).Take(4).Select(p=>p.Id)); Assert.Equal(after,vm.DraftSheet.Objects[6]);
        Assert.Equal(new[] {photoA.Id,between.Id,photoB.Id,after.Id},request.Sheet.Objects.Select(o=>o.Id));
    }

    [Fact]
    public void FillPreviewPlanKeepsAnnotationsAndQuarterTurnsWithoutChangingOriginalCrop()
    {
        var request=Request(); var original=(ContentObject)request.Sheet.Objects[0];
        var annotation=Annotation.Create(AnnotationKind.Arrow,new(10,20),new(900,500),"#175E9C",4);
        original=original with {Placement=original.Placement with {RotationDegrees=15},Edits=original.Edits with {Crop=new(50,70,900,600),QuarterTurns=1,Annotations=[annotation]}};
        request=request with {Sheet=request.Sheet with {Objects=[original]}};
        using var vm=new PhotoGridViewModel(request,false); vm.Scaling=ScaleMode.Fill;
        Assert.All(vm.Plan!.Photos,p=>{
            Assert.Equal(0,p.Placement.RotationDegrees); Assert.Equal(1,p.Edits.QuarterTurns); Assert.Equal(annotation,Assert.Single(p.Edits.Annotations));
            Assert.NotEqual(original.Edits.Crop,p.Edits.Crop); Assert.Equal(p.Placement.BoundsMm,vm.Plan.Cells[vm.Plan.Photos.IndexOf(p)]);
        });
        Assert.Equal(original,Assert.Single(request.Sheet.Objects)); Assert.Equal(new RectD(50,70,900,600),original.Edits.Crop);
        Assert.Contains("90",vm.RotationNotice); Assert.Contains("Исходник",vm.FillNotice);
    }

    [Fact]
    public async Task PreviewPendingAllowsApplyButFailurePreservesPlanAndDisablesApply()
    {
        using var vm=new PhotoGridViewModel(Request(),previewRenderer:(_,_)=>throw new IOException("Fixture: недоступно фото"));
        Assert.True(vm.CanApply); Assert.True(vm.IsPreviewLoading); await vm.PreviewTask;
        Assert.False(vm.CanApply); Assert.NotNull(vm.Plan); Assert.False(vm.IsPreviewLoading); Assert.Null(vm.Preview); Assert.True(vm.HasPreviewError); Assert.Contains("недоступно",vm.PreviewError);
    }

    [Fact]
    public async Task CanceledOlderPreviewCannotReplaceTheLatestLayoutEvenWhenRendererIgnoresCancellation()
    {
        var entered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var calls=0;
        using var vm=new PhotoGridViewModel(Request(),previewRenderer:(_,_)=>{
            if(Interlocked.Increment(ref calls)==1) { entered.SetResult(true); release.Task.GetAwaiter().GetResult(); return Pixel(10); }
            return Pixel(200);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var old=vm.PreviewTask;
        try
        {
            vm.Rows=3; await vm.PreviewTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(vm.CanApply); Assert.Equal(6,vm.Plan!.Photos.Length); var latest=vm.Preview;
            Assert.NotNull(latest); var pixels=new byte[4]; latest.CopyPixels(pixels,4,0); Assert.Equal(200,pixels[0]);
            release.TrySetResult(true); await old.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Same(latest,vm.Preview); Assert.False(vm.IsPreviewLoading);
        }
        finally {release.TrySetResult(true);}
    }

    [Fact]
    public async Task DisposedEditorIgnoresLatePreviewAndCannotApply()
    {
        var entered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm=new PhotoGridViewModel(Request(),previewRenderer:(_,_)=>{entered.SetResult(true);release.Task.GetAwaiter().GetResult();return Pixel(50);});
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var pending=vm.PreviewTask; vm.Dispose(); vm.Dispose();
            release.TrySetResult(true); await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(vm.CanApply); Assert.False(vm.IsPreviewLoading); Assert.Null(vm.Preview); Assert.False(vm.HasPreviewError);
        }
        finally {release.TrySetResult(true);vm.Dispose();}
    }

    [Fact]
    public async Task RealPreviewIsFrozenAndLightweightAndLeavesOriginalPhotoUnchanged()
    {
        var path=Fixtures.CreateImage("pm013-vm-"+Guid.NewGuid().ToString("N")+".png"); var original=SHA256.HashData(File.ReadAllBytes(path));
        using var vm=new PhotoGridViewModel(Request(path:path)); await vm.PreviewTask.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Empty(vm.PreviewError); var image=Assert.IsAssignableFrom<BitmapSource>(vm.Preview); Assert.True(image.IsFrozen);
        Assert.InRange(image.PixelWidth,1,1000); Assert.InRange(image.PixelHeight,1,1000); Assert.Equal(original,SHA256.HashData(File.ReadAllBytes(path)));
        Assert.True(vm.CanApply); Assert.False(vm.IsPreviewLoading); Assert.Equal(4,vm.DraftSheet!.Objects.Length);
    }
}
