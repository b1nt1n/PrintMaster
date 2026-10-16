using CommunityToolkit.Mvvm.Input;
using PrintMaster.Models;
using PrintMaster.Services;
using System.IO;

namespace PrintMaster.ViewModels;

public partial class PrintSessionViewModel
{
    private IPageExportService? _pageExporter;
    private bool CanExportPages()=>IsIdle && Pages.Count>0;
    [RelayCommand(CanExecute=nameof(CanExportPages))]
    private async Task ExportPagesAsync()
    {
        if(!CommitInputForAction()) return; FinishTextEditing();
        try
        {
            var highlighted=HighlightedPages.Select(p=>p.Id).ToHashSet();
            var candidates=Pages.Select(p=>new ExportCandidate(p.Snapshot(),p==SelectedPage,highlighted.Contains(p.Id),p.IsIncluded)).ToArray();
            var automatic=new SheetPaper(Settings.PaperSizeMm,Settings.Landscape,SheetMargins.SafeDefault) { Notebook=Settings.IsNotebook ? Settings.Notebook : null };
            var stem=CurrentProjectPath!=null ? Path.GetFileNameWithoutExtension(CurrentProjectPath) : SelectedPage?.IsBlank==false ? Path.GetFileNameWithoutExtension(SelectedPage.FileName)+"_sheet" : "Макет";
            using var model=new ExportViewModel(candidates,automatic,PrinterProfile,Settings.Monochrome,Settings.Threshold,Settings.Scaling,_dialogs,stem);
            if(!_dialogs.ReviewExport(model) || model.AcceptedPlan==null) return;
            BeginOperation(SessionOperationKind.Export,total:model.AcceptedPlan.Pages.Length);
            try
            {
                var progress=OperationProgress<ExportProgress>(p=>new(p.Message,p.Completed,p.Phase==ExportPhase.Rendering ? p.Total : null,
                    p.Phase switch { ExportPhase.AssemblingPdf=>"Сборка PDF",ExportPhase.CommittingFiles=>"Сохранение файлов",_=>"Подготовка экспорта" }));
                var exporter=_pageExporter ??=new PageExportService(new(_rasterizer));
                var result=await exporter.ExportAsync(model.AcceptedPlan,model.OverwriteConfirmed,progress,_operation!.Token);
                Status=result.Files.Count==1 ? "Экспорт сохранен: "+result.Files[0] : $"Экспортировано: {RussianQuantity.Format(result.Pages,"лист","листа","листов")} в {Path.GetDirectoryName(result.Files[0])}";
                if(CancellationRequested) Status+=". Запрос отмены поступил при завершении записи; выходные файлы уже сохранены.";
            }
            catch(OperationCanceledException) { Status="Экспорт отменен. Существующие выходные файлы сохранены."; }
            catch(Exception ex) { ShowOperationFailure("Ошибка экспорта","Проверьте папку, свободное место и исходники. Выберите другой путь или повторите экспорт.",ex); }
            finally { EndOperation(); }
        }
        catch(Exception ex) when(ex is ArgumentException or IOException or InvalidOperationException)
        { ShowOperationFailure("Экспорт недоступен","Проверьте исходники, параметры и путь сохранения в окне экспорта.",ex); }
    }
}
