using PrintMaster.Models;
using PrintMaster.Services;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace PrintMaster.Tests;

public sealed class PipelineIntegrationTests(ITestOutputHelper output)
{
    [Fact] [Trait("Category","Integration")]
    public async Task DocxConvertsWithoutOfficeAndImportsTwoPages()
    {
        Assert.NotNull(DocxConverter.FindExecutable());
        using var workspace=new SessionWorkspace(); var pdf=new PdfiumRenderer();
        var service=new FileImportService(workspace,new(),pdf); var original=Fixtures.CreateDocx();
        var pages=await service.ImportAsync(original,default);
        Assert.Equal(2,pages.Count); Assert.All(pages,p=>{ Assert.Equal(SourceKind.Docx,p.Kind); Assert.False(p.IsImage); Assert.True(File.Exists(p.WorkingPath)); });
        Assert.NotEqual(original,pages[0].WorkingPath);
        using var bitmap=new PageRasterizer(pdf).Render(pages[0].Snapshot(),595,842,false,default);
        bitmap.Save(Path.Combine(Fixtures.Root,"docx-preview.png"),ImageFormat.Png);
        File.Copy(pages[0].WorkingPath,Path.Combine(Fixtures.Root,"converted-docx.pdf"),true);
        output.WriteLine("LibreOffice: "+DocxConverter.FindExecutable());
    }

    [Theory] [Trait("Category","Integration")]
    [InlineData(".png")] [InlineData(".jpg")] [InlineData(".bmp")]
    public async Task ExportCreatesCopyAtRequestedDpiAndNeverOverwrites(string extension)
    {
        var path=Fixtures.CreateImage("export-source"+extension); var page=Fixtures.ImagePage(path);
        var service=new ImageExportService(new(new())); var old=File.ReadAllBytes(path);
        var copy1=await service.SaveCopyAsync(page,300,default); var copy2=await service.SaveCopyAsync(page,600,default);
        Assert.NotEqual(copy1,copy2); Assert.Contains("_edited (",copy1); Assert.Equal(old,File.ReadAllBytes(path));
        using var first=Image.FromFile(copy1); using var second=Image.FromFile(copy2);
        Assert.Equal(1600,first.Width); Assert.Equal(1200,first.Height); Assert.Equal(3200,second.Width); Assert.Equal(2400,second.Height);
        Assert.InRange(first.HorizontalResolution,299,301); Assert.InRange(second.HorizontalResolution,599,601);
    }

    [Fact] [Trait("Category","Printer")]
    public void HpDriverAcceptsPresetsAndReportsRealMarginsWithoutPrinting()
    {
        var name=PrinterInterop.ListPrinters().FirstOrDefault(x=>x.Contains("P1102",StringComparison.OrdinalIgnoreCase));
        if(name==null) { output.WriteLine("HP P1102 driver absent: capability check not run."); return; }
        var profiles=new List<PrinterProfile>(); var interop=new PrinterInterop();
        foreach(var paper in new[] { PaperPreset.A4,PaperPreset.A5,PaperPreset.Notebook })
        foreach(var landscape in new[] { false,true })
        {
            var settings=new PrintSettings { PrinterName=name,Paper=paper,Landscape=landscape }.Snapshot();
            var profile=interop.Probe(settings); Assert.True(profile.IsVerified); Assert.True(profile.PrintableMm.Width>20);
            var (driver,page)=PrinterInterop.CreateSettings(settings); var mode=PrinterInterop.CreateValidatedDevMode(settings,driver,page);
            output.WriteLine($"Managed page: {page.PaperSize}; native paper {profile.ActualPaperMm}");
            try
            {
                using var graphics=driver.CreateMeasurementGraphics(page);
                var actual=interop.ReadGraphicsProfile(graphics,name);
                Assert.InRange(Math.Abs(actual.PrintableMm.Width-profile.PrintableMm.Width),0,0.2);
                Assert.InRange(Math.Abs(actual.PrintableMm.Height-profile.PrintableMm.Height),0,0.2);
                Assert.InRange(Math.Abs(actual.PrintableMm.X-profile.PrintableMm.X),0,0.2);
                Assert.InRange(Math.Abs(actual.PrintableMm.Y-profile.PrintableMm.Y),0,0.2);
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(mode); }
            profiles.Add(profile); output.WriteLine($"{paper}, landscape={landscape}: {profile.Message}; size={profile.ActualPaperMm}");
        }
        File.WriteAllText(Path.Combine(Fixtures.Root,"hp-driver-profiles.json"),JsonSerializer.Serialize(profiles,new JsonSerializerOptions { WriteIndented=true }));
    }

