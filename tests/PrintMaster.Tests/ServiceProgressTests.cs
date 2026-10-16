using PdfSharp.Pdf.IO;
using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.IO;
using System.Security.Cryptography;
using Xunit;

namespace PrintMaster.Tests;

public sealed class ServiceProgressTests
{
    [Fact]
    public async Task PdfImportReportsCopyingThenPageReadingAndKeepsOriginal()
    {
        var folder=Folder("pdf-import");
        var source=Fixtures.CreatePdf(Path.Combine(Path.GetRelativePath(Fixtures.Root,folder),"source.pdf"),2);
        var original=Hash(source); var reports=new List<ImportProgress>();
        using var workspace=new SessionWorkspace();

        var pages=await new FileImportService(workspace,new(),new()).ImportAsync(source,default,new ImmediateProgress<ImportProgress>(reports.Add));

        Assert.Equal(new[] { ImportPhase.Copying,ImportPhase.ReadingPdfPages },reports.Select(p=>p.Phase));
        Assert.Equal(2,pages.Count); Assert.All(pages,p=>Assert.Equal(SourceKind.Pdf,p.Kind));
        Assert.Equal(original,Hash(source));
        Assert.All(pages,p=>{ Assert.NotNull(p.WorkingPath); Assert.StartsWith(workspace.Root+Path.DirectorySeparatorChar,p.WorkingPath!); });
    }

    [Fact] [Trait("Category","Integration")]
    public async Task DocxImportReportsConversionBetweenCopyingAndPdfReading()
    {
        var source=Path.Combine(Folder("docx-import"),"source.docx");
        File.Copy(Fixtures.CreateDocx(),source); var original=Hash(source); var reports=new List<ImportProgress>();
        using var workspace=new SessionWorkspace();

        var pages=await new FileImportService(workspace,new(),new()).ImportAsync(source,default,new ImmediateProgress<ImportProgress>(reports.Add));

        Assert.Equal(new[] { ImportPhase.Copying,ImportPhase.ConvertingDocx,ImportPhase.ReadingPdfPages },reports.Select(p=>p.Phase));
        Assert.Equal(2,pages.Count); Assert.All(pages,p=>Assert.Equal(SourceKind.Docx,p.Kind));
        Assert.All(pages,p=>Assert.True(File.Exists(p.WorkingPath)));
        Assert.Equal(original,Hash(source));
    }

    [Theory]
    [InlineData(ExportFormat.Png)]
    [InlineData(ExportFormat.Pdf)]
    public async Task ExportReportsPreparationThenAssemblyAndCommitWithoutPrematureVisibleOutputs(ExportFormat format)
    {
        var folder=Folder("export-"+format); var extension=format==ExportFormat.Pdf ? ".pdf" : ".png";
        var pages=new[] { ExportPage(),ExportPage() };
        var options=new ExportOptions(format,ExportAppearance.Color,300,ScaleMode.Fit,MonochromeMode.Auto,160);
        var plan=ExportFilePlan.Build(pages,options,Path.Combine(folder,"pages"+extension));
        var reports=new List<ExportProgress>();
        var progress=new ImmediateProgress<ExportProgress>(p=>
        {
            reports.Add(p);
            Assert.All(plan.Files,file=>Assert.False(File.Exists(file.Path)));
        });

        var result=await new PageExportService(new(new(new()))).ExportAsync(plan,false,progress,default);

        var expected=format==ExportFormat.Pdf
            ? new[] { ExportPhase.Rendering,ExportPhase.Rendering,ExportPhase.Rendering,ExportPhase.Rendering,ExportPhase.AssemblingPdf,ExportPhase.CommittingFiles }
            : new[] { ExportPhase.Rendering,ExportPhase.Rendering,ExportPhase.Rendering,ExportPhase.Rendering,ExportPhase.CommittingFiles };
        Assert.Equal(expected,reports.Select(p=>p.Phase));
        Assert.Equal(new[] { 0,1,1,2 },reports.Where(p=>p.Phase==ExportPhase.Rendering).Select(p=>p.Completed));
        Assert.All(reports,p=>Assert.Equal(2,p.Total)); Assert.Equal(2,result.Pages);
        Assert.Equal(format==ExportFormat.Pdf ? 1 : 2,result.Files.Count);
        Assert.All(result.Files,file=>Assert.True(new FileInfo(file).Length>100));
        Assert.Empty(Directory.GetDirectories(folder,".printmaster-export-*"));
        if(format==ExportFormat.Pdf)
        {
            using var document=PdfReader.Open(result.Files[0],PdfDocumentOpenMode.Import);
            Assert.Equal(2,document.PageCount);
        }
    }

