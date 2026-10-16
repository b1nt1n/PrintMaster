using PrintMaster.Models;
using System.IO;
using System.Text.Json;

namespace PrintMaster.Services;

public interface IWorkspaceStateStore
{
    Task<WorkspaceState> LoadAsync(CancellationToken token);
    Task SaveAsync(WorkspaceState state,CancellationToken token);
}
public sealed class WorkspaceStateStore(string? path=null) : IWorkspaceStateStore
{
    public string Path { get; }=path ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PrintMaster","workspace.json");
    private static readonly JsonSerializerOptions Options=new() { WriteIndented=true };
    public async Task<WorkspaceState> LoadAsync(CancellationToken token)
    {
        if(!File.Exists(Path)) return new();
        await using var stream=File.OpenRead(Path);
        var state=await JsonSerializer.DeserializeAsync<WorkspaceState>(stream,Options,token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Файл рабочего окружения пуст.");
        return state.Normalize();
    }
    public async Task SaveAsync(WorkspaceState state,CancellationToken token)
    {
        state=state.Normalize(); var full=System.IO.Path.GetFullPath(Path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        var temporary=full+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            await using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,true))
            { await JsonSerializer.SerializeAsync(stream,state,Options,token).ConfigureAwait(false); await stream.FlushAsync(token).ConfigureAwait(false); }
            token.ThrowIfCancellationRequested(); File.Move(temporary,full,true);
        }
        finally { if(File.Exists(temporary)) File.Delete(temporary); }
    }
}
internal sealed class MemoryWorkspaceStateStore : IWorkspaceStateStore
{
    private WorkspaceState _state=new();
    public Task<WorkspaceState> LoadAsync(CancellationToken token)=>Task.FromResult(_state);
    public Task SaveAsync(WorkspaceState state,CancellationToken token) { token.ThrowIfCancellationRequested(); _state=state.Normalize(); return Task.CompletedTask; }
}