    [Fact] [Trait("Category","Printer")]
    public async Task MicrosoftPdfPrintsExactQueueCopiesAndPaperSize()
    {
        var name=PrinterInterop.ListPrinters().FirstOrDefault(x=>x.Equals("Microsoft Print to PDF",StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(name); var path=Fixtures.CreatePdf(); var pdf=new PdfiumRenderer();
        var page=new PageSnapshot(Guid.NewGuid(),path,path,SourceKind.Pdf,0,595.276,841.89,210,297,0,2,new(new(0,0,595.276,841.89),Array.Empty<Annotation>()));
        var second=page with { Id=Guid.NewGuid(),PageIndex=1,Copies=1 };
        var outputPath=Path.Combine(Fixtures.Root,"printed-3-copies-a5.pdf");
        if(File.Exists(outputPath)) File.Delete(outputPath);
        var settings=new PrintSettings { PrinterName=name,Paper=PaperPreset.A5,Dpi=300 }.Snapshot(outputPath);
        var engine=new PrintEngine(new(pdf),new());
        var history=new List<PrintProgress>(); var progress=new ImmediateProgress(p=>history.Add(p));
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await engine.PrintAsync(new[] { page,second },settings,progress,timeout.Token);
        await AwaitFile(outputPath);
        var pages=pdf.ReadPages(outputPath); Assert.Equal(3,pages.Count); Assert.All(pages,p=>{ Assert.InRange(p.Width/72*25.4,147,149); Assert.InRange(p.Height/72*25.4,209,211); });
        Assert.Equal(3,history.Last().Completed);
        using var preview=pdf.Render(outputPath,0,420,595,0,false,default);
        preview.Save(Path.Combine(Fixtures.Root,"printed-preview.png"),ImageFormat.Png);
        var black=0; for(var y=0;y<preview.Height;y++) for(var x=0;x<preview.Width;x++) if(preview.GetPixel(x,y).R<50) black++;
        Assert.True(black>100,"Spooler output should contain the original PDF content.");
    }

    [Fact] [Trait("Category","Printer")]
    public async Task PhotoFillPrintsLandscapeA4At600DpiAndRequiresConsent()
    {
        var name=PrinterInterop.ListPrinters().First(x=>x.Equals("Microsoft Print to PDF",StringComparison.OrdinalIgnoreCase));
        var page=Fixtures.ImagePage(Fixtures.CreateImage("photo-600dpi-source.png")) with { RotationQuarterTurns=1 };
        var outputPath=Path.Combine(Fixtures.Root,"photo-600dpi-landscape-a4.pdf"); if(File.Exists(outputPath)) File.Delete(outputPath);
        var config=new PrintSettings { PrinterName=name,Paper=PaperPreset.A4,Landscape=true,Scaling=ScaleMode.Fill,Monochrome=MonochromeMode.Photo,Dpi=600 };
        var engine=new PrintEngine(new(new()),new());
        await Assert.ThrowsAsync<InvalidOperationException>(()=>engine.PrintAsync(new[] { page },config.Snapshot(outputPath),null,default));
        Assert.False(File.Exists(outputPath));
        using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await engine.PrintAsync(new[] { page },config.Snapshot(outputPath,true),null,cancellation.Token); await AwaitFile(outputPath);
        var pdf=new PdfiumRenderer(); var pages=pdf.ReadPages(outputPath); Assert.Single(pages);
        Assert.InRange(pages[0].Width/72*25.4,296,298); Assert.InRange(pages[0].Height/72*25.4,209,211);
        using var bitmap=pdf.Render(outputPath,0,842,595,0,false,default);
        bitmap.Save(Path.Combine(Fixtures.Root,"photo-600dpi-preview.png"),ImageFormat.Png);
        // Safe white margins survive Fill, and center retains photo tones after downsampling.
        Assert.True(bitmap.GetPixel(5,300).R>240); Assert.InRange(bitmap.GetPixel(420,300).R,20,235);
    }

    private static async Task AwaitFile(string path)
    {
        for(var i=0;i<100;i++)
        {
            try { if(File.Exists(path) && new FileInfo(path).Length>100) { using var stream=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.None); return; } }
            catch(IOException) { }
            await Task.Delay(100);
        }
        throw new TimeoutException("Microsoft Print to PDF did not close the output file.");
    }

    [Fact] [Trait("Category","Printer")]
    public async Task BlankSheetObjectsPrintAtTheirPhysicalCoordinates()
    {
        var printer=PrinterInterop.ListPrinters().First(x=>x.Equals("Microsoft Print to PDF",StringComparison.OrdinalIgnoreCase));
        var page=PrintPageItem.CreateBlank(SheetPaper.A4);
        page.Document.AddObject(new ShapeObject(Guid.NewGuid(),new(new(20,30,50,20)),SheetShapeKind.Rectangle,new("#000000",0.2,1,"#000000")));
        page.Document.AddObject(new TextObject(Guid.NewGuid(),new(new(20,65,100,20)),"PrintMaster sheet model",new(FontSizePt:18)));
        var path=Path.Combine(Fixtures.Root,"pm001-printed-sheet.pdf"); if(File.Exists(path)) File.Delete(path);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await new PrintEngine(new(new()),new()).PrintAsync(new[] { page.Snapshot() },new PrintSettings { PrinterName=printer,Dpi=300 }.Snapshot(path),null,timeout.Token);
        await AwaitFile(path); var pdf=new PdfiumRenderer(); Assert.Single(pdf.ReadPages(path));
        using var rendered=pdf.Render(path,0,992,1403,0,false,default);
        Color At(double xMm,double yMm)=>rendered.GetPixel((int)(xMm/210*rendered.Width),(int)(yMm/297*rendered.Height));
        Assert.True(At(25,35).R<20); Assert.True(At(75,35).R>240); Assert.True(At(25,55).R>240);
        rendered.Save(Path.Combine(Fixtures.Root,"pm001-printed-sheet-preview.png"),ImageFormat.Png);
        // A fixed sheet owns its paper even when the inherited job defaults differ.
        var inherited=new PrintSettings{PrinterName=printer,Paper=PaperPreset.A5,Dpi=300}.Snapshot(path);
        Assert.Equal(new SizeD(210,297),ManualDuplexPlanner.ForPage(page.Snapshot(),inherited).PaperSizeMm);
        await new PrintEngine(new(new()),new()).PrintAsync([page.Snapshot()],inherited,null,timeout.Token);
        await AwaitFile(path);var actual=Assert.Single(pdf.ReadPages(path));
        Assert.InRange(actual.Width*25.4/72,209,211);Assert.InRange(actual.Height*25.4/72,296,298);
    }
    private sealed class ImmediateProgress(Action<PrintProgress> handler) : IProgress<PrintProgress>
    { public void Report(PrintProgress value)=>handler(value); }
}