    [Fact]
    public async Task CoordinatorForwardsPhasesSynchronouslyAndKeepsGlobalCountsAcrossStocks()
    {
        var first=Page(copies:2); var second=Page(paper:new(new(148,210),false,SheetMargins.SafeDefault));
        var reports=new List<PrintProgress>(); var runner=new NoPaperRunner(); var dialogs=new Prompts();
        runner.AfterReport=sent=>
        {
            Assert.NotEmpty(reports); var received=reports.Last();
            var offset=runner.Calls.Take(runner.Calls.Count-1).Sum(p=>p.Feeds);
            Assert.Equal(sent.Message,received.Message); Assert.Equal(sent.Phase,received.Phase);
            Assert.Equal(offset+sent.Completed,received.Completed); Assert.Equal(3,received.Total);
        };
        var workflow=new PaperPrintWorkflow(runner,dialogs,_=>null,_=>true,_=>Task.CompletedTask);
        var queuedContext=new QueuingContext(); var previous=SynchronizationContext.Current;
        Task<bool> operation;
        try
        {
            SynchronizationContext.SetSynchronizationContext(queuedContext);
            operation=workflow.StartAsync([first,second],Settings(),new ImmediateProgress<PrintProgress>(reports.Add),default);
            Assert.True(operation.IsCompleted);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }

        Assert.True(await operation); Assert.Empty(queuedContext.Posted);
        Assert.Equal(2,runner.Calls.Count); Assert.False(workflow.HasPending);
        Assert.Equal(new[] { PaperGuideStep.ChangeStock },dialogs.Steps);
        Assert.Contains(reports,p=>p.Phase==PrintPhase.WaitingForPaper && p.Completed==2 && p.Total==3);
        Assert.All(reports.Zip(reports.Skip(1)),pair=>Assert.True(pair.First.Completed<=pair.Second.Completed));
        Assert.Equal(3,reports.Last().Completed); Assert.Equal(PrintPhase.Queuing,reports.Last().Phase);
    }

    [Fact]
    public async Task MandatoryFlipReportsWaitingBeforePromptAndResumeOnlySendsBack()
    {
        var front=Page(); var back=Page(); var reports=new List<PrintProgress>(); var runner=new NoPaperRunner();
        var dialogs=new Prompts(); dialogs.Decisions.Enqueue(false); dialogs.Decisions.Enqueue(true);
        dialogs.BeforePrompt=model=>
        {
            Assert.Equal(PaperGuideStep.Reload,model.Step);
            Assert.Equal(PrintPhase.WaitingForTurn,reports.Last().Phase);
            Assert.Equal(1,reports.Last().Completed); Assert.Equal(2,reports.Last().Total);
        };
        var workflow=new PaperPrintWorkflow(runner,dialogs,_=>null,_=>true,_=>Task.CompletedTask);
        var progress=new ImmediateProgress<PrintProgress>(reports.Add);

        Assert.False(await workflow.StartAsync([front,back],Settings() with { ManualDuplex=true },progress,default));
        Assert.True(workflow.HasPending); Assert.Equal(front.Id,Assert.Single(runner.Calls).Pages.Single().Id);
        Assert.Equal(PrintPhase.WaitingForTurn,reports.Last().Phase);

        Assert.True(await workflow.ContinueAsync(progress,default)); Assert.False(workflow.HasPending);
        Assert.Equal(2,runner.Calls.Count); Assert.Equal(back.Id,runner.Calls[1].Pages.Single().Id);
        Assert.Equal(2,reports.Last().Completed); Assert.Equal(2,reports.Last().Total);
        Assert.All(reports.Zip(reports.Skip(1)),pair=>Assert.True(pair.First.Completed<=pair.Second.Completed));
    }

