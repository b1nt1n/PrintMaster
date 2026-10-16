using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Xunit;

namespace PrintMaster.Tests;

public sealed class InkToolSettingsTests
{
    private static PrintSessionViewModel Session()=>ClipboardTests.CreateVm(new(),new());

    [Fact]
    public void BrushMarkerAndEraserHaveIndependentSizesAndOpacity()
    {
        using var vm=Session(); vm.AddPage(PrintPageItem.CreateBlank(SheetPaper.A4));
        vm.BrushThickness=8; vm.BrushOpacity=0.8; vm.MarkerThickness=32; vm.MarkerOpacity=0.2; vm.EraserDiameter=60;
        vm.Tool=EditorTool.Brush;
        Assert.Equal(8,vm.InkStrokeThickness); Assert.Equal(0.8,vm.InkOpacity);
        vm.Tool=EditorTool.Marker;
        Assert.Equal(32,vm.InkStrokeThickness); Assert.Equal(0.2,vm.InkOpacity);
        vm.Tool=EditorTool.Eraser; Assert.Equal(60/SheetEditing.DipPerMm,vm.InkToolSize,10);
        vm.Tool=EditorTool.Brush; Assert.Equal(8,vm.BrushThickness); Assert.Equal(32,vm.MarkerThickness); Assert.Equal(60,vm.EraserDiameter);
    }

