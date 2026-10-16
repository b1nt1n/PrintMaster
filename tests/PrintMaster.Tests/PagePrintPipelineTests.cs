using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Xunit;

namespace PrintMaster.Tests;

public sealed class PagePrintPipelineTests
{
    private static PrintSettingsSnapshot Settings(string printer="Test printer")=>new PrintSettings { PrinterName=printer }.Snapshot();
    private static PagePrintOptions Options(PrintSettingsSnapshot settings)=>PagePrintOptions.Capture(settings);
    private static PageSnapshot Source(int copies=1)=>new(Guid.NewGuid(),"sample.pdf","sample.pdf",SourceKind.Pdf,0,600,800,210,297,0,copies,new(new(0,0,600,800),[]));

    [Fact]
    public void ImportedPageOverridesPaperAndQualityWhileKeepingJobConsentPrinterAndBackRotation()
    {
        var global=Settings() with { AllowCropping=true,AllowNotebookOverflow=true,OutputPath="one.pdf",ManualDuplex=true,
            PrintOffsetXmm=1.25,PrintOffsetYmm=-2,PageRotationDegrees=180 };
        var page=Source() with { PrintOptions=Options(global with { Paper=PaperPreset.A5,PaperSizeMm=new(148,210),Landscape=true,
            Scaling=ScaleMode.Fill,Monochrome=MonochromeMode.Photo,Dpi=300,Threshold=219 }) };
        var effective=ManualDuplexPlanner.ForPage(page,global);
        Assert.Equal(PaperPreset.A5,effective.Paper); Assert.Equal(new SizeD(148,210),effective.PaperSizeMm);
        Assert.True(effective.Landscape); Assert.Equal(ScaleMode.Fill,effective.Scaling); Assert.Equal(MonochromeMode.Photo,effective.Monochrome);
        Assert.Equal(300,effective.Dpi); Assert.Equal(219,effective.Threshold);
        Assert.Equal(global.PrinterName,effective.PrinterName); Assert.Equal(global.OutputPath,effective.OutputPath);
        Assert.True(effective.AllowCropping); Assert.True(effective.AllowNotebookOverflow); Assert.True(effective.ManualDuplex);
        Assert.Equal(1.25,effective.PrintOffsetXmm); Assert.Equal(-2,effective.PrintOffsetYmm); Assert.Equal(180,effective.PageRotationDegrees);
        Assert.Equal(effective,ManualDuplexPlanner.ForPage(page,effective));
    }

    [Fact]
    public void FixedSheetOwnsGeometryButItsQualityOverrideStillApplies()
    {
        var global=Settings(); var sheet=PrintPageItem.CreateBlank(new(new(70,90),true,SheetMargins.SafeDefault));
        var page=sheet.Snapshot() with { PrintOptions=Options(global with { Paper=PaperPreset.A5,PaperSizeMm=new(148,210),
            Landscape=false,Dpi=300,Monochrome=MonochromeMode.Photo,Threshold=77,Scaling=ScaleMode.Stretch }) };
        var effective=ManualDuplexPlanner.ForPage(page,global);
        Assert.Equal(PaperPreset.Custom,effective.Paper); Assert.Equal(new SizeD(70,90),effective.PaperSizeMm); Assert.True(effective.Landscape);
        Assert.Equal(300,effective.Dpi); Assert.Equal(MonochromeMode.Photo,effective.Monochrome); Assert.Equal(77,effective.Threshold);
        Assert.Equal(sheet.Document.Paper.OrientedSizeMm,effective.OrientedPaperMm);
    }

