using PrintMaster.Models;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.Runtime.InteropServices;

namespace PrintMaster.Services;

public interface IPrintJobRunner
{
    Task PrintAsync(IReadOnlyList<PageSnapshot> pages,PrintSettingsSnapshot settings,IProgress<PrintProgress>? progress,CancellationToken cancellationToken);
}
public sealed class PrintEngine(PageRasterizer rasterizer,PrinterInterop interop) : IPrintJobRunner
{
    private readonly SemaphoreSlim _jobGate=new(1,1);

    public async Task PrintAsync(IReadOnlyList<PageSnapshot> pages,PrintSettingsSnapshot settings,
        IProgress<PrintProgress>? progress,CancellationToken cancellationToken)
    {
        // Validate the complete queue before entering native code or submitting any page.
        ManualDuplexPlanner.ValidateQueue(pages,settings);
        if(!settings.AllowNotebookOverflow && pages.Any(p=>p.Sheet is {} sheet && NotebookTextLayout.Remainder(sheet).Length!=0))
            throw new InvalidOperationException("Текст тетради не помещается. Требуется отдельное подтверждение печати поместившейся части.");
        if(settings.PageRotationDegrees is not (0 or 180)) throw new ArgumentException("Недопустимый поворот печати.");
        if(pages.Any(p=>p.Sheet?.Assets.Any(a=>string.IsNullOrEmpty(a.WorkingPath) || !System.IO.File.Exists(a.WorkingPath))==true))
            throw new InvalidOperationException("Для выбранных страниц отсутствуют исходники. Переподключите файлы проекта.");
        await _jobGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Native setup, rasterization, spooler IO and printer warm-up never block the dispatcher.
            await Task.Run(()=>RunJob(pages,settings,progress,cancellationToken),cancellationToken).ConfigureAwait(false);
        }
        finally { _jobGate.Release(); }
    }

    private void RunJob(IReadOnlyList<PageSnapshot> pages,PrintSettingsSnapshot settings,IProgress<PrintProgress>? progress,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var total=checked(pages.Sum(x=>x.Copies));
        progress?.Report(new(0,total,"Проверка принтера и размера бумаги…",PrintPhase.Probing));
        var effective=pages.Select(page=>ManualDuplexPlanner.ForPage(page,settings)).ToArray();
        if(settings.OutputPath!=null && effective.Skip(1).Any(s=>!ManualDuplexPlanner.SameStock(effective[0],s)))
            throw new InvalidOperationException("Размер бумаги в очереди различается. Для PDF выберите листы одного формата.");
        var profiles=new Dictionary<PrintSettingsSnapshot,PrinterProfile>();
        for(var i=0;i<pages.Count;i++)
        {
            token.ThrowIfCancellationRequested(); var pageSettings=effective[i]; var key=NativeSettings(pageSettings);
            if(!profiles.TryGetValue(key,out var profile)) profiles.Add(key,profile=interop.Probe(pageSettings));
            var safeMm=PageLayout.PrintSafeArea(profile.ActualPaperMm,profile.PrintableMm,pageSettings.SafeMarginMm);
            if(!pageSettings.AllowCropping && pages[i].UsesAutomaticContentLayout && PageLayout.Crops(pages[i].ContentSize,safeMm,pageSettings.Scaling))
                throw new InvalidOperationException($"Страница {i+1}: Fill обрежет края. Подтвердите обрезку или выберите Fit.");
            if(!pageSettings.AllowCropping && SheetEditing.HasClipping(pages[i],PrintViewport.SourceArea(pageSettings,safeMm)))
                throw new InvalidOperationException($"Страница {i+1}: объекты выходят за безопасную печатную область. Подтвердите обрезку.");
        }
        var completed=0;
        using var iterator=PageLayout.ExpandCopies(pages).GetEnumerator();
        if(!iterator.MoveNext()) return;
        var native=new Dictionary<PrintSettingsSnapshot,(PrinterSettings Printer,PageSettings Page,IntPtr Mode)>();
        try
        {
            // Prepare every distinct paper/orientation/DPI before Print(), so a later
            // unsupported page cannot leave an earlier page already submitted.
            foreach(var target in effective.Select(NativeSettings).Distinct())
            {
                token.ThrowIfCancellationRequested();
                var (targetPrinter,targetPage)=PrinterInterop.CreateSettings(target);
                var targetMode=PrinterInterop.CreateValidatedDevMode(target,targetPrinter,targetPage);
                native.Add(target,(targetPrinter,targetPage,targetMode));
                if(settings.OutputPath!=null) { targetPrinter.PrintToFile=true; targetPrinter.PrintFileName=settings.OutputPath; }
            }
            var first=native[NativeSettings(effective[0])];
            var printer=first.Printer; var pageSettings=first.Page;
            using var document=new PrintDocument { PrinterSettings=printer,DefaultPageSettings=pageSettings,
                OriginAtMargins=false,PrintController=new StandardPrintController(),DocumentName="PrintMaster — "+DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
            Exception? pageError=null;
            document.BeginPrint+=(_,e)=>{ if(token.IsCancellationRequested) e.Cancel=true; };
            document.QueryPageSettings+=(_,e)=>
            {
                if(token.IsCancellationRequested) { e.Cancel=true; return; }
                try
                {
                    // QueryPageSettings immediately precedes this iterator's PrintPage.
                    // DPI can differ inside one PDF job without reopening its output file.
                    var currentSettings=ManualDuplexPlanner.ForPage(iterator.Current,settings);
                    var setup=native[NativeSettings(currentSettings)];
                    e.PageSettings=(PageSettings)setup.Page.Clone();
                    PrinterInterop.SetPageDevMode(e.PageSettings,setup.Mode); e.PageSettings.Margins=setup.Page.Margins;
                }
                catch(Exception error) { pageError=error; e.Cancel=true; }
            };
            document.PrintPage+=(_,e)=>
            {
                if(token.IsCancellationRequested) { e.Cancel=true; return; }
                try
                {
                    var current=iterator.Current; var currentSettings=ManualDuplexPlanner.ForPage(current,settings);
                    var graphics=e.Graphics ?? throw new InvalidOperationException("Драйвер не предоставил Graphics.");
                    var physical=new RectD(e.PageBounds.X,e.PageBounds.Y,e.PageBounds.Width,e.PageBounds.Height);
                    var margins=new RectD(e.MarginBounds.X,e.MarginBounds.Y,e.MarginBounds.Width,e.MarginBounds.Height);
                    // .NET 8 PrintableArea swaps an already landscape-oriented HP DC again.
                    // Read the actual PrintPage HDC instead, with no assumed symmetric/rotated margins.
                    var actual=interop.ReadGraphicsProfile(graphics,currentSettings.PrinterName);
                    var hardware=actual.PrintableMm;
                    const double hundredthsPerMm=100/25.4;
                    var safe=physical.Intersect(margins).Intersect(new(hardware.X*hundredthsPerMm,hardware.Y*hundredthsPerMm,
                        hardware.Width*hundredthsPerMm,hardware.Height*hundredthsPerMm));
                    if(safe.IsEmpty) throw new InvalidOperationException("Недоступна печатная область этой страницы.");
                    var expected=currentSettings.OrientedPaperMm;
                    if(Math.Abs(PageLayout.HundredthsToMm(physical.Width)-expected.Width)>1.5 || Math.Abs(PageLayout.HundredthsToMm(physical.Height)-expected.Height)>1.5)
                        throw new InvalidOperationException("Драйвер изменил размер бумаги при начале печати.");
                    var actualSafeMm=new RectD(safe.X/hundredthsPerMm,safe.Y/hundredthsPerMm,safe.Width/hundredthsPerMm,safe.Height/hundredthsPerMm);
                    var sourceArea=PrintViewport.SourceArea(currentSettings,actualSafeMm);
                    if(!currentSettings.AllowCropping && SheetEditing.HasClipping(current,sourceArea))
                        throw new InvalidOperationException("Драйвер изменил поля: объекты листа будут обрезаны. Проверьте печатный вид.");
                    if(!currentSettings.AllowCropping && current.UsesAutomaticContentLayout && PageLayout.Crops(current.ContentSize,safe,currentSettings.Scaling))
                        throw new InvalidOperationException("Драйвер изменил поля: возникла обрезка. Проверьте настройки.");
                    var destination=PageLayout.Place(current.ContentSize,safe,currentSettings.Scaling);
                    // Only this page exists at print DPI. Dispose executes before the next PrintPage.
                    var width=checked((int)Math.Ceiling(safe.Width/100*currentSettings.Dpi));
                    var height=checked((int)Math.Ceiling(safe.Height/100*currentSettings.Dpi));
                    var destinationMm=new RectD(destination.X/hundredthsPerMm,destination.Y/hundredthsPerMm,destination.Width/hundredthsPerMm,destination.Height/hundredthsPerMm);
                    var viewportDestination=new RectD((destinationMm.X-sourceArea.X)/sourceArea.Width*width,(destinationMm.Y-sourceArea.Y)/sourceArea.Height*height,
                        destinationMm.Width/sourceArea.Width*width,destinationMm.Height/sourceArea.Height*height);
                    progress?.Report(new(completed,total,$"Подготовка {completed+1}/{total}: {System.IO.Path.GetFileName(current.OriginalPath)}",PrintPhase.Rendering));
                    using(var bitmap=rasterizer.RenderProcessed(current,width,height,viewportDestination,currentSettings.Monochrome,
                        currentSettings.Threshold,currentSettings.Dpi,token,sourceArea))
                    {
                        if(currentSettings.PageRotationDegrees==180) bitmap.RotateFlip(RotateFlipType.Rotate180FlipNone);
                        var state=graphics.Save();
                        try
                        {
                            graphics.PageUnit=GraphicsUnit.Display; graphics.PageScale=1;
                            graphics.TranslateTransform((float)(-hardware.X*hundredthsPerMm),(float)(-hardware.Y*hundredthsPerMm));
                            graphics.SetClip(new RectangleF((float)safe.X,(float)safe.Y,(float)safe.Width,(float)safe.Height),CombineMode.Intersect);
                            graphics.InterpolationMode=InterpolationMode.NearestNeighbor; graphics.PixelOffsetMode=PixelOffsetMode.Half;
                            graphics.DrawImage(bitmap,new RectangleF((float)safe.X,(float)safe.Y,(float)safe.Width,(float)safe.Height),
                                new RectangleF(0,0,bitmap.Width,bitmap.Height),GraphicsUnit.Pixel);
                        }
                        finally { graphics.Restore(state); }
                    }
                    completed++;
                    progress?.Report(new(completed,total,$"В очередь Windows передается {completed}/{total} оттисков",PrintPhase.Queuing));
                    e.HasMorePages=iterator.MoveNext();
                    if(token.IsCancellationRequested) e.Cancel=true;
                }
                catch(Exception error) { pageError=error; e.Cancel=true; e.HasMorePages=false; }
            };
            progress?.Report(new(0,total,"Передача задания в очередь Windows. Разогрев может занять 10–12 секунд…",PrintPhase.Queuing));
            document.Print();
            if(pageError!=null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(pageError).Throw();
            token.ThrowIfCancellationRequested();
        }
        finally { foreach(var setup in native.Values) Marshal.FreeHGlobal(setup.Mode); }
    }
    // These fields affect rendering or workflow only, not the Windows DEVMODE.
    private static PrintSettingsSnapshot NativeSettings(PrintSettingsSnapshot value)=>value with
    { Scaling=ScaleMode.Fit,Monochrome=MonochromeMode.Auto,Threshold=160,Notebook=null,ManualDuplex=false,
        PrintOffsetXmm=0,PrintOffsetYmm=0,AllowCropping=false,AllowNotebookOverflow=false,PageRotationDegrees=0 };
}
