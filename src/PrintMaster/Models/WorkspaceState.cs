using System.Collections.Immutable;
using System.IO;

namespace PrintMaster.Models;

public enum WorkspacePanel { Queue,Inspector }
public sealed record SavedWindowPlacement(int Left,int Top,int Right,int Bottom,uint Dpi=96,bool Maximized=false)
{
    public bool IsValid=>Dpi is >=48 and <=768 && Math.Abs((long)Left)<=200000 && Math.Abs((long)Top)<=200000 &&
        (long)Right-Left is >=64 and <=32000 && (long)Bottom-Top is >=64 and <=32000;
}
public sealed record WorkspacePanels(double QueueWidth=260,double InspectorWidth=290,bool QueueVisible=true,
    bool InspectorVisible=true,WorkspacePanel PreferredPanel=WorkspacePanel.Queue,bool ExpandInCompact=false)
{
    public WorkspacePanels Normalize()=>this with
    {
        QueueWidth=double.IsFinite(QueueWidth) && QueueWidth is >=220 and <=340 ? QueueWidth : 260,
        InspectorWidth=double.IsFinite(InspectorWidth) && InspectorWidth is >=240 and <=360 ? InspectorWidth : 290,
        PreferredPanel=Enum.IsDefined(PreferredPanel) ? PreferredPanel : WorkspacePanel.Queue
    };
    public (bool Queue,bool Inspector) VisibleAt(double width)
    {
        if(width>=1080) return (QueueVisible,InspectorVisible);
        if(width<820 && !ExpandInCompact) return (false,false);
        if(PreferredPanel==WorkspacePanel.Inspector && InspectorVisible) return (false,true);
        if(QueueVisible) return (true,false);
        return (false,InspectorVisible);
    }
}
public sealed record RecentProjectRecord(string Path,DateTimeOffset LastUsedUtc);
public sealed record WorkspaceState
{
    public const int RecentLimit=10;
    public int Version { get; init; }=1;
    public SavedWindowPlacement? Window { get; init; }
    public WorkspacePanels Panels { get; init; }=new();
    public bool SnapToPrintableArea { get; init; }=true;
    public ImmutableArray<RecentProjectRecord> RecentProjects { get; init; }=[];
    public WorkspaceState Normalize()
    {
        if(Version!=1) throw new InvalidDataException("Версия рабочего окружения не поддерживается.");
        var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase); var recent=new List<RecentProjectRecord>();
        if(!RecentProjects.IsDefault)
            foreach(var item in RecentProjects)
            {
                if(item==null || string.IsNullOrWhiteSpace(item.Path) || !System.IO.Path.IsPathFullyQualified(item.Path)) continue;
                try
                {
                    var full=System.IO.Path.GetFullPath(item.Path);
                    if(!System.IO.Path.GetExtension(full).Equals(".printmaster",StringComparison.OrdinalIgnoreCase) || !paths.Add(full)) continue;
                    recent.Add(item with { Path=full }); if(recent.Count==RecentLimit) break;
                }
                catch(ArgumentException) { }
                catch(NotSupportedException) { }
                catch(IOException) { }
            }
        return this with { Window=Window?.IsValid==true ? Window : null,Panels=(Panels ?? new()).Normalize(),RecentProjects=recent.ToImmutableArray() };
    }
    public WorkspaceState Remember(string path,string? replacedPath=null)
    {
        var full=System.IO.Path.GetFullPath(path);
        var records=RecentProjects.IsDefault ? [] : RecentProjects;
        return (this with { RecentProjects=new[] { new RecentProjectRecord(full,DateTimeOffset.UtcNow) }
            .Concat(records.Where(p=>!string.Equals(p.Path,full,StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(p.Path,replacedPath,StringComparison.OrdinalIgnoreCase))).ToImmutableArray() }).Normalize();
    }
}
