using System.Diagnostics;
using System.IO;

namespace PrintMaster.Services;

public sealed class DocxConverter
{
    public string? ExecutablePath { get; set; }
    public static string? FindExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("PRINTMASTER_LIBREOFFICE");
        var candidates = new List<string?> { configured };
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i=0;i<12 && directory!=null;i++,directory=directory.Parent)
        {
            candidates.Add(Path.Combine(directory.FullName,"tools","LibreOffice","program","soffice.com"));
            candidates.Add(Path.Combine(directory.FullName,"tools","LibreOffice","LibreOffice","program","soffice.com"));
        }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"LibreOffice","program","soffice.com"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"LibreOffice","program","soffice.com"));
        return candidates.FirstOrDefault(x=>!string.IsNullOrWhiteSpace(x) && File.Exists(x));
    }
    public async Task<string> ConvertAsync(string docxPath,string outputDirectory,CancellationToken cancellationToken)
    {
        var executable = ExecutablePath ?? FindExecutable();
        if (executable == null || !File.Exists(executable))
            throw new InvalidOperationException("Для DOCX нужен LibreOffice. Выполните scripts/Setup-LibreOffice.ps1 или выберите soffice.com в настройках.");
        var profile=Path.Combine(outputDirectory,"lo-profile"); Directory.CreateDirectory(profile);
        var userProfile=Path.Combine(profile,"user"); Directory.CreateDirectory(userProfile);
        // Suppress macros and updates of remote links in the isolated conversion profile.
        await File.WriteAllTextAsync(Path.Combine(userProfile,"registrymodifications.xcu"),
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><oor:items xmlns:oor=\"http://openoffice.org/2001/registry\"><item oor:path=\"/org.openoffice.Office.Common/Security/Scripting\"><prop oor:name=\"MacroSecurityLevel\" oor:op=\"fuse\"><value>3</value></prop></item><item oor:path=\"/org.openoffice.Office.Writer/Content/Update\"><prop oor:name=\"Link\" oor:op=\"fuse\"><value>2</value></prop></item></oor:items>",cancellationToken);
        var info=new ProcessStartInfo(executable) { UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,
            RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=outputDirectory };
        foreach(var argument in new[] { "-env:UserInstallation="+new Uri(profile+Path.DirectorySeparatorChar).AbsoluteUri,
            "--headless","--nologo","--nodefault","--norestore","--convert-to","pdf:writer_pdf_Export","--outdir",outputDirectory,docxPath })
            info.ArgumentList.Add(argument);
        using var process=Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить LibreOffice.");
        var stdout=process.StandardOutput.ReadToEndAsync(); var stderr=process.StandardError.ReadToEndAsync();
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch(OperationCanceledException)
        {
            try { process.Kill(true); await process.WaitForExitAsync().ConfigureAwait(false); } catch(InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("LibreOffice не завершил конвертацию за 2 минуты. Проверьте документ и шрифты.");
        }
        var output=await stdout.ConfigureAwait(false); var error=await stderr.ConfigureAwait(false);
        var pdf=Path.Combine(outputDirectory,Path.GetFileNameWithoutExtension(docxPath)+".pdf");
        if (process.ExitCode!=0 || !File.Exists(pdf) || new FileInfo(pdf).Length<10)
            throw new InvalidOperationException("Не удалось преобразовать DOCX в PDF. "+(error+" "+output).Trim());
        return pdf;
    }
}
