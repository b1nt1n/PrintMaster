using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Xunit;

namespace PrintMaster.Tests;

public sealed class UnifiedTextTests
{
    private static PrintSessionViewModel Session()=>new(new ClipboardTests.TestDialogs(),Path.Combine(Fixtures.Root,"ux010-"+Guid.NewGuid().ToString("N")),new FakeClipboard());
    internal static PrintPageItem Photo()
    {
        var file=Fixtures.CreateImage("ux010-source-"+Guid.NewGuid().ToString("N")+".png");
        return PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Image,file,file,0,1,new(1000,750),new(200,150)));
    }
    [Fact]
    public void NewPhotoTextUsesCommonPhysicalStyleAndPreservesOriginalLayoutInOneUndo()
    {
        using var vm=Session(); var page=Photo(); vm.AddPage(page); var before=page.Document.Snapshot(); var history=vm.UndoCount;
        vm.BeginAnnotationTextEditing(new(100,120)); Assert.True(vm.IsEditingText); Assert.True(vm.ShowTextFormatting); Assert.False(page.IsComposed);
        vm.EditingText="  Первая строка\nВторая строка  "; vm.TextFontFamily="Arial"; vm.TextFontSizePt=18; vm.TextBold=true; vm.TextItalic=true; vm.TextColor="#175E9C";
        vm.TextAlignment=TextAlignment.Right; vm.TextLineSpacing=1.5; vm.TextBlockWidthMm=70;
        Assert.Equal(18,vm.SelectedText!.Style.FontSizePt,6); Assert.Equal(70,vm.SelectedText.Placement.BoundsMm.Width,6);
        vm.FinishTextEditing(); Assert.False(vm.IsEditingText); Assert.False(page.IsComposed); Assert.Equal(history+1,vm.UndoCount);
        var caption=Assert.Single(page.Edits.Annotations); Assert.Equal("  Первая строка\nВторая строка  ",caption.Text);
        Assert.Equal(new TextAnnotationFormat("Arial",true,true,TextAlignment.Right,1.5),caption.TextFormat! with { LayoutMmPerUnit=null });
        Assert.True(caption.TextFormat.LayoutMmPerUnit>0);
        vm.UndoCommand.Execute(null); Assert.Equal(before,page.Document.Snapshot()); vm.RedoCommand.Execute(null);
        Assert.Equal(caption,Assert.Single(page.Edits.Annotations));
    }
    [Fact]
    public void CancelRestoresUnstyledCaptionCropAndRotationExactly()
    {
        using var vm=Session(); var page=Photo(); vm.AddPage(page);
        var caption=Annotation.Create(AnnotationKind.Text,new(220,170),new(660,270),"#111827",3,"Старый текст",36);
        page.Edits.Add(caption); page.Edits.SetCrop(new(80,40,800,660)); page.RotationQuarterTurns=3;
        var before=page.Document.Snapshot(); var count=vm.UndoCount;
        vm.BeginAnnotationTextEditing(caption.Start,caption); Assert.Equal(caption.Text,vm.EditingText);
        vm.EditingText="  Замена\n"; vm.TextBold=true; vm.TextFontSizePt=22; vm.FinishTextEditing(false);
        Assert.Equal(before,page.Document.Snapshot()); Assert.Equal(count,vm.UndoCount); Assert.Null(page.Edits.Annotations[0].TextFormat);
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void PresentationKeepsSourceCenterAndPhysicalFontAcrossQuarterTurns(int turn)
    {
        var caption=Annotation.Create(AnnotationKind.Text,new(220,170),new(620,270),"#000000",3,"Текст",36);
        var content=Photo().Document.PrimaryContent!;
        content=content with { Edits=content.Edits with { Crop=new(80,40,800,660),QuarterTurns=turn },Placement=new(new(20,30,132,160),17) };
        var p=AnnotationTextLayout.Present(content,caption,content.Placement.BoundsMm);
        var expected=p.SourceToPaper.Transform(new(420,220)); var bounds=p.Text.Placement.BoundsMm;
        Assert.Equal(expected.X,bounds.X+bounds.Width/2,6); Assert.Equal(expected.Y,bounds.Y+bounds.Height/2,6);
        Assert.Equal(36*p.MmPerUnit*72/25.4,p.Text.Style.FontSizePt,6);
        var revised=AnnotationTextLayout.Update(caption,"Текст",p.Text.Style,p.MmPerUnit,bounds.Width);
        Assert.Equal(caption.Start,revised.Start); Assert.Equal(36,revised.FontSize,6); Assert.Equal(caption.End.X,revised.End.X,6);
    }
    [Fact]
    public void NestedPhotoTextUsesSameEditorWithoutMovingOtherObjects()
    {
        using var vm=Session(); var page=Photo(); vm.AddPage(page);
        var caption=Annotation.Create(AnnotationKind.Text,new(100,100),new(400,190),"#000000",3,"Надпись",36); page.Edits.Add(caption);
        page.Document.PlacePrimaryImage(SheetPaper.A4,new(20,30,160,120)); var content=page.Document.PrimaryContent!;
        var shape=new ShapeObject(Guid.NewGuid(),new(new(20,200,40,30)),SheetShapeKind.Rectangle,new()); page.Document.AddObject(shape);
        vm.BeginAnnotationTextEditing(caption.Start,caption,content.Id); vm.EditingText="Новая надпись"; vm.TextBold=true; vm.FinishTextEditing();
        Assert.Equal(content.Placement,page.Document.PrimaryContent!.Placement); Assert.Equal(shape,page.Document.Objects.Single(o=>o.Id==shape.Id));
        Assert.Equal("Новая надпись",Assert.Single(page.Edits.Annotations).Text);
    }
    [Fact]
    public async Task FormatFiveRoundTripKeepsAnnotationAppearanceAndRawCoordinates()
    {
        using var vm=Session(); var page=Photo(); vm.AddPage(page); vm.BeginAnnotationTextEditing(new(120,160));
        vm.EditingText="Сохраняемый текст\n"; vm.TextBold=true; vm.TextItalic=true; vm.TextColor="#C62828"; vm.TextAlignment=TextAlignment.Center; vm.FinishTextEditing();
        var expected=Assert.Single(page.Edits.Annotations); var file=ProjectFileTests.PathFor("ux010-format5");
        await new ProjectFileService().SaveAsync(ProjectFileTests.Session(page),file,true,default);
        using var workspace=new SessionWorkspace(); var restored=await new ProjectFileService().LoadAsync(file,workspace,default);
        var result=restored.Session.Pages[0].Sheet.PrimaryContent!.Edits.Annotations.Single();
        Assert.Equal(expected with { Points=result.Points },result); Assert.Equal(expected.Points,result.Points); Assert.Equal(10,ProjectFileService.CurrentVersion);
    }
    [Fact]
    public void EmptyExistingCaptionIsRemovedAndUndoRestoresAllItsFormatting()
    {
        using var vm=Session(); var page=Photo(); vm.AddPage(page); vm.BeginAnnotationTextEditing(new(100,100)); vm.EditingText="Удаление"; vm.TextBold=true; vm.FinishTextEditing();
        var previous=Assert.Single(page.Edits.Annotations); vm.BeginAnnotationTextEditing(previous.Start,previous); vm.EditingText=""; vm.FinishTextEditing();
        Assert.Empty(page.Edits.Annotations); vm.UndoCommand.Execute(null); Assert.Equal(previous,Assert.Single(page.Edits.Annotations));
    }
    [Fact]
    public void SourceAndSheetUseIdenticalWordBreaksAtTheSamePhysicalWidth()
    {
        var source=string.Concat(Enumerable.Repeat("Проверка одинакового переноса слов и размера букв. ",8));
        var style=new TextStyle("Arial",14,Bold:true);
        foreach(var width in new[] { 40d,47.37,53.5,63,95 })
        {
            var caption=AnnotationTextLayout.Create(new(100,100),style,.2,width);
            caption=AnnotationTextLayout.Update(caption,source,style,.2);
            var physical=TextLayoutService.Layout(source,style,width); var logical=TextLayoutService.Layout(AnnotationTextLayout.LogicalText(caption));
            Assert.Equal(physical.Lines.Select(l=>(l.Start,l.Length)),logical.Lines.Select(l=>(l.Start,l.Length)));
        }
    }
    [Fact]
    public void TextToolSetsNewDefaultsWithoutRestylingPreviouslySelectedCaption()
    {
        using var vm=Session(); var page=Photo(); vm.AddPage(page); vm.BeginAnnotationTextEditing(new(100,100)); vm.EditingText="Первая"; vm.FinishTextEditing();
        var old=page.Edits.Annotations.Single(); vm.Tool=EditorTool.Text; vm.TextFontFamily="Arial"; vm.TextFontSizePt=20; vm.TextBold=true;
        Assert.Equal(old,page.Edits.Annotations.Single()); vm.BeginAnnotationTextEditing(new(400,250));
        Assert.Equal(20,vm.TextFontSizePt,6); Assert.True(vm.TextBold); Assert.Equal("Arial",vm.TextFontFamily); vm.FinishTextEditing(false);
    }
    [Fact]
    public void LargeValidPhysicalFontIsNotRejectedBySourceCoordinateConversion()
    {
        using var vm=Session(); var page=Photo(); vm.AddPage(page); vm.BeginAnnotationTextEditing(new(100,100));
        vm.EditingText="А"; vm.TextFontSizePt=900;
        Assert.Empty(vm.TextFormattingError); Assert.Equal(900,vm.TextFontSizePt,6); vm.FinishTextEditing(false);
    }
    [Fact]
    public void CaptionClippedByPhotoCropShowsWarningEvenInsidePaperMargins()
    {
        using var vm=Session(); var page=Photo(); vm.AddPage(page); page.Edits.SetCrop(new(0,0,400,100));
        var caption=Annotation.Create(AnnotationKind.Text,new(50,20),new(350,280),"#000000",3,"Строка\nСтрока\nСтрока",36); page.Edits.Add(caption);
        vm.SelectedSourceAnnotation=page.Edits.Annotations.Single();
        Assert.Contains("кадр",vm.TextWarning,StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task VersionFourProjectKeepsUnstyledCaptionWithoutMigrationOrRelayout()
    {
        var page=Photo(); page.Edits.Add(Annotation.Create(AnnotationKind.Text,new(170,110),new(570,220),"#000000",3,"Старый проект",36));
        page.Edits.SetCrop(new(50,40,800,650)); page.RotationQuarterTurns=2; var expected=page.Document.Snapshot();
        var file=ProjectFileTests.PathFor("ux010-version4"); await new ProjectFileService().SaveAsync(ProjectFileTests.Session(page),file,true,default);
        using(var zip=ZipFile.Open(file,ZipArchiveMode.Update))
        {
            var entry=zip.GetEntry("manifest.json")!; JsonNode node;
            using(var input=new StreamReader(entry.Open())) node=JsonNode.Parse(input.ReadToEnd())!;
            node["version"]=4; entry.Delete(); using var output=new StreamWriter(zip.CreateEntry("manifest.json").Open()); output.Write(node.ToJsonString());
        }
        using var workspace=new SessionWorkspace(); var loaded=await new ProjectFileService().LoadAsync(file,workspace,default);
        var restored=loaded.Session.Pages.Single().Sheet.PrimaryContent!; var original=expected.PrimaryContent!;
        Assert.Equal(original.Placement,restored.Placement); Assert.Equal(original.Edits.Crop,restored.Edits.Crop); Assert.Equal(2,restored.Edits.QuarterTurns);
        var before=original.Edits.Annotations.Single(); var after=restored.Edits.Annotations.Single();
        Assert.Equal(before with { Points=after.Points },after); Assert.Null(after.TextFormat);
    }
    [Theory] [InlineData(300)] [InlineData(600)]
    public async Task PhotoPrintAndExportKeepStyledCaptionAndUneditedPixels(int dpi)
    {
        using var vm=Session(); var page=Photo(); vm.AddPage(page); var originalBytes=File.ReadAllBytes(page.WorkingPath);
        vm.BeginAnnotationTextEditing(new(100,100)); vm.EditingText="Четкий текст\nВторая строка"; vm.TextFontFamily="Arial"; vm.TextFontSizePt=18; vm.TextBold=true; vm.TextColor="#175E9C"; vm.FinishTextEditing();
        var p=page.Snapshot(); var renderer=new PageRasterizer(new());
        using var bitmap=renderer.Render(p,1800,1350,true,default);
        var caption=p.Edits.Annotations.Single(); var bounds=AnnotationLayout.VisualBounds(caption); var scale=1.8;
        var ink=0;
        for(var y=(int)(bounds.Y*scale);y<(int)(bounds.Bottom*scale);y+=2)
        for(var x=(int)(bounds.X*scale);x<(int)(bounds.Right*scale);x+=2)
        { var color=bitmap.GetPixel(x,y); if(color.B>color.R+20 && color.R<100) ink++; }
        Assert.True(ink>30);
        using var unannotated=renderer.Render(p with { Sheet=null,Edits=p.Edits with { Annotations=[] } },1800,1350,true,default);
        Assert.Equal(unannotated.GetPixel(1700,1100).ToArgb(),bitmap.GetPixel(1700,1100).ToArgb());
        var export=Path.Combine(Fixtures.Root,"ux010-export-"+Guid.NewGuid().ToString("N")+".png");
        await new ImageExportService(renderer).SaveCopyAsync(p,dpi,default,export);
        using var output=System.Drawing.Image.FromFile(export); Assert.InRange(output.HorizontalResolution,dpi-.1,dpi+.1);
        Assert.Equal(originalBytes,File.ReadAllBytes(page.WorkingPath));
    }
}
