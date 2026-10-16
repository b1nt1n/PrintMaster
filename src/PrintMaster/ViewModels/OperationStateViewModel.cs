using CommunityToolkit.Mvvm.ComponentModel;
using PrintMaster.Models;
using PrintMaster.Services;

namespace PrintMaster.ViewModels;

public partial class PrintSessionViewModel
{
    private long _operationEpoch,_lastReport,_issuedReport;
    private SessionOperationKind _operationKind;
    private bool _operationReportsClosed;
    [ObservableProperty] private string operationLabel="";
    [ObservableProperty] private bool isProgressIndeterminate;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CancelCommand))] private bool cancellationRequested;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PreviewActivity))] private string previewError="";
    public string PreviewActivity=>PreviewError.Length>0 ? PreviewError : IsPreviewLoading ? "Загрузка превью…" : "";
    public string ProgressDescription=>IsProgressIndeterminate ? "Объём работы определяется" : $"{ProgressCompleted} из {ProgressTotal}";
    private bool CanCancelOperation()=>IsBusy && !CancellationRequested;
    private static string OperationName(SessionOperationKind kind)=>kind switch
    {
        SessionOperationKind.Import=>"Импорт",SessionOperationKind.InsertImages=>"Вставка фотографий",SessionOperationKind.ClipboardRead=>"Чтение буфера",
        SessionOperationKind.ClipboardCopy=>"Копирование",SessionOperationKind.ClipboardInsert=>"Вставка",SessionOperationKind.OpenProject=>"Открытие проекта",
        SessionOperationKind.SaveProject=>"Сохранение проекта",SessionOperationKind.SaveImage=>"Сохранение копии",SessionOperationKind.Export=>"Экспорт",
        SessionOperationKind.Print=>"Печать",SessionOperationKind.Relink=>"Переподключение материалов",SessionOperationKind.SavePreferences=>"Сохранение настроек",_=>"Обработка"
    };
    internal void BeginOperation(SessionOperationKind kind,string? message=null,int? total=null)
    {
        if(IsBusy) throw new InvalidOperationException("Дождитесь завершения текущей операции.");
        _operationEpoch++; _lastReport=0; _issuedReport=0; _operationKind=kind; _operationReportsClosed=false;
        _operation=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        ProgressCompleted=0; ProgressTotal=Math.Max(1,total ?? 1); IsProgressIndeterminate=total==null; CancellationRequested=false;
        OperationLabel=OperationName(kind); Status=message ?? OperationLabel+"…"; IsBusy=true;
        NotifyProgressState();
    }
    internal void EndOperation()
    {
        _operationEpoch++; _operation?.Dispose(); _operation=null; IsBusy=false; CancellationRequested=false;
        OperationLabel=""; IsProgressIndeterminate=false; _operationKind=SessionOperationKind.None; NotifyProgressState();
    }
    private void NotifyProgressState() { OnPropertyChanged(nameof(ProgressDescription)); }
    private void ApplyOperationReport(OperationReport value)
    {
        _lastReport=Interlocked.Increment(ref _issuedReport);
        SetOperationReport(value);
    }
    private void SetOperationReport(OperationReport value)
    {
        if(!IsBusy || CancellationRequested || _operationReportsClosed) return;
        OperationLabel=value.Phase ?? OperationName(_operationKind); Status=value.Message;
        IsProgressIndeterminate=value.Total==null;
        if(value.Total is {} total)
        { ProgressTotal=Math.Max(1,total); ProgressCompleted=Math.Clamp(value.Completed,0,ProgressTotal); }
        NotifyProgressState();
    }
    private IProgress<T> OperationProgress<T>(Func<T,OperationReport> map)
    {
        var epoch=_operationEpoch; var context=SynchronizationContext.Current;
        return new ForwardProgress<T>(value=>
        {
            var report=map(value); var order=Interlocked.Increment(ref _issuedReport);
            void Apply()
            {
                if(_disposed || !IsBusy || epoch!=_operationEpoch || order<=_lastReport || CancellationRequested || _operationReportsClosed) return;
                _lastReport=order; SetOperationReport(report);
            }
            if(context!=null && context!=SynchronizationContext.Current) context.Post(_=>Apply(),null); else Apply();
        });
    }
    private IProgress<ImportProgress> ImportProgressFor(string file,int completed,int total)=>OperationProgress<ImportProgress>(p=>new(
        $"{completed+1}/{total} · {System.IO.Path.GetFileName(file)} · {p.Message}",completed,
        p.Phase==ImportPhase.ConvertingDocx ? null : total,p.Phase switch
        { ImportPhase.ConvertingDocx=>"Конвертация DOCX",ImportPhase.Copying=>"Копирование исходника",ImportPhase.ReadingPdfPages=>"Разбор страниц",_=>"Чтение изображения" }));
    private IProgress<PrintProgress> PrintOperationProgress()=>OperationProgress<PrintProgress>(p=>new(p.Message,p.Completed,
        p.Phase is PrintPhase.Probing or PrintPhase.WaitingForPaper or PrintPhase.WaitingForTurn ? null : p.Total,p.Phase switch
        { PrintPhase.Probing=>"Проверка принтера",PrintPhase.Rendering=>"Подготовка печати",PrintPhase.Queuing=>"Передача в очередь Windows",PrintPhase.WaitingForPaper=>"Ожидание бумаги",_=>"Ожидание переворота" }));
    private void RequestOperationCancellation()
    {
        if(!CanCancelOperation()) return; CancellationRequested=true; _operation?.Cancel();
        Status=_operationKind switch
        {
            SessionOperationKind.Print=>"Отмена печати запрошена. Уже переданные Windows оттиски могут выйти из принтера.",
            SessionOperationKind.Import or SessionOperationKind.InsertImages=>"Отмена импорта запрошена. Уже добавленное содержимое останется доступным.",
            SessionOperationKind.Export=>"Отмена экспорта запрошена. Ожидаем безопасного завершения текущего этапа записи.",
            SessionOperationKind.SaveProject or SessionOperationKind.SaveImage=>"Отмена сохранения запрошена. Ожидаем завершения записи без потери предыдущего файла.",
            SessionOperationKind.OpenProject=>"Отмена открытия запрошена. Текущий проект сохраняется.",
            _=>"Отмена операции запрошена. Ожидаем её безопасного завершения."
        };
        OperationLabel="Отмена · "+OperationName(_operationKind);
    }
    private void ShowOperationFailure(string title,string nextAction,Exception error)
    {
        FreezeOperationReports();
        var reason=error.Message.Split('\n')[0].Trim(); if(reason.Length>250) reason=reason[..250]+"…";
        Status=title+": "+reason+" "+nextAction;
        _dialogs.ShowOperationError(new(title,title+": "+reason,nextAction,error.ToString()));
    }
    private void FreezeOperationReports()=>_operationReportsClosed=true;
    internal IProgress<OperationReport> OperationReporter()=>OperationProgress<OperationReport>(x=>x);
}
