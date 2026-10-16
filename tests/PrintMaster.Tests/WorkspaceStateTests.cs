using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.Collections.Immutable;
using System.IO;
using Xunit;

namespace PrintMaster.Tests;

public sealed class WorkspaceStateTests
{
    internal static string StatePath()=>Path.Combine(Fixtures.Root,"ux013-"+Guid.NewGuid().ToString("N")+".json");
    [Fact]
    public async Task Store_RoundTripAndCancelledReplacement_PreservesPreviousFile()
    {
        var path=StatePath(); var store=new WorkspaceStateStore(path);
        var state=new WorkspaceState { Window=new(-1400,80,-300,800,144,true),Panels=new(312,334,false,true,WorkspacePanel.Inspector,true),
            RecentProjects=[new(ProjectFileTests.PathFor("recent"),DateTimeOffset.UtcNow)] };
        await store.SaveAsync(state,default); var bytes=await File.ReadAllBytesAsync(path); var restored=await store.LoadAsync(default);
        Assert.Equal(state.Window,restored.Window); Assert.Equal(state.Panels,restored.Panels); Assert.Equal(state.RecentProjects.ToArray(),restored.RecentProjects.ToArray());
        using var cancel=new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>store.SaveAsync(state with { Panels=new() },cancel.Token));
        Assert.Equal(bytes,await File.ReadAllBytesAsync(path)); Assert.Empty(Directory.GetFiles(Fixtures.Root,Path.GetFileName(path)+".*.tmp"));
        Assert.DoesNotContain("Threshold",await File.ReadAllTextAsync(path));
    }
    [Fact]
    public void RecentProjects_CanonicalCaseInsensitivePathsMoveToFrontAndKeepTen()
    {
        var paths=Enumerable.Range(0,12).Select(i=>ProjectFileTests.PathFor("recent-"+i)).ToArray(); var state=new WorkspaceState();
        foreach(var path in paths) state=state.Remember(path);
        Assert.Equal(paths.Reverse().Take(10),state.RecentProjects.Select(p=>p.Path));
        state=state.Remember(paths[4].ToUpperInvariant());
        Assert.Equal(paths[4].ToUpperInvariant(),state.RecentProjects[0].Path); Assert.Equal(10,state.RecentProjects.Length);
        Assert.Single(state.RecentProjects.Where(p=>p.Path.Equals(paths[4],StringComparison.OrdinalIgnoreCase)));
    }
    [Fact]
    public void Normalize_BadWindowAndIndividualRecentEntries_DoNotLoseValidPanelsOrProjects()
    {
        var valid=ProjectFileTests.PathFor("valid");
        var state=new WorkspaceState { Window=new(0,0,-1,0),Panels=new(309,Double.NaN,false,true),RecentProjects=
            [new("relative.printmaster",default),new(valid,default),new(valid.ToUpperInvariant(),default),new(valid+".png",default)] }.Normalize();
        Assert.Null(state.Window); Assert.Equal(309,state.Panels.QueueWidth); Assert.Equal(290,state.Panels.InspectorWidth);
        Assert.False(state.Panels.QueueVisible); Assert.Equal(valid,Assert.Single(state.RecentProjects).Path);
    }
    [Theory]
    [InlineData(1600,true,true)] [InlineData(900,false,true)] [InlineData(750,false,false)]
    public void Panels_AdaptiveCollapse_PreservesUserIntent(double width,bool queue,bool inspector)
    {
        var panels=new WorkspacePanels(320,340,true,true,WorkspacePanel.Inspector);
        Assert.Equal((queue,inspector),panels.VisibleAt(width));
        Assert.Equal((true,true),panels.VisibleAt(1600)); Assert.Equal(320,panels.QueueWidth); Assert.Equal(340,panels.InspectorWidth);
        Assert.Equal((false,true),(panels with { ExpandInCompact=true }).VisibleAt(750));
    }
    [Theory] [InlineData(96,1000,700)] [InlineData(120,1250,875)] [InlineData(144,1500,1050)]
    public void WindowSize_ChangedDpi_PreservesLogicalDimensions(int dpi,int width,int height)
    {
        Assert.Equal((width,height),WindowEnvironment.ScaleSize(new(-1000,60,0,760,96),(uint)dpi));
    }
    [Fact]
    public async Task EnvironmentChange_NeverEditsProjectHistoryOrDefaults()
    {
        var environment=new WorkspaceStateStore(StatePath()); var preferences=new Preferences(new() { AutosaveEnabled=false,Dpi=300,Threshold=118 });
        using var vm=Session(new Dialogs(),environment,preferences); await vm.InitializeAsync();
        var page=PrintPageItem.CreateBlank(new(new(148,210),true,SheetMargins.SafeDefault)); vm.AddPage(page);
        page.Document.AddObject(new TextObject(Guid.NewGuid(),new(new(15,20,80,20)),"Сохранить макет",new()));
        vm.SelectedPage=page; Assert.True(await vm.SaveProjectToAsync(ProjectFileTests.PathFor("unchanged")));
        var before=vm.CaptureProject(); var dirty=vm.HasUnsavedChanges; var undo=vm.UndoDescription;
        Assert.True(await vm.SaveWorkspaceEnvironmentAsync(new(60,60,1060,760),new(315,328,false,true)));
        Assert.Equal(before.Print,vm.CaptureProject().Print); Assert.Equal(before.Pages.ToArray(),vm.CaptureProject().Pages.ToArray());
        Assert.Equal(dirty,vm.HasUnsavedChanges); Assert.Equal(undo,vm.UndoDescription); Assert.Equal(0,preferences.Saves);
        Assert.Equal(118,preferences.Value.Threshold); Assert.Single(vm.RecentProjects);
    }
    [Fact]
    public async Task ReopenedSession_RecentProjectTakesPriorityOverDefaults_ButNotEnvironment()
    {
        var statePath=StatePath(); var project=ProjectFileTests.PathFor("project-priority");
        var preferences=new Preferences(new() { AutosaveEnabled=false,Dpi=300,Threshold=110 });
        using(var first=Session(new Dialogs(),new WorkspaceStateStore(statePath),preferences))
        {
            await first.InitializeAsync(); first.Settings.Dpi=600; first.Settings.Threshold=210;
            first.AddPage(PrintPageItem.CreateBlank(new(new(334,208),false,SheetMargins.SafeDefault)));
            Assert.True(await first.SaveProjectToAsync(project)); await first.SaveWorkspaceEnvironmentAsync(new(80,90,1080,790),new(317,339,false,true));
        }
        using var next=Session(new Dialogs(),new WorkspaceStateStore(statePath),preferences); await next.InitializeAsync();
        Assert.Equal(300,next.Settings.Dpi); Assert.Empty(next.Pages); // MRU never auto-opens a project.
        var environment=next.SavedWorkspace;
        await next.OpenRecentProjectCommand.ExecuteAsync(Assert.Single(next.RecentProjects));
        Assert.Equal(600,next.Settings.Dpi); Assert.Equal(210,next.Settings.Threshold);
        Assert.Equal(new SizeD(334,208),Assert.Single(next.Pages).Document.Paper.OrientedSizeMm);
        Assert.Equal(environment.Window,next.SavedWorkspace.Window); Assert.Equal(environment.Panels,next.SavedWorkspace.Panels);
        Assert.Equal(300,preferences.Value.Dpi); Assert.Equal(0,preferences.Saves);
    }
    [Theory] [InlineData(MissingRecentProjectChoice.Cancel)] [InlineData(MissingRecentProjectChoice.Remove)]
    public async Task MissingRecentProject_CancelOrRemove_LeavesCurrentContentIntact(MissingRecentProjectChoice choice)
    {
        var absent=ProjectFileTests.PathFor("absent"); var store=new WorkspaceStateStore(StatePath()); await store.SaveAsync(new WorkspaceState().Remember(absent),default);
        var dialogs=new Dialogs { Missing=choice }; using var vm=Session(dialogs,store); await vm.InitializeAsync();
        var current=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(current); vm.SelectedPage=current; var before=vm.CaptureProject();
        await vm.OpenRecentProjectCommand.ExecuteAsync(Assert.Single(vm.RecentProjects));
        Assert.Equal(before.Pages.ToArray(),vm.CaptureProject().Pages.ToArray()); Assert.Null(vm.CurrentProjectPath);
        Assert.Equal(choice==MissingRecentProjectChoice.Remove ? 0 : 1,vm.RecentProjects.Count); Assert.Empty(dialogs.Errors);
        Assert.Equal(choice==MissingRecentProjectChoice.Remove ? 0 : 1,(await store.LoadAsync(default)).RecentProjects.Length);
    }
    [Fact]
    public async Task MovedRecentProject_SuccessfulLocate_ReplacesOldEntry()
    {
        var old=ProjectFileTests.PathFor("old"); var moved=ProjectFileTests.PathFor("moved"); await CreateProjectAsync(moved,"Перемещённый проект");
        var store=new WorkspaceStateStore(StatePath()); await store.SaveAsync(new WorkspaceState().Remember(old),default);
        var dialogs=new Dialogs { Missing=MissingRecentProjectChoice.Locate,OpenPath=moved }; using var vm=Session(dialogs,store); await vm.InitializeAsync();
        await vm.OpenRecentProjectCommand.ExecuteAsync(Assert.Single(vm.RecentProjects));
        Assert.Equal(moved,vm.CurrentProjectPath); Assert.Equal(moved,Assert.Single(vm.RecentProjects).Path);
        Assert.Equal("Перемещённый проект",Assert.IsType<TextObject>(Assert.Single(vm.Pages).Document.Objects[0]).Text);
        Assert.Equal(moved,Assert.Single((await store.LoadAsync(default)).RecentProjects).Path);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task FailedOrCancelledReplacement_LeavesQueueAndRecentOrder(bool corrupt)
    {
        var path=ProjectFileTests.PathFor("replace"); if(corrupt) await File.WriteAllTextAsync(path,"not a project"); else await CreateProjectAsync(path,"Другой проект");
        var store=new WorkspaceStateStore(StatePath()); await store.SaveAsync(new WorkspaceState().Remember(path),default);
        var dialogs=new Dialogs { Unsaved=corrupt ? UnsavedProjectChoice.Discard : UnsavedProjectChoice.Cancel };
        using var vm=Session(dialogs,store); await vm.InitializeAsync(); vm.AddPage(PrintPageItem.CreateBlank(SheetPaper.A4));
        var before=vm.CaptureProject(); var recent=vm.SavedWorkspace.RecentProjects;
        await vm.OpenRecentProjectCommand.ExecuteAsync(Assert.Single(vm.RecentProjects));
        Assert.Equal(before.Pages.ToArray(),vm.CaptureProject().Pages.ToArray()); Assert.Null(vm.CurrentProjectPath);
        Assert.Equal(recent.ToArray(),vm.SavedWorkspace.RecentProjects.ToArray()); Assert.Equal(corrupt ? 1 : 0,dialogs.Errors.Count);
    }
    [Fact]
    public async Task ClearRecentProjects_DoesNotDeleteFilesOrModifyCurrentProject()
    {
        var path=ProjectFileTests.PathFor("clear"); await CreateProjectAsync(path,"Не удалять"); var bytes=await File.ReadAllBytesAsync(path);
        using var vm=Session(new Dialogs(),new WorkspaceStateStore(StatePath())); await vm.InitializeAsync(); Assert.True(await vm.OpenProjectFromAsync(path));
        var before=vm.CaptureProject(); await vm.ClearRecentProjectsCommand.ExecuteAsync(null);
        Assert.Empty(vm.RecentProjects); Assert.Equal(before.Pages.ToArray(),vm.CaptureProject().Pages.ToArray()); Assert.Equal(path,vm.CurrentProjectPath);
        Assert.Equal(bytes,await File.ReadAllBytesAsync(path)); Assert.False(vm.HasUnsavedChanges);
    }
    [Fact]
    public async Task CorruptEnvironment_DoesNotDiscardPrinterDefaults_AndCanBeSavedAgain()
    {
        var path=StatePath(); await File.WriteAllTextAsync(path,"{bad json");
        using var vm=Session(new Dialogs(),new WorkspaceStateStore(path),new Preferences(new() { AutosaveEnabled=false,Dpi=300,Threshold=119 }));
        await vm.InitializeAsync(); Assert.NotEmpty(vm.WorkspaceWarning); Assert.Equal(300,vm.Settings.Dpi); Assert.Equal(119,vm.Settings.Threshold);
        Assert.True(await vm.SaveWorkspaceEnvironmentAsync(null,new(306,333))); Assert.Empty(vm.WorkspaceWarning);
        Assert.Equal(306,(await new WorkspaceStateStore(path).LoadAsync(default)).Panels.QueueWidth);
    }
    [Fact]
    public async Task WorkspaceWriteFailure_DoesNotTurnSuccessfulProjectSaveIntoFailure()
    {
        var dialogs=new Dialogs(); using var vm=Session(dialogs,new BrokenStore()); await vm.InitializeAsync(); vm.AddPage(PrintPageItem.CreateBlank(SheetPaper.A4));
        var path=ProjectFileTests.PathFor("write-failure"); Assert.True(await vm.SaveProjectToAsync(path));
        Assert.True(File.Exists(path)); Assert.False(vm.HasUnsavedChanges); Assert.Equal(path,vm.CurrentProjectPath);
        Assert.Contains("рабочее окружение",vm.WorkspaceWarning); Assert.Empty(dialogs.Errors);
    }
    [Fact]
    public async Task RecoveryAndFailedSave_AreNeverAddedToRecentProjects()
    {
        var store=new WorkspaceStateStore(StatePath()); using var vm=Session(new Dialogs(),store); await vm.InitializeAsync();
        var recovery=ProjectFileTests.PathFor("service-recovery"); await CreateProjectAsync(recovery,"Восстановление");
        Assert.True(await vm.OpenProjectFromAsync(recovery,true)); Assert.Empty(vm.RecentProjects); Assert.Null(vm.CurrentProjectPath);
        var invalid=Path.Combine(Fixtures.Root,"ux013-target-directory-"+Guid.NewGuid().ToString("N")+".printmaster"); Directory.CreateDirectory(invalid);
        Assert.False(await vm.SaveProjectToAsync(invalid)); Assert.Empty(vm.RecentProjects); Assert.True(vm.HasUnsavedChanges);
    }
    [Fact]
    public async Task RecentProject_InvalidFocusedInput_PreventsReplacement()
    {
        var path=ProjectFileTests.PathFor("input-block"); await CreateProjectAsync(path,"Не открывать при ошибке");
        var store=new WorkspaceStateStore(StatePath()); await store.SaveAsync(new WorkspaceState().Remember(path),default);
        using var vm=Session(new Dialogs(),store); await vm.InitializeAsync(); var page=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(page);
        vm.CommitInputs=()=>false; await vm.OpenRecentProjectCommand.ExecuteAsync(Assert.Single(vm.RecentProjects));
        Assert.Same(page,Assert.Single(vm.Pages)); Assert.Null(vm.CurrentProjectPath); Assert.Single(vm.RecentProjects);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task LocateRecentProject_CancelledPickerOrBadFile_PreservesMissingEntry(bool corrupt)
    {
        var absent=ProjectFileTests.PathFor("locate-absent"); var bad=ProjectFileTests.PathFor("locate-bad"); await File.WriteAllTextAsync(bad,"invalid archive");
        var store=new WorkspaceStateStore(StatePath()); await store.SaveAsync(new WorkspaceState().Remember(absent),default);
        var dialogs=new Dialogs { Missing=MissingRecentProjectChoice.Locate,OpenPath=corrupt ? bad : null };
        using var vm=Session(dialogs,store); await vm.InitializeAsync(); var page=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(page);
        await vm.OpenRecentProjectCommand.ExecuteAsync(Assert.Single(vm.RecentProjects));
        Assert.Same(page,Assert.Single(vm.Pages)); Assert.Null(vm.CurrentProjectPath); Assert.Equal(absent,Assert.Single(vm.RecentProjects).Path);
        Assert.Equal(corrupt ? 1 : 0,dialogs.Errors.Count);
    }
    [Fact]
    public async Task ConcurrentEnvironmentAndRecentUpdates_AreSerializedAndKeepLatestFields()
    {
        var store=new DelayedStore(); using var vm=Session(new Dialogs(),store); await vm.InitializeAsync();
        var first=vm.SaveWorkspaceEnvironmentAsync(new(0,0,1000,700),new(280,300)); await store.Entered.Task;
        var path=ProjectFileTests.PathFor("overlap"); await CreateProjectAsync(path,"Данные сохраняются");
        var opening=vm.OpenProjectFromAsync(path);
        // Opening updates the MRU before its disk save can enter the occupied gate.
        var deadline=DateTime.UtcNow.AddSeconds(10);
        while(vm.RecentProjects.Count==0) { if(DateTime.UtcNow>deadline) throw new TimeoutException("Opening did not reach MRU update."); await Task.Delay(10); }
        var latest=new WorkspacePanels(327,341,false,true); var second=vm.SaveWorkspaceEnvironmentAsync(new(90,100,1190,900),latest);
        store.Release.SetResult(); await Task.WhenAll(first,opening,second);
        Assert.Equal(latest,store.Value.Panels); Assert.Equal(new SavedWindowPlacement(90,100,1190,900),store.Value.Window);
        Assert.Equal(path,Assert.Single(store.Value.RecentProjects).Path); Assert.Equal(1,store.MaximumConcurrent);
    }
    private static PrintSessionViewModel Session(Dialogs dialogs,IWorkspaceStateStore store,IApplicationPreferencesStore? preferences=null)=>
        new(dialogs,Path.Combine(Fixtures.Root,"ux013-recovery-"+Guid.NewGuid().ToString("N")),preferences:preferences ?? new Preferences(new() { AutosaveEnabled=false }),workspaceState:store);
    internal static async Task CreateProjectAsync(string path,string text)
    {
        var page=PrintPageItem.CreateBlank(SheetPaper.A4); page.Document.AddObject(new TextObject(Guid.NewGuid(),new(new(15,20,100,20)),text,new()));
        var session=new ProjectSession(ProjectPrintSettings.Capture(new PrintSettings()),new(page.Id,50,true,false,"#111827",4,36,0,300),[new(page.Document.Snapshot(),true,1)]);
        await new ProjectFileService().SaveAsync(session,path,true,default);
    }
    internal sealed class Dialogs : IUserDialogs
    {
        public MissingRecentProjectChoice Missing=MissingRecentProjectChoice.Cancel; public UnsavedProjectChoice Unsaved=UnsavedProjectChoice.Discard;
        public string? OpenPath; public List<string> Errors=[];
        public string[] PickFiles()=>[]; public string? PickPdfOutput()=>null; public string? PickLibreOffice()=>null;
        public bool ConfirmCropping(int count)=>false; public void Error(string message)=>Errors.Add(message);
        public MissingRecentProjectChoice ResolveMissingRecentProject(string path)=>Missing;
        public string? PickProjectToOpen()=>OpenPath; public UnsavedProjectChoice ConfirmProjectReplacement()=>Unsaved;
    }
    internal sealed class Preferences(ApplicationPreferences value) : IApplicationPreferencesStore
    {
        public ApplicationPreferences Value=value; public int Saves;
        public Task<ApplicationPreferences> LoadAsync(CancellationToken token)=>Task.FromResult(Value);
        public Task SaveAsync(ApplicationPreferences value,CancellationToken token) { Value=value; Saves++; return Task.CompletedTask; }
    }
    private sealed class BrokenStore : IWorkspaceStateStore
    {
        public Task<WorkspaceState> LoadAsync(CancellationToken token)=>Task.FromResult(new WorkspaceState());
        public Task SaveAsync(WorkspaceState state,CancellationToken token)=>throw new IOException("Тест: нет доступа.");
    }
    private sealed class DelayedStore : IWorkspaceStateStore
    {
        public TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WorkspaceState Value=new(); public int MaximumConcurrent; private int _active,_saves;
        public Task<WorkspaceState> LoadAsync(CancellationToken token)=>Task.FromResult(Value);
        public async Task SaveAsync(WorkspaceState state,CancellationToken token)
        {
            _active++; MaximumConcurrent=Math.Max(MaximumConcurrent,_active);
            try { if(++_saves==1) { Entered.SetResult(); await Release.Task.WaitAsync(token); } Value=state; }
            finally { _active--; }
        }
    }
}
