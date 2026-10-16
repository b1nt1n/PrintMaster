using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PrintMaster.Models;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;

namespace PrintMaster.Services;

public interface IPageExportService
{
    Task<ExportResult> ExportAsync(ExportPlan plan,bool overwriteConfirmed,IProgress<ExportProgress>? progress,CancellationToken token);
}

public sealed class PageExportService(SheetExportRenderer renderer) : IPageExportService
{
    private readonly SemaphoreSlim _gate=new(1,1);
    public async Task<ExportResult> ExportAsync(ExportPlan plan,bool overwriteConfirmed,IProgress<ExportProgress>? progress,CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { return await Task.Run(()=>Run(plan,overwriteConfirmed,progress,token),token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    private ExportResult Run(ExportPlan plan,bool overwriteConfirmed,IProgress<ExportProgress>? progress,CancellationToken token)
    {
        SheetExportRenderer.ValidatePages(plan.Pages,plan.Options);
        ExportFilePlan.AssertNoSourceCollision(plan.Pages,plan.Files.Select(f=>f.Path));
        if(plan.Files.Length!=(plan.Options.Format==ExportFormat.Pdf ? 1 : plan.Pages.Length)) throw new ArgumentException("Неверное число выходных файлов.");
        if(plan.ExistingFiles.Count>0 && !overwriteConfirmed) throw new IOException("Замена существующих файлов не подтверждена.");
        foreach(var file in plan.Files) ExportFilePlan.AssertUnchanged(file);
        var directory=Path.GetDirectoryName(plan.Files[0].Path)!;
        if(plan.Files.Any(f=>!string.Equals(directory,Path.GetDirectoryName(f.Path),StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Пакет должен сохраняться в одной папке.");
        var stage=Path.Combine(directory,".printmaster-export-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage); var outputs=new List<string>();
        try
        {
            var pdfPages=new List<string>();
            for(var index=0;index<plan.Pages.Length;index++)
            {
                token.ThrowIfCancellationRequested(); var item=plan.Pages[index];
                progress?.Report(new(index,plan.Pages.Length,$"Подготовка листа {index+1}/{plan.Pages.Length}…",ExportPhase.Rendering));
                var png=Path.Combine(stage,$"page-{index:D6}.png");
                using(var bitmap=renderer.Render(item,plan.Options,token)) bitmap.Save(png,ImageFormat.Png);
                token.ThrowIfCancellationRequested();
                if(plan.Options.Format==ExportFormat.Pdf)
                {
                    var pdfPath=Path.Combine(stage,$"page-{index:D6}.pdf");
                    // A separate document releases decoded image data before the next 600-DPI page.
                    using(var single=new PdfDocument())
                    {
                        var page=single.AddPage(); var size=item.Paper.OrientedSizeMm;
                        page.Width=XUnit.FromMillimeter(size.Width); page.Height=XUnit.FromMillimeter(size.Height);
                        using(var image=XImage.FromFile(png))
                        using(var graphics=XGraphics.FromPdfPage(page)) graphics.DrawImage(image,0,0,page.Width.Point,page.Height.Point);
                        single.Save(pdfPath);
                    }
                    pdfPages.Add(pdfPath); File.Delete(png);
                }
                else outputs.Add(png);
                progress?.Report(new(index+1,plan.Pages.Length,$"Подготовлено {index+1}/{plan.Pages.Length} листов",ExportPhase.Rendering));
            }
            if(plan.Options.Format==ExportFormat.Pdf)
            {
                progress?.Report(new(plan.Pages.Length,plan.Pages.Length,"Сборка PDF из подготовленных листов…",ExportPhase.AssemblingPdf));
                var combinedPath=Path.Combine(stage,"combined.pdf");
                using(var combined=new PdfDocument())
                {
                    combined.Info.Title="PrintMaster — экспорт листов"; combined.Info.Creator="PrintMaster";
                    foreach(var path in pdfPages)
                    {
                        token.ThrowIfCancellationRequested();
                        // Import compressed PDF resources; do not decode every image again.
                        using var imported=PdfReader.Open(path,PdfDocumentOpenMode.Import);
                        combined.AddPage(imported.Pages[0]);
                    }
                    token.ThrowIfCancellationRequested(); combined.Save(combinedPath);
                }
                outputs.Add(combinedPath);
            }
            token.ThrowIfCancellationRequested();
            progress?.Report(new(plan.Pages.Length,plan.Pages.Length,"Сохранение выходных файлов…",ExportPhase.CommittingFiles));
            Commit(plan.Files,outputs,stage,token);
            return new(plan.Files.Select(f=>f.Path).ToArray(),plan.Pages.Length);
        }
        finally
        {
            // Only the generated private folder is eligible for cleanup.
            // If rollback cannot restore an existing output, its backup is deliberately retained.
            var resolvedStage=Path.GetFullPath(stage); var resolvedParent=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            try
            {
                if(Directory.Exists(stage) && resolvedStage.StartsWith(resolvedParent,StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(resolvedStage).StartsWith(".printmaster-export-",StringComparison.Ordinal)
                    && (File.GetAttributes(stage)&FileAttributes.ReparsePoint)==0 && !File.Exists(Path.Combine(stage,"KEEP-BACKUPS.txt"))) Directory.Delete(stage,true);
            }
            catch(IOException) { } catch(UnauthorizedAccessException) { }
        }
    }
    private static void Commit(IReadOnlyList<ExportDestination> files,IReadOnlyList<string> staged,string stage,CancellationToken token)
    {
        var backups=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var originalHashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var committed=new List<string>();
        try
        {
            for(var index=0;index<files.Count;index++)
            {
                token.ThrowIfCancellationRequested(); var target=files[index]; ExportFilePlan.AssertUnchanged(target);
                if(!target.Existed) continue;
                var backup=Path.Combine(stage,$"backup-{index:D6}"); File.Copy(target.Path,backup,false);
                // The generated backup must be movable/removable even when its source is read-only.
                File.SetAttributes(backup,File.GetAttributes(backup)&~FileAttributes.ReadOnly);
                backups.Add(target.Path,backup); originalHashes.Add(target.Path,Hash(backup));
            }
            token.ThrowIfCancellationRequested();
            for(var index=0;index<files.Count;index++)
            {
                var target=files[index]; ExportFilePlan.AssertUnchanged(target);
                if(target.Existed && Hash(target.Path)!=originalHashes[target.Path]) throw new IOException("Файл изменился перед заменой: "+Path.GetFileName(target.Path));
                File.Move(staged[index],target.Path,target.Existed); committed.Add(target.Path);
            }
        }
        catch(Exception failure)
        {
            var errors=new List<Exception>();
            foreach(var path in committed.AsEnumerable().Reverse())
            {
                try { if(backups.TryGetValue(path,out var backup)) File.Move(backup,path,true); else File.Delete(path); }
                catch(Exception restoreError) when(restoreError is IOException or UnauthorizedAccessException) { errors.Add(restoreError); }
            }
            if(errors.Count==0) throw;
            File.WriteAllText(Path.Combine(stage,"KEEP-BACKUPS.txt"),"Не удалось восстановить все выходы. Сохранены резервные копии.\n"+failure+"\n"+string.Join("\n",errors));
            throw new IOException("Не удалось завершить экспорт и восстановить все файлы. Резервные копии: "+stage,failure);
        }
    }
    private static string Hash(string path)
    { using var input=File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
}
