using PrintMaster.Models;
using PrintMaster.Services;

namespace PrintMaster.ViewModels;

public partial class PrintSessionViewModel
{
    private sealed record InsertionTarget(PrintPageItem Page,SheetPaper Paper,RectD Safe,RectD PrimaryBounds);
    private InsertionTarget CaptureInsertionTarget(PrintPageItem page)
    {
        var settings=EffectiveSettings(page);
        var paper=page.IsSheetLayout ? page.Document.Paper : new SheetPaper(settings.PaperSizeMm,settings.Landscape,SheetMargins.SafeDefault){Notebook=settings.Notebook};
        var safe=page==SelectedPage ? SafeAreaMm : paper.SafeAreaMm;
        return new(page,paper,safe,PageLayout.Place(page.Snapshot().ContentSize,safe,settings.Scaling));
    }
    internal static RectD ImageInsertionArea(RectD safe,PointD? anchor=null)
    {
        var width=safe.Width*0.8; var height=safe.Height*0.6;
        var x=anchor is {} p ? p.X-width/2 : safe.X+Math.Min(5,safe.Width/10);
        var y=anchor is {} q ? q.Y-height/2 : safe.Y+Math.Min(5,safe.Height/10);
        return new(Math.Clamp(x,safe.X,safe.Right-width),Math.Clamp(y,safe.Y,safe.Bottom-height),width,height);
    }
    private void InsertObject(InsertionTarget target,SheetObject item,SourceAsset? asset=null)
    {
        target.Page.Document.InsertIntoLayout(item,asset,target.Paper,target.PrimaryBounds);
        if(SelectedPage!=target.Page) return;
        ShowPrintPreview=false; SelectedSourceAnnotation=null; Tool=EditorTool.Select; SelectedSheetObjectId=item.Id;
        NotifySheetMode();
    }
    internal PasteDestination FileDropDestination(IEnumerable<string> paths,bool onCanvas)
        =>onCanvas && CanInsertOnCurrentPage && paths.All(IsImageFile) ? PasteDestination.Auto : PasteDestination.Queue;
    internal static bool IsImageFile(string path)=>System.IO.Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp";
    internal async Task HandleFileDropAsync(IEnumerable<string> paths,bool onCanvas,PointD? anchor=null)
    {
        var files=paths.ToArray(); if(files.Length==0 || !IsIdle) return;
        if(FileDropDestination(files,onCanvas)==PasteDestination.Auto)
        {
            ShowPrintPreview=false; await InsertSheetImagesFromAsync(files,anchor);
        }
        else
        {
            await ImportFilesAsync(files);
        }
    }
}
