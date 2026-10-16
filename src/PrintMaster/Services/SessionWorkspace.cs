using System.IO;

namespace PrintMaster.Services;

public sealed class SessionWorkspace : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(),"PrintMaster",Guid.NewGuid().ToString("N"));
    public SessionWorkspace() => Directory.CreateDirectory(Root);
    public string NewImportDirectory()
    {
        var path=Path.Combine(Root,Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path;
    }
    internal void DiscardFailedImport(string directory)
    {
        // Only a direct, generated child is owned by this import transaction.
        // Never accept a caller's source path or recurse through a replaced junction.
        var full=Path.GetFullPath(directory); var root=Path.GetFullPath(Root);
        if(!string.Equals(Path.GetDirectoryName(full),root,StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(full),"N",out _))
            throw new ArgumentException("Папка не принадлежит временному импорту.",nameof(directory));
        try
        {
            if(Directory.Exists(full) && (File.GetAttributes(root)&FileAttributes.ReparsePoint)==0
                && (File.GetAttributes(full)&FileAttributes.ReparsePoint)==0) Directory.Delete(full,true);
        }
        catch(IOException) { } catch(UnauthorizedAccessException) { }
    }
    public void Dispose()
    {
        // Only this explicitly owned, uniquely named session directory is removed.
        try { Directory.Delete(Root,true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
