using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.IO;
using Xunit;

namespace PrintMaster.Tests;

public sealed class OperationStateTests
{
    private static PrintSessionViewModel Session(IUserDialogs? dialogs=null)=>new(dialogs ?? new Dialogs(),
        Path.Combine(Fixtures.Root,"ux014-operations-"+Guid.NewGuid().ToString("N")));
    [Fact]
    public void BeginUnknownOperation_AfterCompletedPrint_ResetsCountersAndShowsWaiting()
    {
        using var vm=Session(); vm.BeginOperation(SessionOperationKind.Print,total:9);
        vm.OperationReporter().Report(new("В очередь Windows передано 9",9,9)); vm.EndOperation();
        vm.BeginOperation(SessionOperationKind.SaveProject);
        Assert.Equal(0,vm.ProgressCompleted); Assert.Equal(1,vm.ProgressTotal); Assert.True(vm.IsProgressIndeterminate);
        Assert.Equal("Сохранение проекта",vm.OperationLabel); Assert.DoesNotContain("9",vm.ProgressDescription); vm.EndOperation();
        vm.BeginOperation(SessionOperationKind.Import,total:3); Assert.False(vm.IsProgressIndeterminate); Assert.Equal(3,vm.ProgressTotal); vm.EndOperation();
    }
    [Fact]
    public void LateReport_AfterEndOrNewOperation_CannotOverwriteCurrentStatus()
    {
        using var vm=Session(); var queue=new QueuedContext(); var previous=SynchronizationContext.Current;
        vm.BeginOperation(SessionOperationKind.Print,total:3); IProgress<OperationReport> progress;
        try { SynchronizationContext.SetSynchronizationContext(queue); progress=vm.OperationReporter(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        progress.Report(new("старое",2,3)); vm.EndOperation(); vm.Status="Задание передано Windows"; queue.Drain();
        Assert.Equal("Задание передано Windows",vm.Status);
        vm.BeginOperation(SessionOperationKind.OpenProject); progress.Report(new("старое после открытия",3,3)); queue.Drain();
        Assert.Equal("Открытие проекта…",vm.Status); Assert.True(vm.IsProgressIndeterminate); Assert.Equal(0,vm.ProgressCompleted); vm.EndOperation();
    }
    [Fact]
    public void OutOfOrderReports_KeepNewestAndAllReporterInstancesShareSequence()
    {
        using var vm=Session(); vm.BeginOperation(SessionOperationKind.Export,total:4); var queue=new QueuedContext(); var old=SynchronizationContext.Current;
        IProgress<OperationReport> first,second;
        try { SynchronizationContext.SetSynchronizationContext(queue); first=vm.OperationReporter(); second=vm.OperationReporter(); }
        finally { SynchronizationContext.SetSynchronizationContext(old); }
        first.Report(new("первый",1,4)); first.Report(new("второй",2,4)); second.Report(new("новый этап",3,4)); queue.DrainReverse();
        Assert.Equal("новый этап",vm.Status); Assert.Equal(3,vm.ProgressCompleted); vm.EndOperation();
    }
    [Theory]
    [InlineData(SessionOperationKind.Print,"Windows")]
    [InlineData(SessionOperationKind.Import,"добавленное")]
    [InlineData(SessionOperationKind.SaveProject,"предыдущего файла")]
    [InlineData(SessionOperationKind.Export,"записи")]
    [InlineData(SessionOperationKind.OpenProject,"Текущий проект")]
    public void CancellationRequest_IsSpecificAndCannotBeRequestedTwice(SessionOperationKind kind,string text)
    {
        using var vm=Session(); vm.BeginOperation(kind); var report=vm.OperationReporter();
        Assert.True(vm.CancelCommand.CanExecute(null)); vm.CancelCommand.Execute(null);
        Assert.True(vm.CancellationRequested); Assert.Contains(text,vm.Status); Assert.False(vm.CancelCommand.CanExecute(null));
        var message=vm.Status; report.Report(new("Поздний отчет",10,10)); Assert.Equal(message,vm.Status); vm.EndOperation();
    }
    [Fact]
    public void CommitStage_UsesWaitingAfterAllRastersArePrepared()
    {
        using var vm=Session(); vm.BeginOperation(SessionOperationKind.Export,total:2); var report=vm.OperationReporter();
        report.Report(new("Подготовлено",2,2,"Подготовка экспорта")); Assert.False(vm.IsProgressIndeterminate);
        report.Report(new("Сборка PDF",2,null,"Сборка PDF")); Assert.True(vm.IsProgressIndeterminate); Assert.Equal("Сборка PDF",vm.OperationLabel);
        Assert.True(vm.IsBusy); vm.EndOperation();
    }
    [Fact]
    public async Task CancelImport_PreservesFirstPageAndExplainsPartialResult()
    {
        using var vm=Session(); var first=Fixtures.CreateImage("ux014-cancel-first.png"); var second=Fixtures.CreateImage("ux014-cancel-second.png");
        vm.Pages.CollectionChanged+=(_,_)=>{ if(vm.Pages.Count==1) vm.CancelCommand.Execute(null); };
        await vm.ImportFilesAsync([first,second]); Assert.Single(vm.Pages); Assert.False(vm.IsBusy);
        Assert.Contains("1 страница",vm.Status); Assert.Contains("добавленное содержимое доступно",vm.Status); Assert.True(File.Exists(first)); Assert.True(File.Exists(second));
    }
    [Fact]
    public async Task PreviewFailure_DuringForegroundOperation_DoesNotReplaceItsStatus()
    {
        using var vm=Session(); var missing=PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Image,"absent.png",null,0,1,new(100,100),new(40,40)));
        vm.BeginOperation(SessionOperationKind.SaveProject,"Сохранение макета…"); vm.AddPage(missing); vm.SelectedPage=missing;
        await Task.Delay(180); Assert.Equal("Сохранение макета…",vm.Status); Assert.Contains("Переподключите",vm.PreviewActivity); vm.EndOperation();
    }
    [Fact]
    public async Task SaveFailure_ProvidesRecoveryActionAndSeparateTechnicalDetails()
    {
        var dialogs=new Dialogs(); using var vm=Session(dialogs); vm.AddPage(PrintPageItem.CreateBlank(SheetPaper.A4));
        var target=Path.Combine(Fixtures.Root,"ux014-folder-"+Guid.NewGuid().ToString("N")+".printmaster"); Directory.CreateDirectory(target);
        Assert.False(await vm.SaveProjectToAsync(target)); var error=Assert.Single(dialogs.Errors);
        Assert.Contains("Ошибка сохранения",error.Summary); Assert.Contains("папку",error.NextAction); Assert.Contains("Exception",error.Details);
        Assert.True(vm.HasUnsavedChanges); Assert.False(vm.IsBusy);
    }
    [Fact]
    public async Task ProgressDuringErrorDialog_CannotReplaceTerminalFailure()
    {
        var dialogs=new Dialogs(); using var vm=Session(dialogs); vm.AddPage(PrintPageItem.CreateBlank(SheetPaper.A4));
        dialogs.OnError=_=>vm.OperationReporter().Report(new("Запоздавшая подготовка",1,1));
        var path=Path.Combine(Fixtures.Root,"ux014-error-progress-"+Guid.NewGuid().ToString("N")+".printmaster");Directory.CreateDirectory(path);
        Assert.False(await vm.SaveProjectToAsync(path)); Assert.Contains("Ошибка сохранения проекта",vm.Status); Assert.DoesNotContain("Запоздавшая",vm.Status);
    }
    private sealed class Dialogs : IUserDialogs
    {
        public List<OperationErrorInfo> Errors=[];
        public Action<OperationErrorInfo>? OnError;
        public string[] PickFiles()=>[]; public string? PickPdfOutput()=>null; public string? PickLibreOffice()=>null;
        public bool ConfirmCropping(int count)=>false; public void Error(string message)=>throw new InvalidOperationException(message);
        public void ShowOperationError(OperationErrorInfo info){Errors.Add(info);OnError?.Invoke(info);}
    }
    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly List<(SendOrPostCallback Callback,object? State)> _queue=[];
        public override void Post(SendOrPostCallback callback,object? state)=>_queue.Add((callback,state));
        public void Drain(){var entries=_queue.ToArray();_queue.Clear();foreach(var e in entries)e.Callback(e.State);}
        public void DrainReverse(){var entries=_queue.AsEnumerable().Reverse().ToArray();_queue.Clear();foreach(var e in entries)e.Callback(e.State);}
    }
}