    [Fact]
    public void SourceSizesStayInNormalizedCoordinatesAndSheetSizesUseMillimetres()
    {
        using var vm=Session(); var source=PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Image,"missing.png",null,0,1,new(1000,750),new(200,150)));
        vm.AddPage(source); vm.Tool=EditorTool.Brush; vm.InkToolSize=12;
        Assert.Equal(12,vm.StrokeThickness); Assert.Equal(12,vm.InkToolSize); Assert.Equal("ед.",vm.InkToolSizeUnit);
        var sheet=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(sheet); vm.SelectedPage=sheet; vm.Tool=EditorTool.Brush;
        Assert.Equal(12/SheetEditing.DipPerMm,vm.InkToolSize,10); Assert.Equal("мм",vm.InkToolSizeUnit);
        vm.InkToolSize=3; Assert.Equal(3,vm.SheetInkStrokeWidthMm,10); Assert.Equal(3*SheetEditing.DipPerMm,vm.BrushThickness,10);
        vm.Tool=EditorTool.Marker; vm.InkToolSize=6;
        Assert.Equal(6,vm.SheetInkStrokeWidthMm,10); Assert.Equal(3*SheetEditing.DipPerMm,vm.BrushThickness,10);
        vm.Tool=EditorTool.Eraser; vm.InkToolSize=10; Assert.Equal(10,vm.SheetEraserDiameterMm,10);
    }

    [Theory]
    [InlineData(0)] [InlineData(0.25)] [InlineData(1)]
    public void ZeroQuarterAndFullOpacityCanBeSetForNewStrokes(double opacity)
    {
        using var vm=Session(); vm.BrushOpacity=opacity; vm.MarkerOpacity=opacity; vm.Tool=EditorTool.Brush;
        Assert.Equal(opacity,vm.InkOpacity); vm.Tool=EditorTool.Marker; Assert.Equal(opacity,vm.InkOpacity);
        vm.Tool=EditorTool.Rectangle; Assert.Equal(1,vm.InkOpacity);
    }

    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(-1)] [InlineData(2)]
    public void InvalidOpacityDoesNotChangeDefaultsOrDirtyTheProject(double value)
    {
        using var vm=Session(); vm.BrushOpacity=value; vm.MarkerOpacity=value;
        Assert.Equal(1,vm.BrushOpacity); Assert.Equal(85d/255,vm.MarkerOpacity); Assert.False(vm.HasUnsavedChanges);
    }

    [Theory]
    [InlineData(double.NaN)] [InlineData(double.NegativeInfinity)] [InlineData(-1)] [InlineData(0)] [InlineData(1001)]
    public void InvalidDiameterDoesNotReplaceLastValidSettings(double value)
    {
        using var vm=Session(); vm.BrushThickness=value; vm.MarkerThickness=value; vm.EraserDiameter=value;
        Assert.Equal(4,vm.BrushThickness); Assert.Equal(16,vm.MarkerThickness); Assert.Equal(30,vm.EraserDiameter); Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public void SizeAndOpacityDefaultsDoNotRetroactivelyChangeObjectsOrObjectUndo()
    {
        using var vm=Session(); var page=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(page);
        var stroke=new InkObject(Guid.NewGuid(),new(new(10,10,40,20)),[new(0,0),new(1,1)],new("#175E9C",2,0.6));
        page.Document.AddObject(stroke); var before=vm.UndoCount;
        vm.BrushThickness=20; vm.BrushOpacity=0.1; vm.MarkerThickness=40; vm.MarkerOpacity=0.2; vm.EraserDiameter=80;
        Assert.Equal(stroke,Assert.Single(page.Document.Objects)); Assert.Equal(before,vm.UndoCount);
        vm.UndoCommand.Execute(null); Assert.Empty(page.Document.Objects);
        vm.RedoCommand.Execute(null); Assert.Equal(stroke,Assert.Single(page.Document.Objects)); Assert.Equal(0.1,vm.BrushOpacity); Assert.Equal(80,vm.EraserDiameter);
    }

    [Fact]
    public void ChangingToolAndDefaultsNotifiesBothCanvasBindingsAndContextualFields()
    {
        using var vm=Session(); vm.AddPage(PrintPageItem.CreateBlank(SheetPaper.A4));
        var properties=new HashSet<string>(); vm.PropertyChanged+=(_,e)=>{ if(e.PropertyName!=null) properties.Add(e.PropertyName); };
        vm.Tool=EditorTool.Marker;
        Assert.Contains(nameof(vm.InkStrokeThickness),properties); Assert.Contains(nameof(vm.InkOpacity),properties);
        Assert.Contains(nameof(vm.InkToolContextKey),properties); Assert.Contains(nameof(vm.ShowInkPaintSettings),properties);
        properties.Clear(); vm.MarkerThickness=25; vm.MarkerOpacity=0;
        Assert.Contains(nameof(vm.SheetInkStrokeWidthMm),properties); Assert.Contains(nameof(vm.InkToolSize),properties);
        Assert.Contains(nameof(vm.InkOpacityPercent),properties); Assert.Contains(nameof(vm.IsInvisibleInk),properties); Assert.True(vm.IsInvisibleInk);
        vm.Tool=EditorTool.Eraser; Assert.False(vm.IsInvisibleInk); properties.Clear(); vm.EraserDiameter=40;
        Assert.Contains(nameof(vm.SheetEraserDiameterMm),properties); Assert.Contains(nameof(vm.InkToolSize),properties);
    }

    [Fact]
    public async Task SavingAndLoadingRestoresAllFourOptionsAndDirtyCheckpoint()
    {
        using var vm=Session(); vm.AddPage(PrintPageItem.CreateBlank(SheetPaper.A4));
        vm.BrushOpacity=0.7; vm.MarkerThickness=37; vm.MarkerOpacity=0.43; vm.EraserDiameter=51;
        var path=ProjectFileTests.PathFor("pm012-options"); Assert.True(await vm.SaveProjectToAsync(path)); Assert.False(vm.HasUnsavedChanges);
        vm.MarkerThickness=41; Assert.True(vm.HasUnsavedChanges); vm.MarkerThickness=37; Assert.False(vm.HasUnsavedChanges);
        vm.EraserDiameter=52; Assert.True(vm.HasUnsavedChanges); vm.EraserDiameter=51; Assert.False(vm.HasUnsavedChanges);
        vm.BrushOpacity=0.8; Assert.True(vm.HasUnsavedChanges); vm.BrushOpacity=0.7; Assert.False(vm.HasUnsavedChanges);
        vm.MarkerOpacity=0.5; Assert.True(vm.HasUnsavedChanges); vm.MarkerOpacity=0.43; Assert.False(vm.HasUnsavedChanges);
        using var restored=Session(); Assert.True(await restored.OpenProjectFromAsync(path));
        Assert.Equal(0.7,restored.BrushOpacity); Assert.Equal(37,restored.MarkerThickness); Assert.Equal(0.43,restored.MarkerOpacity); Assert.Equal(51,restored.EraserDiameter);
        Assert.False(restored.HasUnsavedChanges);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public async Task ProjectsWithoutInkOptionsUseCompatibleDefaults(int version)
    {
        var page=PrintPageItem.CreateBlank(SheetPaper.A4); var original=ProjectFileTests.Session(page); var path=ProjectFileTests.PathFor("pm012-legacy");
        var service=new ProjectFileService(); await service.SaveAsync(original,path,true,default);
        using(var archive=ZipFile.Open(path,ZipArchiveMode.Update))
        {
            var entry=archive.GetEntry("manifest.json")!; JsonObject root;
            using(var reader=new StreamReader(entry.Open())) root=JsonNode.Parse(reader.ReadToEnd())!.AsObject(); entry.Delete(); root["version"]=version;
            var editor=root["session"]!["editor"]!.AsObject(); foreach(var key in new[] {"brushOpacity","markerThickness","markerOpacity","eraserDiameter"}) editor.Remove(key);
            using var writer=new StreamWriter(archive.CreateEntry("manifest.json").Open()); writer.Write(root.ToJsonString());
        }
        using var workspace=new SessionWorkspace(); var restored=await service.LoadAsync(path,workspace,default);
        Assert.Equal(7,restored.Session.Editor.StrokeThickness); Assert.Equal(28,restored.Session.Editor.MarkerThickness);
        Assert.Equal(1,restored.Session.Editor.BrushOpacity); Assert.Equal(85d/255,restored.Session.Editor.MarkerOpacity); Assert.Equal(30,restored.Session.Editor.EraserDiameter);
    }

    [Theory]
    [InlineData("brushOpacity",-0.1)] [InlineData("brushOpacity",1.01)] [InlineData("markerOpacity",-1)] [InlineData("markerOpacity",2)]
    [InlineData("markerThickness",0)] [InlineData("markerThickness",1001)] [InlineData("eraserDiameter",0)] [InlineData("eraserDiameter",1001)]
    [InlineData("brushOpacity",double.NaN)] [InlineData("markerOpacity",double.PositiveInfinity)]
    [InlineData("markerThickness",double.NaN)] [InlineData("eraserDiameter",double.NegativeInfinity)]
    public void ProjectValidationRejectsInvalidInkSettings(string property,double value)
    {
        var session=ProjectFileTests.Session(PrintPageItem.CreateBlank(SheetPaper.A4));
        var editor=property switch {
            "brushOpacity"=>session.Editor with { BrushOpacity=value },"markerOpacity"=>session.Editor with { MarkerOpacity=value },
            "markerThickness"=>session.Editor with { MarkerThickness=value },_=>session.Editor with { EraserDiameter=value }
        };
        Assert.Throws<InvalidDataException>(()=>ProjectFileService.ValidateSession(session with { Editor=editor }));
    }

    [Fact]
    public void EraserAndWholeObjectDeletionAreDistinctToolsWithOnlyApplicableSettings()
    {
        using var vm=Session(); vm.AddPage(PrintPageItem.CreateBlank(SheetPaper.A4));
        vm.Tool=EditorTool.Eraser; Assert.True(vm.ShowInkToolSettings); Assert.True(vm.ShowInkSizeSettings); Assert.False(vm.ShowInkPaintSettings);
        vm.Tool=EditorTool.ObjectEraser; Assert.True(vm.ShowInkToolSettings); Assert.False(vm.ShowInkSizeSettings); Assert.False(vm.ShowInkPaintSettings);
        vm.Tool=EditorTool.Brush; Assert.True(vm.ShowInkPaintSettings); Assert.False(vm.ShowShapeStrokeDefaults);
        vm.Tool=EditorTool.Rectangle; Assert.False(vm.ShowInkToolSettings); Assert.True(vm.ShowShapeStrokeDefaults);
    }
}
