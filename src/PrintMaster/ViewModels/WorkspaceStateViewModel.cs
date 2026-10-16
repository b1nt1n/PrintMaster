using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrintMaster.Models;
using PrintMaster.Services;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;

namespace PrintMaster.ViewModels;

public sealed partial class RecentProjectViewModel(RecentProjectRecord record) : ObservableObject
{
    public RecentProjectRecord Record { get; }=record;
    public string Path=>Record.Path;
    public string Name=>System.IO.Path.GetFileName(Path);
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayName))] private bool? available;
    public string DisplayName=>Name+(Available==false ? " — файл не найден" : "");
}
public partial class PrintSessionViewModel
{
    private IWorkspaceStateStore _environmentStore=new MemoryWorkspaceStateStore();
    private WorkspaceState _environment=new();
    private Task? _environmentInitialization;
    private readonly SemaphoreSlim _environmentSaveGate=new(1,1);
    internal bool IsWorkspaceInitialized { get; private set; }
    public WorkspaceState SavedWorkspace=>_environment;
    public ObservableCollection<RecentProjectViewModel> RecentProjects { get; }=new();
    [ObservableProperty] private string workspaceWarning="";
    public Task InitializeWorkspaceAsync()=>_environmentInitialization ??=LoadWorkspaceAsync();
    private async Task LoadWorkspaceAsync()
    {
        try { _environment=(await _environmentStore.LoadAsync(_lifetime.Token)).Normalize(); }
        catch(OperationCanceledException) { return; }
        catch(Exception ex) { WorkspaceWarning="Рабочее окружение не прочитано: "+ex.Message+". Используются начальные параметры окна."; Status=WorkspaceWarning; }
        SnapToPrintableArea=_environment.SnapToPrintableArea;
        IsWorkspaceInitialized=true; RefreshRecentProjects();
    }
    private void RefreshRecentProjects()
    {
        RecentProjects.Clear();
        foreach(var entry in _environment.RecentProjects) RecentProjects.Add(new(entry));
        ClearRecentProjectsCommand.NotifyCanExecuteChanged();
    }
    public async Task RefreshRecentAvailabilityAsync()
    {
        var entries=RecentProjects.ToArray();
        try
        {
            var result=await Task.Run(()=>entries.Select(e=>{ _lifetime.Token.ThrowIfCancellationRequested(); return File.Exists(e.Path); }).ToArray(),_lifetime.Token);
            if(_disposed) return;
            for(var i=0;i<entries.Length;i++) if(RecentProjects.Contains(entries[i])) entries[i].Available=result[i];
        }
        catch(OperationCanceledException) { }
    }
    private async Task<bool> SaveWorkspaceAsync()
    {
        var entered=false;
        try
        {
            await _environmentSaveGate.WaitAsync(_lifetime.Token); entered=true;
            // Read the newest immutable state after entering the gate, not before it.
            await _environmentStore.SaveAsync(_environment,_lifetime.Token); WorkspaceWarning=""; return true;
        }
        catch(OperationCanceledException) { return false; }
        catch(Exception ex) { WorkspaceWarning="Не удалось сохранить рабочее окружение: "+ex.Message; Status=WorkspaceWarning; return false; }
        finally { if(entered) _environmentSaveGate.Release(); }
    }
    public async Task<bool> SaveWorkspaceEnvironmentAsync(SavedWindowPlacement? window,WorkspacePanels panels)
    {
        await InitializeWorkspaceAsync();
        _environment=(_environment with { Window=window,Panels=panels }).Normalize();
        return await SaveWorkspaceAsync();
    }
    internal void ShowWorkspaceSaveError()=>_dialogs.Error(WorkspaceWarning+"\nПроект и рабочая сессия сохранены отдельно. Окно будет закрыто.");
    private async Task RememberRecentProjectAsync(string path,string? replacedPath=null)
    {
        await InitializeWorkspaceAsync(); _environment=_environment.Remember(path,replacedPath); RefreshRecentProjects(); await SaveWorkspaceAsync();
    }
    private bool CanClearRecentProjects()=>IsIdle && RecentProjects.Count>0;
    [RelayCommand(CanExecute=nameof(CanClearRecentProjects))]
    private async Task ClearRecentProjectsAsync()
    {
        _environment=_environment with { RecentProjects=[] }; RefreshRecentProjects();
        if(await SaveWorkspaceAsync()) Status="Список недавних проектов очищен. Файлы проектов сохранены.";
    }
    [RelayCommand(CanExecute=nameof(IsIdle))]
    private async Task RemoveRecentProjectAsync(string? path)
    {
        if(path==null) return;
        _environment=_environment with { RecentProjects=_environment.RecentProjects.Where(p=>!string.Equals(p.Path,path,StringComparison.OrdinalIgnoreCase)).ToImmutableArray() };
        RefreshRecentProjects(); await SaveWorkspaceAsync();
    }
    [RelayCommand(CanExecute=nameof(IsIdle))]
    private async Task OpenRecentProjectAsync(RecentProjectViewModel? entry)
    {
        if(entry==null || !IsIdle || !CommitInputForAction()) return;
        bool exists;
        try { exists=await Task.Run(()=>File.Exists(entry.Path),_lifetime.Token); }
        catch(OperationCanceledException) { return; }
        if(!IsIdle || _disposed) return;
        if(exists) { await OpenProjectFromAsync(entry.Path); return; }
        entry.Available=false;
        var choice=_dialogs.ResolveMissingRecentProject(entry.Path);
        if(choice==MissingRecentProjectChoice.Remove) { await RemoveRecentProjectAsync(entry.Path); return; }
        if(choice!=MissingRecentProjectChoice.Locate) return;
        var replacement=_dialogs.PickProjectToOpen();
        if(replacement!=null && await OpenProjectFromAsync(replacement)) await RemoveRecentProjectAsync(entry.Path);
    }
}
