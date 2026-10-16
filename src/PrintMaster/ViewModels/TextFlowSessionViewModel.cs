using PrintMaster.Models;
using PrintMaster.Services;
using System.Collections.Immutable;

namespace PrintMaster.ViewModels;

public partial class PrintSessionViewModel
{
    private TextObject? FlowObject(PrintPageItem page,Guid story)=>page.Document.Objects.OfType<TextObject>().FirstOrDefault(t=>t.Flow is { Notebook:false } f && f.StoryId==story);
    private PrintPageItem[] StoryPages(Guid story)=>Pages.Where(p=>FlowObject(p,story)!=null).OrderBy(p=>FlowObject(p,story)!.Flow!.PartIndex).ToArray();
    internal string StoryText(Guid story)=>Pages.FirstOrDefault(p=>p.Document.NotebookText?.Id==story)?.Document.NotebookText?.Source
        ?? string.Concat(StoryPages(story).Select(p=>FlowObject(p,story)!.Text));
    private RectD TextFlowSafe(SheetPaper paper)
    {
        var size=paper.OrientedSizeMm; var hardware=paper.SafeAreaMm;
        if(PrinterProfile is {} profile && Math.Abs(profile.ActualPaperMm.Width-size.Width)<1 && Math.Abs(profile.ActualPaperMm.Height-size.Height)<1) hardware=hardware.Intersect(profile.PrintableMm);
        return PageLayout.PrintSafeArea(size,hardware,5);
    }
    private void ApplyTextFlow(string source,TextStyle style,SheetPaper paper,Guid story,PrintPageItem? emptyFirst=null,int? insertion=null,bool recordHistory=true)
    {
        var notebookPage=Pages.FirstOrDefault(p=>p.Document.NotebookText?.Id==story);
        if(paper.Notebook!=null)
        {
            var regular=StoryPages(story); var first=notebookPage ?? emptyFirst ?? regular.FirstOrDefault();
            if(first==null) throw new InvalidOperationException("Не найден лист текста тетради.");
            using var edit=recordHistory ? BeginHistoryEdit("Текст на тетрадном листе") : null;
            if(regular.Length!=0)
            {
                foreach(var member in regular)
                {
                    var objects=member.Document.Objects.Where(o=>o is not TextObject t || t.Flow?.StoryId!=story).ToImmutableArray();
                    if(member==first) member.Document.ReplaceObjects([],"Продолжение текста");
                    var cleaned=member.Document.Snapshot() with { Objects=objects };
                    member.Document.RestoreHistoryState(cleaned);
                    if(member!=first && objects.IsEmpty) DetachFlowPage(member);
                }
            }
            SetNotebookStory(first,source,style,story,paper); SelectedPage=first;
            SelectedSheetObjectId=first.Document.Objects.OfType<TextObject>().First(t=>t.Flow?.Notebook==true).Id;
            NotifyFlowHistory(); NotifySheetMode(); return;
        }
        var safe=TextFlowSafe(paper); var fragments=TextLayoutService.Paginate(source,style,safe);
        var existing=StoryPages(story); var templates=notebookPage!=null ? new[] { notebookPage } : existing.Length==0 && emptyFirst!=null ? new[] { emptyFirst } : existing;
        if(templates.Length==0) throw new InvalidOperationException("Не найдена страница текста.");
        var firstIndex=insertion ?? templates.Min(Pages.IndexOf); var after=new List<PrintPageItem>();
        for(var i=0;i<fragments.Count;i++)
        {
            var template=i<templates.Length ? templates[i] : null; var original=template?.Document.Snapshot();
            var previous=template==null ? null : FlowObject(template,story);
            var fragment=fragments[i]; var item=new TextObject(previous?.Id ?? Guid.NewGuid(),new(new(safe.X,safe.Y,safe.Width,fragment.HeightMm)),fragment.Text,style,Flow:new(story,i,i+1<fragments.Count));
            var objects=(original?.Objects ?? []).Where(o=>o is not TextObject { Flow.Notebook:true }).ToImmutableArray();
            if(previous!=null) objects=objects.SetItem(objects.IndexOf(previous),item); else objects=objects.Add(item);
            var snapshot=new SheetSnapshot(template?.Id ?? Guid.NewGuid(),paper,original?.Assets ?? [],objects,null);
            after.Add(new(SheetDocument.Restore(snapshot)) { IsIncluded=template?.IsIncluded ?? templates[0].IsIncluded,Copies=template?.Copies ?? templates[0].Copies,PrintOptions=template is null ? templates[0].PrintOptions : template.PrintOptions });
        }
        // Keep manually added photographs/figures even if the text now needs fewer pages.
        foreach(var extra in templates.Skip(fragments.Count))
        {
            var snapshot=extra.Document.Snapshot(); var objects=snapshot.Objects.Where(o=>o is not TextObject t || t.Flow?.StoryId!=story).ToImmutableArray();
            if(objects.Length!=0) after.Add(new(SheetDocument.Restore(snapshot with { Objects=objects })) { Copies=extra.Copies,IsIncluded=extra.IsIncluded,PrintOptions=extra.PrintOptions });
        }
        if(Pages.Count-templates.Length+after.Count>20000) throw new InvalidOperationException("Очередь содержит более 20 000 страниц.");
        using var history=recordHistory ? BeginHistoryEdit("Текст на страницах") : null;
        ReplaceFlowPages(templates,after,firstIndex,after.First().Id);
        NotifyFlowHistory(); Status=$"Текст распределен: {RussianQuantity.Format(fragments.Count,"страница","страницы","страниц")}. Размер шрифта — {style.FontSizePt:g} пт.";
    }
    private void DetachFlowPage(PrintPageItem page)
    {
        page.PropertyChanged-=PageChanged; page.Edits.PropertyChanged-=EditsChanged; page.Thumbnail=null; Pages.Remove(page);
    }
    private void ReplaceFlowPages(IReadOnlyList<PrintPageItem> remove,IReadOnlyList<PrintPageItem> insert,int index,Guid? selected)
    {
        _applyingTextFlow=true;
        try
        {
            foreach(var page in remove) DetachFlowPage(page);
            var at=Math.Clamp(index,0,Pages.Count);
            foreach(var page in insert) { page.PropertyChanged+=PageChanged; page.Edits.PropertyChanged+=EditsChanged; Pages.Insert(at++,page); }
            SelectedPage=insert.FirstOrDefault(p=>p.Id==selected) ?? insert.FirstOrDefault() ?? Pages.ElementAtOrDefault(Math.Max(0,at-1));
            SelectedSheetObjectId=SelectedPage?.Document.Objects.OfType<TextObject>().FirstOrDefault()?.Id;
            NotifyQueue(); NotifySheetMode(); MarkProjectChanged();
            _=LoadEditorAsync(SelectedPage);
        }
        finally { _applyingTextFlow=false; }
    }
    private void NotifyFlowHistory()=>NotifyHistory();
    private bool ReflowSelectedPaper(SheetPaper paper)
    {
        if(SelectedPage?.Document.NotebookText is {} story)
        { ApplyTextFlow(story.Source,story.Style,paper,story.Id,SelectedPage); return true; }
        var flow=SelectedPage?.Document.Objects.OfType<TextObject>().FirstOrDefault(t=>t.Flow!=null);
        if(flow?.Flow==null) return false;
        ApplyTextFlow(StoryText(flow.Flow.StoryId),flow.Style,paper,flow.Flow.StoryId); return true;
    }
    private void NormalizeLoadedTextFlows()
    {
        foreach(var story in Pages.SelectMany(p=>p.Document.Objects.OfType<TextObject>()).Where(t=>t.Flow is { Notebook:false }).Select(t=>t.Flow!.StoryId).Distinct().ToArray())
        {
            var members=StoryPages(story); var preferred=members.FirstOrDefault(p=>p.Id==SelectedPage?.Id) ?? members.First(); var item=FlowObject(preferred,story)!;
            if(members.Select(p=>FlowObject(p,story)!.Style).Distinct().Count()>1 || members.Any(p=>TextLayoutService.Overflows(FlowObject(p,story)!) || SheetEditing.Outside(FlowObject(p,story)!,p.Document.Paper.SafeAreaMm)))
                try { ApplyTextFlow(StoryText(story),item.Style,preferred.Document.Paper,story,recordHistory:false); }
                catch(InvalidOperationException) { /* Keep all characters and show the layout warning. */ }
        }
        NotifyFlowHistory();
    }
}
