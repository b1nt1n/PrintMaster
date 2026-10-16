using PdfSharp.Pdf.IO;
using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using Xunit;

namespace PrintMaster.Tests;

public sealed class SheetExportTests
{
    internal static string Folder(string label)
    { var path=Path.Combine(Fixtures.Root,"ux012",label+"-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    internal static PrintPageItem Scene(SheetPaper? paper=null,string label="Экспорт: текст и фигуры")
    {
        var page=PrintPageItem.CreateBlank(paper ?? new(new(80,100),false,SheetMargins.SafeDefault));
        page.Document.AddObject(new TextObject(Guid.NewGuid(),new(new(10,12,55,30)),label,new(FontSizePt:14,Color:"#111827")));
        page.Document.AddObject(new ShapeObject(Guid.NewGuid(),new(new(12,48,30,18)),SheetShapeKind.Rectangle,new("#175E9C",.7,1,"#33AA77")));
        page.Document.AddObject(new InkObject(Guid.NewGuid(),new(new(15,75,35,8)),[new(0,0),new(.4,.8),new(1,0)],new("#B94712",.6)));
        return page;
    }
    internal static ExportPage ExportPage(PrintPageItem page)=>SheetExportRenderer.Prepare(page.Snapshot(),SheetPaper.A4);
    internal static ExportOptions Options(ExportFormat format=ExportFormat.Png,int dpi=300,ExportAppearance appearance=ExportAppearance.Color)
        =>new(format,appearance,dpi,ScaleMode.Fit,MonochromeMode.Auto,160);
    private static PageExportService Service()=>new(new(new(new())));
    private static byte[] Hash(string path)=>SHA256.HashData(File.ReadAllBytes(path));

    [Theory] [InlineData(300)] [InlineData(600)]
    public async Task PngPreservesPagePixelsDpiAndPhysicalSize(int dpi)
    {
        var page=Scene(); var before=page.Document.Snapshot(); var target=Path.Combine(Folder("png"),"лист.png");
        var plan=ExportFilePlan.Build([ExportPage(page)],Options(dpi:dpi),target); var result=await Service().ExportAsync(plan,false,null,default);
        using var image=new Bitmap(target);
        Assert.Equal((int)Math.Ceiling(80/25.4*dpi),image.Width); Assert.Equal((int)Math.Ceiling(100/25.4*dpi),image.Height);
        Assert.InRange(image.HorizontalResolution,dpi-.1,dpi+.1); Assert.Equal(Color.White.ToArgb(),image.GetPixel(0,0).ToArgb());
        var pixel=image.GetPixel((int)(25d/80*image.Width),(int)(55d/100*image.Height)); Assert.Equal(ColorTranslator.FromHtml("#33AA77").ToArgb(),pixel.ToArgb());
        Assert.Equal(1,result.Pages); Assert.Equal([target],result.Files); Assert.Equal(before,page.Document.Snapshot());
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(target)!,".printmaster-export-*"));
    }
    [Theory] [InlineData(300)] [InlineData(600)]
    public async Task PdfAndPngRenderTheSameImageWithoutJpegLoss(int dpi)
    {
        var folder=Folder("matching"); var page=Scene(); var item=ExportPage(page); var png=Path.Combine(folder,"sheet.png"); var pdf=Path.Combine(folder,"sheet.pdf");
        await Service().ExportAsync(ExportFilePlan.Build([item],Options(dpi:dpi),png),false,null,default);
        await Service().ExportAsync(ExportFilePlan.Build([item],Options(ExportFormat.Pdf,dpi),pdf),false,null,default);
        using var expected=new Bitmap(png); using var actual=new PdfiumRenderer().Render(pdf,0,expected.Width,expected.Height,0,false,default);
        using var doc=PdfReader.Open(pdf,PdfDocumentOpenMode.Import); Assert.Single(doc.Pages); Assert.InRange(doc.Pages[0].Width.Millimeter,79.99,80.01); Assert.InRange(doc.Pages[0].Height.Millimeter,99.99,100.01);
        var differences=0; var sampled=0;
        for(var y=0;y<expected.Height;y+=3) for(var x=0;x<expected.Width;x+=3)
        { sampled++; var a=actual.GetPixel(x,y); var b=expected.GetPixel(x,y); if(Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B)>25) differences++; }
        Assert.True(differences<sampled*.015,$"PDF differs at {differences}/{sampled} sampled pixels.");
        Assert.Contains("/FlateDecode",System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(pdf)));
        Assert.DoesNotContain("/DCTDecode",System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(pdf)));
    }
    [Fact]
    public async Task PdfKeepsMixedPaperSizesOrderAndOnePagePerQueueItem()
    {
        var a=Scene(SheetPaper.A4,"A"); var b=Scene(new(new(148,210),true,SheetMargins.SafeDefault),"B");
        var c=Scene(NotebookProfiles.Default(NotebookVariant.GridSpread).ToPaper(),"C"); a.Copies=7; b.Copies=2;
        var target=Path.Combine(Folder("mixed"),"a4-a5-notebook.pdf");
        await Service().ExportAsync(ExportFilePlan.Build([ExportPage(a),ExportPage(b),ExportPage(c)],Options(ExportFormat.Pdf),target),false,null,default);
        using var pdf=PdfReader.Open(target,PdfDocumentOpenMode.Import); Assert.Equal(3,pdf.PageCount);
        var expected=new[] { new SizeD(210,297),new(210,148),new(334,208) };
        for(var i=0;i<3;i++) { Assert.InRange(pdf.Pages[i].Width.Millimeter,expected[i].Width-.01,expected[i].Width+.01); Assert.InRange(pdf.Pages[i].Height.Millimeter,expected[i].Height-.01,expected[i].Height+.01); }
    }
    [Theory] [InlineData(ExportAppearance.Color)] [InlineData(ExportAppearance.Monochrome)]
    public void NotebookGuidesNeverBecomeExportedInk(ExportAppearance appearance)
    {
        var page=PrintPageItem.CreateBlank(NotebookProfiles.Default(NotebookVariant.GridSpread).ToPaper());
        using var bitmap=new SheetExportRenderer(new(new())).Render(ExportPage(page),Options(appearance:appearance),default);
        for(var y=0;y<bitmap.Height;y+=7) for(var x=0;x<bitmap.Width;x+=7) Assert.Equal(Color.White.ToArgb(),bitmap.GetPixel(x,y).ToArgb());
    }
    [Fact]
    public void MonochromeUsesBinaryPixelsAndPrintableClippingWhileColorKeepsPaperEdge()
    {
        var page=PrintPageItem.CreateBlank(new(new(60,60),false,SheetMargins.SafeDefault));
        page.Document.AddObject(new ShapeObject(Guid.NewGuid(),new(new(1,10,30,25)),SheetShapeKind.Rectangle,new("#888888",.5,1,"#888888")));
        var renderer=new SheetExportRenderer(new(new())); var item=ExportPage(page);
        using var color=renderer.Render(item,Options(),default); using var mono=renderer.Render(item,Options(appearance:ExportAppearance.Monochrome) with { Monochrome=MonochromeMode.Photo,AllowClipping=true },default);
        Assert.Equal(136,color.GetPixel(20,200).R); Assert.Equal(255,mono.GetPixel(20,200).R);
        var blacks=0; var whites=0;
        for(var y=150;y<350;y+=2) for(var x=90;x<340;x+=2)
        { var c=mono.GetPixel(x,y); Assert.Equal(c.R,c.G); Assert.Equal(c.G,c.B); Assert.True(c.R is 0 or 255); if(c.R==0) blacks++; else whites++; }
        Assert.True(blacks>100); Assert.True(whites>100);
    }
    [Fact]
    public async Task CancellationAfterFirstStageLeavesExistingOutputsByteIdentical()
    {
        var folder=Folder("cancel"); var pages=new[] {ExportPage(Scene()),ExportPage(Scene())}; var plan=ExportFilePlan.Build(pages,Options(),Path.Combine(folder,"pages.png"));
        foreach(var file in plan.Files) File.WriteAllText(file.Path,"existing "+file.Path);
        plan=ExportFilePlan.Build(pages,Options(),Path.Combine(folder,"pages.png")); var hashes=plan.Files.Select(f=>Hash(f.Path)).ToArray();
        using var cancel=new CancellationTokenSource(); var progress=new ImmediateProgress(p=>{ if(p.Completed==1) cancel.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Service().ExportAsync(plan,true,progress,cancel.Token));
        for(var i=0;i<plan.Files.Length;i++) Assert.Equal(hashes[i],Hash(plan.Files[i].Path));
        Assert.Empty(Directory.GetDirectories(folder,".printmaster-export-*"));
    }
    [Fact]
    public async Task UnconfirmedOverwriteAndChangedTargetDoNotReplaceAFile()
    {
        var target=Path.Combine(Folder("overwrite"),"file.png"); File.WriteAllText(target,"before"); var page=ExportPage(Scene()); var plan=ExportFilePlan.Build([page],Options(),target);
        var before=Hash(target); await Assert.ThrowsAsync<IOException>(()=>Service().ExportAsync(plan,false,null,default)); Assert.Equal(before,Hash(target));
        File.WriteAllText(target,"changed after confirmation"); var after=Hash(target);
        await Assert.ThrowsAsync<IOException>(()=>Service().ExportAsync(plan,true,null,default)); Assert.Equal(after,Hash(target));
        plan=ExportFilePlan.Build([page],Options(),target); await Service().ExportAsync(plan,true,null,default); using var image=new Bitmap(target); Assert.True(image.Width>900);
    }
    [Fact]
    public async Task FailureOnSecondCommitRollsBackTheFirstReplacement()
    {
        var folder=Folder("rollback"); var pages=new[] {ExportPage(Scene()),ExportPage(Scene())}; var plan=ExportFilePlan.Build(pages,Options(),Path.Combine(folder,"pages.png"));
        foreach(var file in plan.Files) File.WriteAllText(file.Path,"existing "+file.Path);
        File.SetAttributes(plan.Files[1].Path,FileAttributes.ReadOnly); plan=ExportFilePlan.Build(pages,Options(),Path.Combine(folder,"pages.png"));
        var before=plan.Files.Select(f=>Hash(f.Path)).ToArray();
        try { await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>Service().ExportAsync(plan,true,null,default)); for(var i=0;i<2;i++) Assert.Equal(before[i],Hash(plan.Files[i].Path)); }
        finally { File.SetAttributes(plan.Files[1].Path,FileAttributes.Normal); }
        Assert.Empty(Directory.GetDirectories(folder,".printmaster-export-*"));
    }
    [Fact]
    public void OutputCannotReplaceOriginalPdfOrWorkingImageAndNamesAreStable()
    {
        var image=DrawingStyleTests.ImagePage(); var item=ExportPage(image);
        Assert.Throws<IOException>(()=>ExportFilePlan.Build([item],Options(),image.OriginalPath));
        var pdfPath=Fixtures.CreatePdf("ux012-protected.pdf"); var imported=PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Pdf,pdfPath,pdfPath,0,2,new(595,842),new(210,297)));
        Assert.Throws<IOException>(()=>ExportFilePlan.Build([ExportPage(imported)],Options(ExportFormat.Pdf),pdfPath));
        var path=Path.Combine(Folder("names"),"Моя тетрадь.png"); var plan=ExportFilePlan.Build([item,item,item],Options(),path);
        Assert.Equal(new[] {"Моя тетрадь_001.png","Моя тетрадь_002.png","Моя тетрадь_003.png"},plan.Files.Select(f=>Path.GetFileName(f.Path)));
    }
    [Fact]
    public void OverflowAndOversizedRasterAreRejectedBeforeCreatingOutputs()
    {
        var page=Scene(); page.Document.AddObject(new TextObject(Guid.NewGuid(),new(new(10,10,20,1)),"Не помещаются строки\nвторая\nтретья",new(FontSizePt:18)));
        Assert.Throws<InvalidOperationException>(()=>SheetExportRenderer.ValidatePages([ExportPage(page)],Options()));
        SheetExportRenderer.ValidatePages([ExportPage(page)],Options() with { AllowOverflow=true,AllowClipping=true });
        var huge=PrintPageItem.CreateBlank(new(new(1000,1000),false,SheetMargins.SafeDefault));
        Assert.Contains("300 DPI",Assert.Throws<InvalidOperationException>(()=>SheetExportRenderer.ValidatePages([ExportPage(huge)],Options(dpi:600))).Message);
    }
    [Fact]
    public async Task ImportedPhotoCropRotationAndPdfExportDoNotChangeOriginalMaterials()
    {
        var folder=Folder("imports"); var imagePath=Fixtures.CreateImage("ux012-import-"+Guid.NewGuid().ToString("N")+".png");
        var photo=PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Image,imagePath,imagePath,0,1,new(800,600),new(160,120)));
        photo.Edits.SetCrop(new(40,20,650,460)); photo.RotationQuarterTurns=1;
        photo.Edits.Add(Annotation.Create(AnnotationKind.Arrow,new(100,100),new(400,300),"#175E9C",5));
        var pdfPath=Fixtures.CreatePdf("ux012-import-"+Guid.NewGuid().ToString("N")+".pdf");
        var document=PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Pdf,pdfPath,pdfPath,1,2,new(595,842),new(210,297)));
        var imagesBefore=Hash(imagePath); var pdfBefore=Hash(pdfPath); var options=Options(ExportFormat.Pdf,600);
        var pages=new[] {SheetExportRenderer.Prepare(photo.Snapshot(),new(new(100,150),false,SheetMargins.SafeDefault)),SheetExportRenderer.Prepare(document.Snapshot(),SheetPaper.A4)};
        var target=Path.Combine(folder,"photo-document.pdf"); await Service().ExportAsync(ExportFilePlan.Build(pages,options,target),false,null,default);
        Assert.Equal(imagesBefore,Hash(imagePath)); Assert.Equal(pdfBefore,Hash(pdfPath));
        var reader=new PdfiumRenderer(); Assert.Equal(2,reader.ReadPages(target).Count);
        using var preview=reader.Render(target,0,400,600,0,false,default);
        Assert.Equal(Color.White.ToArgb(),preview.GetPixel(0,0).ToArgb());
        Assert.Contains(Enumerable.Range(40,320),x=>preview.GetPixel(x,300).R<200);
    }
    [Fact]
    public async Task ExternalChangeDuringStagingIsRetainedInsteadOfOverwritten()
    {
        var folder=Folder("external"); var target=Path.Combine(folder,"sheet.png"); File.WriteAllText(target,"before");
        var plan=ExportFilePlan.Build([ExportPage(Scene())],Options(),target);
        var progress=new ImmediateProgress(p=>{ if(p.Completed==1) File.WriteAllText(target,"external content must stay"); });
        await Assert.ThrowsAsync<IOException>(()=>Service().ExportAsync(plan,true,progress,default)); Assert.Equal("external content must stay",File.ReadAllText(target));
        Assert.Empty(Directory.GetDirectories(folder,".printmaster-export-*"));
    }
    internal sealed class ExportDialogs : IUserDialogs
    {
        public bool Replace { get; set; }
        public int Confirmations { get; private set; }
        public string[] PickFiles()=>[]; public string? PickPdfOutput()=>null; public string? PickLibreOffice()=>null; public bool ConfirmCropping(int count)=>false; public void Error(string message) { }
        public bool ConfirmExportOverwrite(IReadOnlyList<string> files) { Confirmations++; return Replace; }
    }
    [Fact]
    public async Task ScopeUsesQueueOrderAndAcceptDoesNotRestartThePreview()
    {
        var a=Scene(label:"A"); var b=Scene(label:"B"); var c=Scene(label:"C"); var dialogs=new ExportDialogs();
        using var vm=new ExportViewModel([new(a.Snapshot(),false,true,true),new(b.Snapshot(),true,false,false),new(c.Snapshot(),false,true,true)],SheetPaper.A4,null,MonochromeMode.Auto,160,ScaleMode.Fit,dialogs,"test");
        vm.Scope=ExportScope.Highlighted; vm.OutputPath=Path.Combine(Folder("vm"),"selected.png"); await vm.RefreshPreviewAsync();
        Assert.Equal(new[] {a.Id,c.Id},vm.Pages.Select(p=>p.Page.Id)); Assert.True(vm.CanAccept); Assert.True(vm.TryAccept()); Assert.NotNull(vm.AcceptedPlan); Assert.False(vm.IsLoading);
        vm.Scope=ExportScope.Current; await vm.RefreshPreviewAsync(); Assert.Equal(b.Id,Assert.Single(vm.Pages).Page.Id);
        File.WriteAllText(vm.OutputPath,"existing"); vm.OutputPath=vm.OutputPath+" "; vm.OutputPath=vm.OutputPath.Trim(); await vm.RefreshPreviewAsync();
        Assert.False(vm.TryAccept()); Assert.Equal(1,dialogs.Confirmations); Assert.Null(vm.AcceptedPlan);
        dialogs.Replace=true; Assert.True(vm.TryAccept()); Assert.True(vm.OverwriteConfirmed);
    }
    private sealed class ImmediateProgress(Action<ExportProgress> action) : IProgress<ExportProgress>
    { public void Report(ExportProgress value)=>action(value); }
}
