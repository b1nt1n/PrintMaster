using PrintMaster.Models;
using System.IO;
using System.Text.Json;

namespace PrintMaster.Services;

public interface IApplicationPreferencesStore
{
    Task<ApplicationPreferences> LoadAsync(CancellationToken token);
    Task SaveAsync(ApplicationPreferences preferences,CancellationToken token);
}

public sealed class ApplicationPreferencesStore(string? path=null) : IApplicationPreferencesStore
{
    public string Path { get; }=path ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PrintMaster","preferences.json");
    private static readonly JsonSerializerOptions JsonOptions=new() { WriteIndented=true };

    public async Task<ApplicationPreferences> LoadAsync(CancellationToken token)
    {
        if(!File.Exists(Path)) return new();
        await using var stream=File.OpenRead(Path);
        var value=await JsonSerializer.DeserializeAsync<ApplicationPreferences>(stream,JsonOptions,token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Файл настроек пуст.");
        // A removed LibreOffice installation must not discard unrelated preferences.
        var checkedValue=value with { LibreOfficePath=null }; checkedValue.Validate();
        // Removed converters cannot remain as invalid state: saving an unrelated
        // paper profile or printer preference validates this value again.
        return !string.IsNullOrWhiteSpace(value.LibreOfficePath) && !File.Exists(value.LibreOfficePath)
            ? checkedValue : value;
    }

    public async Task SaveAsync(ApplicationPreferences preferences,CancellationToken token)
    {
        preferences.Validate();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var temporary=Path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            await using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,true))
            { await JsonSerializer.SerializeAsync(stream,preferences,JsonOptions,token).ConfigureAwait(false); await stream.FlushAsync(token).ConfigureAwait(false); }
            token.ThrowIfCancellationRequested(); File.Move(temporary,Path,true);
        }
        finally { if(File.Exists(temporary)) File.Delete(temporary); }
    }
}

// Explicitly used by model tests; the application composition root supplies the disk store.
internal sealed class MemoryApplicationPreferencesStore : IApplicationPreferencesStore
{
    private ApplicationPreferences _value=new();
    public Task<ApplicationPreferences> LoadAsync(CancellationToken token)=>Task.FromResult(_value);
    public Task SaveAsync(ApplicationPreferences preferences,CancellationToken token)
    { token.ThrowIfCancellationRequested(); preferences.Validate(); _value=preferences; return Task.CompletedTask; }
}