    [Fact]
    public async Task DecliningNotebookLoadReportsWaitingAndSendsNothing()
    {
        var reports=new List<PrintProgress>(); var runner=new NoPaperRunner(); var dialogs=new Prompts();
        dialogs.Decisions.Enqueue(false);
        dialogs.BeforePrompt=model=>
        {
            Assert.Equal(PaperGuideStep.Load,model.Step); Assert.Empty(runner.Calls);
            Assert.Equal(PrintPhase.WaitingForPaper,reports.Last().Phase);
        };
        var page=Page(paper:NotebookProfiles.Default(NotebookVariant.GridSingle).ToPaper());
        var workflow=new PaperPrintWorkflow(runner,dialogs,_=>null,_=>false,_=>Task.CompletedTask);

        Assert.False(await workflow.StartAsync([page],Settings(),new ImmediateProgress<PrintProgress>(reports.Add),default));

        var report=Assert.Single(reports);
        Assert.Equal(PrintPhase.WaitingForPaper,report.Phase); Assert.Equal(0,report.Completed); Assert.Equal(1,report.Total);
        Assert.Empty(runner.Calls); Assert.True(workflow.HasPending);
    }

    private static string Folder(string label)
    {
        var path=Path.Combine(Fixtures.Root,"ux014-service",label+"-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    private static byte[] Hash(string path)=>SHA256.HashData(File.ReadAllBytes(path));
    private static PageSnapshot Page(int copies=1,SheetPaper? paper=null)=>PrintPageItem.CreateBlank(paper ?? SheetPaper.A4).Snapshot() with { Copies=copies };
    private static PrintSettingsSnapshot Settings()=>new PrintSettings { PrinterName="Test printer — no hardware" }.Snapshot();
    private static ExportPage ExportPage()
    {
        var page=PrintPageItem.CreateBlank(new(new(60,70),false,SheetMargins.SafeDefault));
        page.Document.AddObject(new ShapeObject(Guid.NewGuid(),new(new(10,10,20,20)),SheetShapeKind.Rectangle,new("#175E9C",.7,1,"#33AA77")));
        return SheetExportRenderer.Prepare(page.Snapshot(),SheetPaper.A4);
    }
    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    { public void Report(T value)=>report(value); }
    private sealed class QueuingContext : SynchronizationContext
    {
        public List<(SendOrPostCallback Callback,object? State)> Posted { get; }=[];
        public override void Post(SendOrPostCallback callback,object? state)=>Posted.Add((callback,state));
    }
    private sealed class NoPaperRunner : IPrintJobRunner
    {
        public List<PrintPacket> Calls { get; }=[];
        public Action<PrintProgress>? AfterReport { get; set; }
        public Task PrintAsync(IReadOnlyList<PageSnapshot> pages,PrintSettingsSnapshot settings,IProgress<PrintProgress>? progress,CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls.Add(new(pages.ToArray(),settings)); var total=pages.Sum(p=>p.Copies);
            void Report(PrintProgress value) { progress?.Report(value); AfterReport?.Invoke(value); }
            Report(new(0,total,$"probe-{Calls.Count}",PrintPhase.Probing));
            for(var completed=0;completed<total;completed++)
            {
                Report(new(completed,total,$"render-{Calls.Count}-{completed}",PrintPhase.Rendering));
                Report(new(completed+1,total,$"queue-{Calls.Count}-{completed+1}",PrintPhase.Queuing));
            }
            return Task.CompletedTask;
        }
    }
    private sealed class Prompts : IUserDialogs
    {
        public Queue<bool> Decisions { get; }=new();
        public List<PaperGuideStep> Steps { get; }=[];
        public Action<PaperGuideViewModel>? BeforePrompt { get; set; }
        public bool ShowPaperGuide(PaperGuideViewModel model)
        { BeforePrompt?.Invoke(model); Steps.Add(model.Step); return Decisions.Count==0 || Decisions.Dequeue(); }
        public string[] PickFiles()=>[]; public string? PickPdfOutput()=>null; public string? PickLibreOffice()=>null;
        public bool ConfirmCropping(int count)=>false; public void Error(string message) { }
    }
}