    [Fact]
    public void AnnotatedAutomaticImageStillUsesIndividualPaperInsteadOfOldA4SheetMetadata()
    {
        var path=Fixtures.CreateImage("pm020-automatic-"+Guid.NewGuid().ToString("N")+".png");
        var page=PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Image,path,path,0,1,new(800,600),new(160,120)));
        page.Edits.Add(Annotation.Create(AnnotationKind.Rectangle,new(50,50),new(100,100),"#000000",4));
        var snapshot=page.Snapshot() with { PrintOptions=Options(Settings() with { Paper=PaperPreset.A5,PaperSizeMm=new(148,210),Landscape=true }) };
        Assert.True(snapshot.UsesAutomaticContentLayout);
        var effective=ManualDuplexPlanner.ForPage(snapshot,Settings());
        Assert.Equal(new SizeD(148,210),effective.PaperSizeMm); Assert.True(effective.Landscape);
    }

    [Fact]
    public void ConsecutivePacketsPreserveOrderAndCopiesAcrossDifferentQualityAndPaper()
    {
        var global=Settings(); var a=Source(2); var b=Source(3) with { PrintOptions=Options(global with { Dpi=300,Monochrome=MonochromeMode.Photo }) };
        var c=Source() with { PrintOptions=Options(global with { Paper=PaperPreset.A5,PaperSizeMm=new(148,210) }) };
        var d=Source();
        var packets=ManualDuplexPlanner.Simplex([a,b,c,d],global);
        Assert.Equal(4,packets.Count); Assert.Equal(new[] { a.Id,b.Id,c.Id,d.Id },packets.SelectMany(p=>p.Pages).Select(p=>p.Id));
        Assert.Equal(new[] { 2,3,1,1 },packets.Select(p=>p.Feeds)); Assert.Equal(300,packets[1].Settings.Dpi);
        Assert.Equal(MonochromeMode.Photo,packets[1].Settings.Monochrome); Assert.Equal(PaperPreset.A5,packets[2].Settings.Paper);
    }

    [Fact]
    public void ManualDuplexPairsSameStockDespiteDifferentQualityAndPreservesBackRotation()
    {
        var global=Settings(); var a=Source(2); var b=Source() with { PrintOptions=Options(global with { Dpi=300,Monochrome=MonochromeMode.Photo }) };
        var c=Source() with { PrintOptions=Options(global with { Paper=PaperPreset.A5,PaperSizeMm=new(148,210) }) };
        var plan=ManualDuplexPlanner.Build([a,b,c],global,backRotation:180);
        Assert.Equal(3,plan.Sheets.Count); Assert.Equal(b.Id,plan.Sheets[0].Back.Page.Id); Assert.True(plan.Sheets[1].Back.IsBlank);
        Assert.Equal(c.Id,plan.Sheets[2].Front.Page.Id); Assert.True(plan.Sheets[2].Back.IsBlank);
        var back=plan.BackPackets.SelectMany(p=>p.Pages.Select(page=>(page,p.Settings))).Single(p=>p.page.Id==b.Id);
        Assert.Equal(180,back.Settings.PageRotationDegrees); Assert.Equal(180,ManualDuplexPlanner.ForPage(back.page,back.Settings).PageRotationDegrees);
        Assert.Equal(300,back.Settings.Dpi); Assert.Equal(MonochromeMode.Photo,back.Settings.Monochrome);
    }

    [Fact]
    public async Task PdfQualityOnlyOverridesUseOneOutputJobAndKeepAllCopies()
    {
        var global=Settings() with { OutputPath="one.pdf" }; var a=Source(2); var b=Source(3) with { PrintOptions=Options(global with { Dpi=300,Monochrome=MonochromeMode.Photo }) };
        var runner=new Recorder(); var workflow=new PaperPrintWorkflow(runner,new Dialogs(),_=>null,_=>true,_=>Task.CompletedTask);
        Assert.True(await workflow.StartAsync([a,b],global,null,default));
        var call=Assert.Single(runner.Calls); Assert.Equal("one.pdf",call.Settings.OutputPath); Assert.Equal(5,call.Feeds);
        Assert.Equal(new[] { a.Id,b.Id },call.Pages.Select(p=>p.Id)); Assert.Equal(300,call.Pages[1].PrintOptions!.Dpi);
    }

    [Fact]
    public async Task InvalidLaterOverrideIsRejectedBeforeFirstPacketOrNativePrinterAccess()
    {
        var global=Settings("HP LaserJet Professional P1102"); var invalid=Source() with
        { PrintOptions=Options(Settings() with { Paper=PaperPreset.Custom,PaperSizeMm=new(400,500) }) };
        var runner=new Recorder(); var workflow=new PaperPrintWorkflow(runner,new Dialogs(),_=>null,_=>true,_=>Task.CompletedTask);
        var error=await Assert.ThrowsAsync<ArgumentException>(()=>workflow.StartAsync([Source(),invalid],global,null,default));
        Assert.Contains("Страница 2",error.Message); Assert.Empty(runner.Calls); Assert.False(workflow.HasPending);
        var engine=new PrintEngine(new(new()),new());
        error=await Assert.ThrowsAsync<ArgumentException>(()=>engine.PrintAsync([Source(),invalid],global,null,default));
        Assert.Contains("Страница 2",error.Message); Assert.Contains("P1102",error.Message);
    }

    [Fact]
    public void ReviewUsesIndividualFillAndThresholdWarningsEvenWhenPaperIsUniform()
    {
        var path=Fixtures.CreateImage("pm020-review-"+Guid.NewGuid().ToString("N")+".png");
        var a=Fixtures.ImagePage(path) with { PrintOptions=Options(Settings() with { Scaling=ScaleMode.Fill,Monochrome=MonochromeMode.Text }) };
        var b=Fixtures.ImagePage(path) with { PrintOptions=Options(Settings() with { Scaling=ScaleMode.Fit,Monochrome=MonochromeMode.Photo }) };
        using var review=new PrintReviewViewModel([a,b],new() { PrinterName="Test printer",Monochrome=MonochromeMode.Photo },[]);
        Assert.False(review.HasMixedPaper); Assert.True(review.HasIndividualOptions); Assert.True(review.AllowCropping);
        Assert.Contains("1 странице",review.Warning); Assert.Contains("полутона",review.MonoHint);
        Assert.False(review.TryAccept(false)); Assert.True(review.TryAccept(true));
    }

    [Fact]
    public void ReviewDetectsMixedPaperOnImportedPagesAndRejectsPdf()
    {
        var a=Source(); var b=Source() with { PrintOptions=Options(Settings() with { Paper=PaperPreset.A5,PaperSizeMm=new(148,210) }) };
        using var review=new PrintReviewViewModel([a,b],new() { PrinterName="Microsoft Print to PDF" },[]);
        Assert.True(review.HasMixedPaper); Assert.Contains("Размер бумаги",review.Error); Assert.False(review.TryAccept(true));
    }

    [Fact]
    public void ReviewCountsSourceFillAndOverlayClippingOnceAndRequestsConsentForEither()
    {
        var path=Fixtures.CreateImage("pm020-overlay-"+Guid.NewGuid().ToString("N")+".png");
        var page=PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Image,path,path,0,1,new(800,600),new(160,120)));
        page.Document.AddObject(new ShapeObject(Guid.NewGuid(),new(new(180,20,20,20)),SheetShapeKind.Rectangle,new("#000000",.5)));
        var global=Settings("Microsoft Print to PDF");
        var options=Options(global with {Paper=PaperPreset.A5,PaperSizeMm=new(148,210),Scaling=ScaleMode.Fit});
        Assert.True(page.Snapshot().UsesAutomaticContentLayout);
        using(var review=new PrintReviewViewModel([page.Snapshot() with {PrintOptions=options}],new() {PrinterName=global.PrinterName},[]))
        { Assert.True(review.AllowCropping); Assert.Contains("1 странице",review.Warning); Assert.False(review.TryAccept(false)); }
        using(var review=new PrintReviewViewModel([page.Snapshot() with {PrintOptions=options with {Scaling=ScaleMode.Fill}}],new() {PrinterName=global.PrinterName},[]))
        { Assert.True(review.AllowCropping); Assert.Contains("1 странице",review.Warning); Assert.True(review.TryAccept(true)); }
    }

    [Fact] [Trait("Category","Printer")]
    public async Task NativePdfOneJobHonorsDifferentThresholdsDpiAndCopiesInOrder()
    {
        var path=Path.Combine(Fixtures.Root,"pm020-grey-"+Guid.NewGuid().ToString("N")+".png");
        using(var image=new Bitmap(400,400)) { using var graphics=Graphics.FromImage(image); graphics.Clear(Color.FromArgb(128,128,128)); image.Save(path,ImageFormat.Png); }
        var global=Settings("Microsoft Print to PDF") with { Paper=PaperPreset.A5,PaperSizeMm=new(148,210),Monochrome=MonochromeMode.Text,
            OutputPath=Path.Combine(Fixtures.Root,"pm020-native-"+Guid.NewGuid().ToString("N")+".pdf") };
        var source=Fixtures.ImagePage(path) with { SourceWidth=400,SourceHeight=400,Edits=new(new(0,0,400,400),[]) };
        var white=source with { Copies=2,PrintOptions=Options(global with { Dpi=300,Threshold=50 }) };
        var black=source with { Id=Guid.NewGuid(),PrintOptions=Options(global with { Dpi=600,Threshold=220 }) };
        var photo=source with { Id=Guid.NewGuid(),PrintOptions=Options(global with { Dpi=300,Monochrome=MonochromeMode.Photo }) };
        await new PrintEngine(new(new()),new()).PrintAsync([white,black,photo],global,null,default);
        for(var attempt=0;attempt<100 && (!File.Exists(global.OutputPath) || new FileInfo(global.OutputPath).Length==0);attempt++) await Task.Delay(50);
        var pdf=new PdfiumRenderer(); var pages=pdf.ReadPages(global.OutputPath!); Assert.Equal(4,pages.Count);
        Assert.All(pages,p=> { Assert.InRange(p.Width/72*25.4,147,149); Assert.InRange(p.Height/72*25.4,209,211); });
        for(var index=0;index<3;index++)
        {
            using var bitmap=pdf.Render(global.OutputPath!,index,592,840,0,false,default);
            Assert.InRange(bitmap.GetPixel(296,420).R,index<2 ? 240 : 0,index<2 ? 255 : 15);
        }
        using(var bitmap=pdf.Render(global.OutputPath!,3,592,840,0,false,default))
        {
            double total=0; for(var y=380;y<460;y++) for(var x=256;x<336;x++) total+=bitmap.GetPixel(x,y).R/255.0;
            Assert.InRange(total/(80*80),.2,.8);
        }
    }

    private sealed class Recorder:IPrintJobRunner
    {
        public List<PrintPacket> Calls {get;}=[];
        public Task PrintAsync(IReadOnlyList<PageSnapshot> pages,PrintSettingsSnapshot settings,IProgress<PrintProgress>? progress,CancellationToken token)
        { Calls.Add(new(pages.ToArray(),settings)); return Task.CompletedTask; }
    }
    private sealed class Dialogs:IUserDialogs
    {
        public string[] PickFiles()=>[]; public string? PickPdfOutput()=>null; public string? PickLibreOffice()=>null;
        public bool ConfirmCropping(int count)=>false; public void Error(string message) { }
        public bool ShowPaperGuide(PaperGuideViewModel model)=>true;
    }
}
