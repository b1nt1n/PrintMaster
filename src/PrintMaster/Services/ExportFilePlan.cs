using PrintMaster.Models;
using System.Collections.Immutable;
using System.IO;

namespace PrintMaster.Services;

public static class ExportFilePlan
{
    public static ExportPlan Build(IReadOnlyList<ExportPage> pages,ExportOptions options,string chosenFile)
    {
        options.Validate(); if(pages.Count==0) throw new ArgumentException("Выберите страницы для экспорта.");
        if(string.IsNullOrWhiteSpace(chosenFile)) throw new ArgumentException("Выберите путь для экспорта.");
        var path=Path.GetFullPath(chosenFile); var directory=Path.GetDirectoryName(path)!;
        var extension=options.Format==ExportFormat.Pdf ? ".pdf" : ".png";
        if(!Path.GetExtension(path).Equals(extension,StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Для выбранного формата нужен файл "+extension+".");
        if(!Directory.Exists(directory)) throw new DirectoryNotFoundException("Папка экспорта отсутствует. Выберите существующую папку.");
        var stem=Path.GetFileNameWithoutExtension(path);
        if(string.IsNullOrWhiteSpace(stem) || stem.IndexOfAny(Path.GetInvalidFileNameChars())>=0) throw new ArgumentException("Укажите допустимое имя файла.");
        var count=options.Format==ExportFormat.Pdf ? 1 : pages.Count; var digits=Math.Max(3,count.ToString().Length);
        var paths=Enumerable.Range(1,count).Select(n=>count==1 ? path : Path.Combine(directory,stem+"_"+n.ToString("D"+digits)+extension)).ToArray();
        AssertNoSourceCollision(pages,paths);
        var files=paths.Select(p=>
        {
            if(Directory.Exists(p)) throw new IOException("Выбранное имя занято папкой: "+Path.GetFileName(p));
            var info=new FileInfo(p); return new ExportDestination(p,info.Exists,info.Exists ? info.Length : 0,info.Exists ? info.LastWriteTimeUtc : default);
        }).ToImmutableArray();
        return new(pages.ToImmutableArray(),options,files);
    }
    public static void AssertNoSourceCollision(IEnumerable<ExportPage> pages,IEnumerable<string> paths)
    {
        var selected=pages.ToArray();
        var protectedFiles=selected.SelectMany(p=>p.Page.GetSheet().Assets.SelectMany(a=>new[] { a.OriginalPath,a.WorkingPath }))
            .Concat(selected.SelectMany(p=>new[] {p.Page.OriginalPath,p.Page.WorkingPath}))
            .Where(p=>!string.IsNullOrWhiteSpace(p)).Select(p=>Path.GetFullPath(p!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(paths.Select(Path.GetFullPath).Any(protectedFiles.Contains)) throw new IOException("Экспорт не может заменять исходный или рабочий файл. Выберите другое имя или папку.");
    }
    public static void AssertUnchanged(ExportDestination target)
    {
        var info=new FileInfo(target.Path);
        if(info.Exists!=target.Existed || info.Exists && (info.Length!=target.Length || info.LastWriteTimeUtc!=target.LastWriteUtc))
            throw new IOException("Файл изменился после выбора пути. Повторите экспорт: "+Path.GetFileName(target.Path));
    }
}
