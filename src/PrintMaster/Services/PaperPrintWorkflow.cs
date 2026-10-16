using PrintMaster.Models;
using PrintMaster.ViewModels;

namespace PrintMaster.Services;

// This foreground coordinator owns prompts and immutable job snapshots.
// PrintEngine owns background native printing; callbacks resume on the calling UI context.
public sealed class PaperPrintWorkflow(IPrintJobRunner runner,IUserDialogs dialogs,
    Func<PrintSettingsSnapshot,PrinterPaperCalibration?> calibration,
    Func<PrintSettingsSnapshot,bool> hiddenGuide,Func<PaperGuideViewModel,Task> rememberGuide)
{
    private sealed class Job
    {
        public IReadOnlyList<IReadOnlyList<DuplexSheet>>? Batches;
        public IReadOnlyList<PrintPacket> Packets=[];
        public int BatchIndex,PacketIndex,CompletedFeeds,TotalFeeds;
        public bool BackPass,WaitingFlip;
        public PrintSettingsSnapshot? LastStock;
    }
    private Job? _pending;
    public bool HasPending=>_pending!=null;
    public string PendingLabel=>_pending?.WaitingFlip==true ? "Продолжить · печатать оборот" : "Продолжить печать";
    public void Discard()=>_pending=null;
    public async Task<bool> StartAsync(IReadOnlyList<PageSnapshot> pages,PrintSettingsSnapshot settings,IProgress<PrintProgress>? progress,CancellationToken token)
    {
        if(_pending!=null) throw new InvalidOperationException("Сначала завершите или закройте ожидающее задание.");
        ManualDuplexPlanner.ValidateQueue(pages,settings);
        var job=new Job();
        if(settings.ManualDuplex && settings.OutputPath==null)
        {
            var c=calibration(settings); var plan=ManualDuplexPlanner.Build(pages,settings,c?.OutputFaceDown ?? true,c?.BackRotationDegrees ?? 0);
            job.Batches=ManualDuplexPlanner.StockBatches(plan);
            job.Packets=ManualDuplexPlanner.FrontPass(job.Batches[0]);
            job.TotalFeeds=plan.Sheets.Count+job.Batches.Sum(b=>b.Any(s=>!s.Back.IsBlank) ? b.Count : 0);
        }
        else
        {
            job.Packets=ManualDuplexPlanner.Simplex(pages,settings); job.TotalFeeds=job.Packets.Sum(p=>p.Feeds);
            if(settings.OutputPath!=null)
            {
                if(job.Packets.Skip(1).Any(p=>!ManualDuplexPlanner.SameStock(job.Packets[0].Settings,p.Settings)))
                    throw new InvalidOperationException("PDF разных форматов сохраняйте отдельными заданиями; один файл нельзя перезаписывать несколькими проходами.");
                // Preserve one PDF output and let PrintEngine select each page's
                // DPI/monochrome/threshold inside the same PrintDocument.
                job.Packets=[new(pages.ToArray(),settings)];
            }
        }
        _pending=job; return await ContinueAsync(progress,token);
    }
    public async Task<bool> ContinueAsync(IProgress<PrintProgress>? progress,CancellationToken token)
    {
        var job=_pending ?? throw new InvalidOperationException("Нет ожидающего задания.");
        try
        {
            while(true)
            {
                token.ThrowIfCancellationRequested();
                if(job.WaitingFlip)
                {
                    progress?.Report(new(job.CompletedFeeds,job.TotalFeeds,"Ожидание ручного переворота бумаги.",PrintPhase.WaitingForTurn));
                    var sheets=job.Batches![job.BatchIndex]; var settings=sheets.First(s=>!s.Back.IsBlank).Back.Settings;
                    var model=new PaperGuideViewModel(settings,sheets.Count,PaperGuideStep.Reload,calibration(settings));
                    if(!dialogs.ShowPaperGuide(model) || model.Calibration==null) return false;
                    await rememberGuide(model);
                    job.Packets=ManualDuplexPlanner.BackPass(sheets,model.OutputFaceDown,model.BackRotationDegrees);
                    job.PacketIndex=0; job.WaitingFlip=false; job.BackPass=true;
                }
                for(;job.PacketIndex<job.Packets.Count;job.PacketIndex++)
                {
                    var packet=job.Packets[job.PacketIndex]; var settings=packet.Settings;
                    var isPhysical=settings.OutputPath==null;
                    var stockChange=isPhysical && job.LastStock!=null && !ManualDuplexPlanner.SameStock(job.LastStock,settings);
                    var loadGuide=isPhysical && settings.Paper==PaperPreset.Notebook && !hiddenGuide(settings);
                    if(stockChange || !job.BackPass && loadGuide && job.LastStock==null)
                    {
                        progress?.Report(new(job.CompletedFeeds,job.TotalFeeds,"Ожидание загрузки бумаги в лоток.",PrintPhase.WaitingForPaper));
                        var model=new PaperGuideViewModel(settings,packet.Feeds,stockChange ? PaperGuideStep.ChangeStock : PaperGuideStep.Load,calibration(settings));
                        if(!dialogs.ShowPaperGuide(model) || model.Calibration==null) return false;
                        await rememberGuide(model);
                    }
                    var c=calibration(settings);
                    if(c!=null) settings=settings with { PrintOffsetXmm=c.OffsetXmm,PrintOffsetYmm=c.OffsetYmm };
                    var offset=job.CompletedFeeds; var total=job.TotalFeeds;
                    var wrapped=new ForwardProgress<PrintProgress>(p=>
                    { if(ReferenceEquals(job,_pending) && !token.IsCancellationRequested) progress?.Report(new(offset+p.Completed,total,p.Message,p.Phase)); });
                    // If a native pass fails, exact physical completion is unknown. No automatic retry.
                    await runner.PrintAsync(packet.Pages,settings with { ManualDuplex=false },wrapped,token);
                    job.CompletedFeeds+=packet.Feeds; job.LastStock=settings;
                    progress?.Report(new(job.CompletedFeeds,job.TotalFeeds,$"В очередь Windows передано {job.CompletedFeeds}/{job.TotalFeeds} проходов листа",PrintPhase.Queuing));
                }
                if(job.Batches==null) break;
                if(!job.BackPass && job.Batches[job.BatchIndex].Any(s=>!s.Back.IsBlank))
                { job.WaitingFlip=true; continue; }
                if(++job.BatchIndex>=job.Batches.Count) break;
                job.Packets=ManualDuplexPlanner.FrontPass(job.Batches[job.BatchIndex]); job.PacketIndex=0; job.BackPass=false;
            }
            _pending=null; return true;
        }
        catch { _pending=null; throw; }
    }
}
