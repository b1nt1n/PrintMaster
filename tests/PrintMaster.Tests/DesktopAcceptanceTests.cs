using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.Collections.Immutable;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace PrintMaster.Tests;

public sealed class DesktopAcceptanceTests
{
    [DesktopAcceptanceFact] [Trait("Category","DesktopAcceptance")]
    public Task RealWindowsClipboardRoundTripsImagesFilesObjectsAndPages()=>OnSta(async ()=>
    {
        var original=CaptureClipboard(); var originalFormats=original.GetFormats(false); var directory=NewDirectory();
        var dialogs=new ClipboardTests.TestDialogs(); var clipboard=new ClipboardService();
        using var vm=new PrintSessionViewModel(dialogs,Path.Combine(directory,"recovery"),clipboard);
        try
        {
            var first=PrintPageItem.CreateBlank(SheetPaper.A4); var second=PrintPageItem.CreateBlank(SheetPaper.A4);
            vm.AddPage(first); vm.AddPage(second); vm.SelectedPage=first;
            var pixels=new byte[] {0,0,255,255, 0,255,0,0, 255,0,0,255, 128,128,128,255};
            var image=BitmapSource.Create(2,2,96,96,System.Windows.Media.PixelFormats.Bgra32,null,pixels,8); image.Freeze();
            await clipboard.WriteAsync(new([],Image:image),default);
            var fromSystem=await clipboard.ReadAsync(default);
            Assert.True(fromSystem.Image!=null,"Clipboard image missing; formats: "+string.Join(", ",Clipboard.GetDataObject()?.GetFormats(false) ?? []));
            Assert.True(fromSystem.Image!.IsFrozen);
            using(var probeWorkspace=new SessionWorkspace())
                await new FileImportService(probeWorkspace,new(),new()).ImportClipboardImageAsync(fromSystem.Image!,default);
            await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto);
            Assert.Equal(2,vm.Pages.Count);
            Assert.True(first.Document.Objects.Count==1,"Image insertion: "+vm.Status+"; "+string.Join("; ",dialogs.Errors));
            Assert.Single(first.Document.Snapshot().Assets);
            using(var bitmap=System.Drawing.Image.FromFile(first.Document.Snapshot().Assets[0].WorkingPath!))
            { Assert.Equal(2,bitmap.Width); Assert.Equal(0,((Bitmap)bitmap).GetPixel(1,0).A); }
            Clipboard.SetImage(image); await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Queue); Assert.Equal(3,vm.Pages.Count);
            var files=new System.Collections.Specialized.StringCollection {Fixtures.CreatePdf("qa13-clipboard.pdf"),Fixtures.CreateImage("qa13-file.png")};
            Clipboard.SetFileDropList(files); await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Queue);
            Assert.Equal(6,vm.Pages.Count);
            vm.SelectedPage=first;
            var text=SheetEditing.CreateText("Настоящий буфер",new(Color:"#2A73C5",Bold:true),new(20,40),first.Document.Paper.SafeAreaMm);
            var shape=new ShapeObject(Guid.NewGuid(),new(new(25,80,30,20)),SheetShapeKind.Rectangle,new("#31A26F",0.5));
            first.Document.AddObjects([text,shape]); vm.SelectAllSheetObjects(); var source=first.Document.Snapshot(); var amount=first.Document.Objects.Count;
            Assert.True(await vm.CopyContextAsync(false,true)); Assert.Empty(first.Document.Objects);
            vm.SelectedPage=second; await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto);
            Assert.Equal(amount,second.Document.Objects.Count); Assert.Equal("#2A73C5",second.Document.Objects.OfType<TextObject>().Single().Style.Color);
            vm.UndoCommand.Execute(null); Assert.Empty(second.Document.Objects); vm.UndoCommand.Execute(null);
            Assert.Equal(source.Objects.ToArray(),first.Document.Objects.ToArray());
            vm.SelectedPage=first; vm.SelectAllSheetObjects(); await vm.CopySelectionCommand.ExecuteAsync(null);
            using(var destination=new PrintSessionViewModel(dialogs,Path.Combine(directory,"destination-recovery"),clipboard))
            {
                var page=PrintPageItem.CreateBlank(SheetPaper.A4); destination.AddPage(page);
                await destination.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto);
                Assert.Equal(amount,page.Document.Objects.Count); Assert.Equal("#2A73C5",page.Document.Objects.OfType<TextObject>().Single().Style.Color);
            }
            vm.SetQueueSelection([first,second],first); await vm.CopyContextAsync(true,false);
            var pages=vm.Pages.Count; await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Queue);
            Assert.Equal(pages+2,vm.Pages.Count);
            Clipboard.SetText("Текст в системном буфере — № ё"); await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto);
            Assert.Contains(vm.SelectedPage!.Document.Objects.OfType<TextObject>(),t=>t.Text.Contains("№ ё")); Assert.Empty(dialogs.Errors);
            await new ProjectFileService().SaveAsync(vm.CaptureProject(),Path.Combine(directory,"clipboard-result.printmaster"),true,default);
        }
        finally
        {
            Clipboard.SetDataObject(original,true);
            var restored=Clipboard.GetDataObject();
            Assert.All(originalFormats,f=>Assert.True(restored?.GetDataPresent(f,false)==true,"Original Clipboard format restored: "+f));
        }
    });

    [DesktopAcceptanceFact] [Trait("Category","DesktopAcceptance")]
    public async Task PortableReferencesRecoveryExportAndDeviceFormats()
    {
        var directory=NewDirectory();
        var original=Path.Combine(directory,"original.png"); File.Copy(Fixtures.CreateImage("qa13-export-input.png"),original);
        using var vm=new PrintSessionViewModel(new ClipboardTests.TestDialogs(),Path.Combine(directory,"recovery"),new FakeClipboard());
        await vm.ImportFilesAsync([original,Fixtures.CreatePdf("qa13-portable.pdf"),Fixtures.CreateDocx()]);
        Assert.Equal(5,vm.Pages.Count); var session=vm.CaptureProject();
        var portable=Path.Combine(directory,"portable.printmaster"); var reference=Path.Combine(directory,"reference.printmaster");
        var files=new ProjectFileService(); await files.SaveAsync(session,portable,true,default); await files.SaveAsync(session,reference,false,default);
        var renamed=Path.Combine(directory,"original-renamed.png"); File.Move(original,renamed);
        using(var workspace=new SessionWorkspace())
        {
            var loaded=await files.LoadAsync(portable,workspace,default); Assert.Empty(loaded.Missing); Assert.Equal(5,loaded.Session.Pages.Length);
        }
        using(var workspace=new SessionWorkspace())
        {
            var loaded=await files.LoadAsync(reference,workspace,default); Assert.Single(loaded.Missing);
            var asset=loaded.Session.Pages.SelectMany(p=>p.Sheet.Assets).First(a=>a.Id==loaded.Missing[0].AssetId);
            var relinked=await files.RelinkAsync(asset,renamed,workspace,default);
            Assert.True(File.Exists(relinked.WorkingPath));
        }
        await vm.FlushRecoveryAsync();
        using(var workspace=new SessionWorkspace())
        { var recovered=await files.LoadAsync(vm.RecoveryFilePath,workspace,default); Assert.Equal(5,recovered.Session.Pages.Length); Assert.Empty(recovered.Missing); }
        var photo=vm.Pages.First(p=>p.IsImage);
        photo.Edits.Add(Annotation.Create(AnnotationKind.Rectangle,new(50,50),new(150,150),"#2A73C5",3));
        var exporter=new ImageExportService(new(new()));
        var low=await exporter.SaveCopyAsync(photo.Snapshot(),300,default,Path.Combine(directory,"copy-300.png"));
        var high=await exporter.SaveCopyAsync(photo.Snapshot(),600,default,Path.Combine(directory,"copy-600.png"));
        using(var a=System.Drawing.Image.FromFile(low)) using(var b=System.Drawing.Image.FromFile(high))
        { Assert.InRange(b.Width/(double)a.Width,1.999,2.001); Assert.InRange(b.HorizontalResolution,599,601); }
        var hp=PrinterInterop.ListPrinters().First(p=>p.Contains("P1102",StringComparison.OrdinalIgnoreCase));
        foreach(var paper in new[] {PaperPreset.A4,PaperPreset.A5,PaperPreset.Notebook})
        foreach(var landscape in new[] {false,true})
        {
            var settings=new PrintSettings {PrinterName=hp,Paper=paper,Landscape=landscape,Dpi=600};
            var profile=await Task.Run(()=>new PrinterInterop().Probe(settings.Snapshot()));
            Assert.InRange(Math.Abs(profile.ActualPaperMm.Width-settings.OrientedPaperMm.Width),0,1.5);
            Assert.InRange(Math.Abs(profile.ActualPaperMm.Height-settings.OrientedPaperMm.Height),0,1.5);
        }
        await File.WriteAllTextAsync(Path.Combine(directory,"verification.txt"),"Portable/reference/relink/recovery/export and six HP format profiles passed.");
    }
    [PhysicalA4Fact] [Trait("Category","PhysicalPrinter")]
    public async Task PhysicalHpA4TextHalftonesAndTwoPlusOneCopies()
    {
        var directory=NewDirectory(); var hp=PrinterInterop.ListPrinters().First(p=>p.Contains("P1102",StringComparison.OrdinalIgnoreCase));
        var rasterizer=new PageRasterizer(new()); var engine=new PrintEngine(rasterizer,new());
        var text=PrintPageItem.CreateBlank(SheetPaper.A4); var safe=text.Document.Paper.SafeAreaMm;
        text.Document.AddObject(SheetEditing.CreateText("PrintMaster 1.0.13 — A4 / ТЕКСТ\n10, 14, 24 pt; русский: ё, й, № 123\nКвадрат ниже должен быть 100 × 100 мм.",new(FontSizePt:14),new(20,20),safe));
        text.Document.AddObject(new ShapeObject(Guid.NewGuid(),new(new(20,65,100,100)),SheetShapeKind.Rectangle,new("#000000",0.4)));
        var tonePath=Path.Combine(directory,"halftones.png");
        using(var bitmap=new Bitmap(1800,1200))
        {
            bitmap.SetResolution(300,300);
            using var graphics=Graphics.FromImage(bitmap); graphics.Clear(System.Drawing.Color.White);
            for(var i=0;i<256;i++) { using var brush=new SolidBrush(System.Drawing.Color.FromArgb(i,i,i)); graphics.FillRectangle(brush,i*1800f/256,100,1800f/256+1,400); }
            using var font=new Font("Arial",42); graphics.DrawString("PrintMaster — ПОЛУТОНА / 600 DPI",font,Brushes.Black,50,20);
            using var pen=new Pen(System.Drawing.Color.Black,3); for(var i=0;i<24;i++) graphics.DrawEllipse(pen,60+i*8,620+i*4,700-i*16,420-i*8);
            bitmap.Save(tonePath,ImageFormat.Png);
        }
        using var workspace=new SessionWorkspace(); var photo=(await new FileImportService(workspace,new(),new()).ImportAsync(tonePath,default)).Single();
        var a=PrintPageItem.CreateBlank(SheetPaper.A4); a.Copies=2;
        a.Document.AddObject(SheetEditing.CreateText("A — ДВЕ КОПИИ",new(FontSizePt:32),new(25,35),safe));
        var b=PrintPageItem.CreateBlank(SheetPaper.A4);
        b.Document.AddObject(SheetEditing.CreateText("B — ОДНА КОПИЯ",new(FontSizePt:32),new(25,35),safe));
        var settings=new PrintSettings {PrinterName=hp,Paper=PaperPreset.A4,Dpi=600,Monochrome=MonochromeMode.Text};
        var logs=new List<string>(); using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await engine.PrintAsync([text.Snapshot()],settings.Snapshot(),new SynchronousProgress(logs),timeout.Token);
        settings.Monochrome=MonochromeMode.Photo;
        await engine.PrintAsync([photo.Snapshot()],settings.Snapshot(),new SynchronousProgress(logs),timeout.Token);
        settings.Monochrome=MonochromeMode.Text;
        await engine.PrintAsync([a.Snapshot(),b.Snapshot()],settings.Snapshot(),new SynchronousProgress(logs),timeout.Token);
        Assert.Contains(logs,x=>x.StartsWith("3/3 "));
        await engine.PrintAsync([text.Snapshot()],settings.Snapshot(Path.Combine(directory,"hp-text-reference.pdf")) with {PrinterName="Microsoft Print to PDF"},null,timeout.Token);
        await File.WriteAllLinesAsync(Path.Combine(directory,"hp-submission.txt"),logs);
    }
    private sealed class SynchronousProgress(List<string> rows) : IProgress<PrintProgress>
    { public void Report(PrintProgress value)=>rows.Add($"{value.Completed}/{value.Total} {value.Message}"); }
    private static string NewDirectory()
    {
        var path=Path.Combine(Fixtures.Root,"qa13","run-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path;
    }
    private static DataObject CaptureClipboard()
    {
        var snapshot=new DataObject(); var source=Clipboard.GetDataObject(); if(source==null) return snapshot;
        foreach(var format in source.GetFormats(false))
        {
            var value=source.GetData(format,false);
            if(value is BitmapSource bitmap) value=ClipboardService.FreezeImage(bitmap);
            if(value is MemoryStream stream) value=new MemoryStream(stream.ToArray(),false);
            if(value!=null) snapshot.SetData(format,value,false);
        }
        return snapshot;
    }
    private static Task OnSta(Func<Task> body)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async ()=>{try {await body(); completion.SetResult();} catch(Exception ex){completion.SetException(ex);} finally{dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}});
            Dispatcher.Run();
        }) {IsBackground=true}; thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
}

public sealed class DesktopAcceptanceFactAttribute : FactAttribute
{
    public DesktopAcceptanceFactAttribute() { if(Environment.GetEnvironmentVariable("PRINTMASTER_DESKTOP_ACCEPTANCE")!="1") Skip="Explicit desktop/clipboard acceptance run only."; }
}
public sealed class PhysicalA4FactAttribute : FactAttribute
{
    public PhysicalA4FactAttribute() { if(Environment.GetEnvironmentVariable("PRINTMASTER_PHYSICAL_A4")!="1") Skip="Requires user-confirmed HP with A4 loaded; sends five A4 impressions."; }
}
