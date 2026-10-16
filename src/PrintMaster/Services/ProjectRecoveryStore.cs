using PrintMaster.Models;
using System.IO;

namespace PrintMaster.Services;

public sealed class ProjectRecoveryStore : IDisposable
{
    private readonly ProjectFileService _files;
    private readonly FileStream _ownerLock;
    public string DirectoryPath { get; }
    public string RecoveryPath { get; }
    private readonly string _lockPath;

    public ProjectRecoveryStore(ProjectFileService files,string? directory=null)
    {
        _files=files; DirectoryPath=directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PrintMaster","Recovery");
        Directory.CreateDirectory(DirectoryPath); var id=Guid.NewGuid().ToString("N");
        RecoveryPath=Path.Combine(DirectoryPath,"recovery-"+id+ProjectFileService.Extension); _lockPath=RecoveryPath+".lock";
        _ownerLock=new FileStream(_lockPath,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None);
    }
    public Task WriteAsync(ProjectSession snapshot,CancellationToken token)=>_files.SaveAsync(snapshot,RecoveryPath,true,token);
    public string? FindLatest()
    {
        foreach(var path in Directory.EnumerateFiles(DirectoryPath,"recovery-*"+ProjectFileService.Extension).OrderByDescending(File.GetLastWriteTimeUtc))
        {
            if(path==RecoveryPath) continue;
            try { using var availability=new FileStream(path+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); return path; }
            catch(IOException) { }
        }
        return null;
    }
    public void Dismiss(string path)
    {
        var full=Path.GetFullPath(path); var root=Path.GetFullPath(DirectoryPath)+Path.DirectorySeparatorChar;
        if(!full.StartsWith(root,StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("recovery-",StringComparison.Ordinal))
            throw new ArgumentException("Файл не принадлежит хранилищу восстановления.");
        if(File.Exists(full)) File.Move(full,full+".dismissed",true);
    }
    public void Dispose()
    {
        _ownerLock.Dispose(); try { File.Delete(_lockPath); } catch(IOException) { }
    }
}
