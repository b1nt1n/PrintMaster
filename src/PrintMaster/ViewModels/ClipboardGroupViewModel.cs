using PrintMaster.Models;
using PrintMaster.Services;
using System.Collections.Immutable;
using System.IO;

namespace PrintMaster.ViewModels;

public partial class PrintSessionViewModel
{
    // Groups reuse the validated portable project container, including original materials.
    public async Task<bool> CopyContextAsync(bool queue,bool cut=false)
    {
        if(!IsIdle || ShowPrintPreview && !queue) return false;
        FinishTextEditing();
        var pages=queue ? HighlightedPages.ToArray() : [];
        var queueState=pages.Select(p=>new ProjectPage(p.Document.Snapshot(),p.IsIncluded,p.Copies) {PrintOptions=p.PrintOptions}).ToArray();
        var page=SelectedPage; var state=page?.Document.Snapshot();
        var selected=SelectedObjects.ToArray(); var whole=SourceImageSelected;
        var annotation=SelectedSourceAnnotation; bool copied;
        if(queue)
        {
            if(pages.Length==0) return false;
            copied=await CopyArchiveAsync(queueState,ClipboardSelectionKind.Pages);
        }
        else if(selected.Length>1 && page!=null)
        {
            var used=selected.OfType<ContentObject>().Select(c=>c.AssetId).Concat(selected.OfType<MarkdownObject>().SelectMany(o=>o.Document?.Images.Select(i=>i.AssetId) ?? [])).ToHashSet();
            var sheet=state! with { Objects=selected.Select(o=>o is TextObject text ? text with {Flow=null} : o).ToImmutableArray(),
                Assets=state!.Assets.Where(a=>used.Contains(a.Id)).ToImmutableArray(),PrimaryContentId=null,NotebookText=null };
            copied=await CopyArchiveAsync([new(sheet,true,1)],ClipboardSelectionKind.Objects);
        }
        else copied=await CopySelectionCoreAsync();
        if(!copied || !cut) return copied;
        if(queue)
        {
            if(pages.Where((p,i)=>!Pages.Contains(p) || p.Copies!=queueState[i].Copies || p.IsIncluded!=queueState[i].IsIncluded || p.PrintOptions!=queueState[i].PrintOptions || !SameSheet(p.Document.Snapshot(),queueState[i].Sheet)).Any())
            {
                Status="Страницы изменились во время копирования. Копия в буфере готова, исходная очередь сохранена."; return false;
            }
            using var history=BeginHistoryEdit("Вырезать страницы"); var active=SelectedPage; var at=Pages.IndexOf(pages[0]);
            _editingQueue=true;
            try
            {
                foreach(var original in pages) { original.PropertyChanged-=PageChanged; original.Edits.PropertyChanged-=EditsChanged; Pages.Remove(original); }
                if(active==null || !Pages.Contains(active)) SelectedPage=Pages.ElementAtOrDefault(Math.Min(at,Pages.Count-1));
                SetQueueSelection(HighlightedPages,SelectedPage); NotifyQueue(); MarkProjectChanged();
            }
            finally { _editingQueue=false; NotifyQueueSelection(); }
            Status=$"Вырезано страниц: {pages.Length}. Выберите место и нажмите Ctrl+V."; return true;
        }
        if(page!=null && Pages.Contains(page) && state!=null && SameSheet(page.Document.Snapshot(),state) && (page.IsImage || page.IsBlank) && !page.HasMissingSources)
        {
            using var history=BeginHistoryEdit("Вырезать объекты");
            if(whole && page.Document.PrimaryContent is {} primary) page.Document.RemoveObjects([primary.Id]);
            else if(selected.Length>0) RemoveCutObjects(page,selected);
            else if(annotation!=null) page.Edits.Remove(annotation.Id);
            else return false;
            if(SelectedPage==page) { SetObjectSelection([]); SelectedSourceAnnotation=null; SourceImageSelected=false; }
            Status="Выбранное вырезано. Перейдите на нужный лист и нажмите Ctrl+V."; return true;
        }
        Status="Исходный лист изменился во время копирования. Копия в буфере готова, содержимое листа сохранено."; return false;
    }
    private void RemoveCutObjects(PrintPageItem page,IReadOnlyCollection<SheetObject> selected)
    {
        var ids=selected.Select(o=>o.Id).ToHashSet();var story=page.Document.NotebookText;
        var parts=page.Document.Objects.OfType<TextObject>().Where(t=>t.Flow is {Notebook:true} flow && flow.StoryId==story?.Id)
            .OrderBy(t=>t.Flow!.PartIndex).ToArray();
        if(story==null || !parts.Any(t=>ids.Contains(t.Id))) {page.Document.RemoveObjects(ids);return;}
        // A cut contains only the selected visible fragments. Removing one notebook
        // object via RemoveObjects would delete the entire story, including unselected
        // text and its hidden overflow. Remove exactly those source ranges instead.
        var ranges=new List<(int Start,int Length)>();var offset=0;
        foreach(var part in parts)
        {if(ids.Contains(part.Id))ranges.Add((offset,part.Text.Length));offset+=part.Text.Length;}
        var remaining=story.Source;
        foreach(var range in ranges.AsEnumerable().Reverse())remaining=remaining.Remove(range.Start,range.Length);
        var partIds=parts.Select(t=>t.Id).ToHashSet();page.Document.RemoveObjects(ids.Where(id=>!partIds.Contains(id)));
        if(remaining.Length==0)page.Document.SetNotebookText(null,[]);
        else SetNotebookStory(page,remaining,story.Style,story.Id);
    }
    private async Task<bool> CopyArchiveAsync(IEnumerable<ProjectPage> pages,ClipboardSelectionKind kind)
    {
        var session=CaptureProject() with { Pages=pages.ToImmutableArray() };
        BeginOperation(SessionOperationKind.ClipboardCopy);
        try
        {
            long materialBytes=0;
            foreach(var asset in session.Pages.SelectMany(p=>p.Sheet.Assets).DistinctBy(a=>a.WorkingPath,StringComparer.OrdinalIgnoreCase))
            {
                _operation!.Token.ThrowIfCancellationRequested();
                if(string.IsNullOrEmpty(asset.WorkingPath) || !File.Exists(asset.WorkingPath)) throw new InvalidDataException("Исходник отсутствует. Переподключите материал перед копированием или вырезанием.");
                materialBytes+=new FileInfo(asset.WorkingPath).Length;
                if(materialBytes>ClipboardService.MaxSelectionBytes) throw new InvalidDataException("Материалы группы превышают 64 МБ. Копируйте меньше объектов или страниц.");
            }
            var path=Path.Combine(_workspace.NewImportDirectory(),"clipboard.printmaster");
            await _projectFiles.SaveAsync(session,path,true,_operation!.Token);
            if(new FileInfo(path).Length>ClipboardService.MaxSelectionBytes) throw new InvalidDataException("Копия группы превышает 64 МБ. Копируйте меньше страниц.");
            // Verify that the exact portable data can be pasted before acknowledging a cut.
            using(var verification=new SessionWorkspace())
                await _projectFiles.LoadAsync(path,verification,_operation.Token,embeddedOnly:true,materialLimit:ClipboardService.MaxSelectionBytes);
            var bytes=await File.ReadAllBytesAsync(path,_operation.Token);
            await _clipboard.WriteAsync(new([],SelectionArchive:bytes,SelectionKind:kind),_operation.Token);
            _operation.Token.ThrowIfCancellationRequested();
            Status=kind==ClipboardSelectionKind.Pages ? "Страницы скопированы вместе с материалами." : "Группа объектов скопирована вместе с оформлением и материалами.";
            return true;
        }
        catch(OperationCanceledException) { Status="Копирование отменено."; }
        catch(Exception ex) { ShowOperationFailure("Не удалось скопировать","Проверьте материалы и размер выделения; скопируйте меньше объектов или страниц. Содержимое не вырезано при ошибке.",ex); }
        finally { EndOperation(); }
        return false;
    }
    private async Task PasteSelectionArchiveAsync(ClipboardSnapshot clipboard,PasteDestination destination)
    {
        if(clipboard.SelectionArchive is not {} bytes) return;
        FinishTextEditing(); var existing=destination==PasteDestination.Auto && CanInsertOnCurrentPage ? CaptureInsertionTarget(SelectedPage!) : null;
        LoadedProject loaded;
        BeginOperation(SessionOperationKind.ClipboardInsert);
        try
        {
            if(bytes.Length>ClipboardService.MaxSelectionBytes) throw new InvalidDataException("Группа превышает 64 МБ.");
            var path=Path.Combine(_workspace.NewImportDirectory(),"clipboard.printmaster");
            await File.WriteAllBytesAsync(path,bytes,_operation!.Token);
            loaded=await _projectFiles.LoadAsync(path,_workspace,_operation.Token,embeddedOnly:true,materialLimit:ClipboardService.MaxSelectionBytes);
            if(!loaded.Missing.IsEmpty || loaded.Session.Pages.IsEmpty) throw new InvalidDataException("В копии отсутствуют материалы.");
            if(clipboard.SelectionKind==ClipboardSelectionKind.Objects && loaded.Session.Pages.Length!=1) throw new InvalidDataException("Некорректная группа объектов.");
        }
        catch(OperationCanceledException) { Status="Вставка отменена."; return; }
        catch(Exception ex) { ShowOperationFailure("Не удалось вставить группу","Скопируйте выделение снова или выберите подходящий лист. Существующее содержимое сохраняется.",ex); return; }
        finally { EndOperation(); }
        var cloned=loaded.Session.Pages.Any(p=>p.Sheet.Objects.OfType<MarkdownObject>().Any()) ? MarkdownGraphCloner.ClonePages(loaded.Session.Pages) : default;
        var copies=cloned.IsDefault ? loaded.Session.Pages.Select(p=>CloneQueuePage(new(SheetDocument.Restore(p.Sheet)) { Copies=p.Copies,IsIncluded=p.IsIncluded,PrintOptions=p.PrintOptions })).ToArray()
            : cloned.Select(p=>new PrintPageItem(SheetDocument.Restore(p.Sheet)){Copies=p.Copies,IsIncluded=p.IsIncluded,PrintOptions=p.PrintOptions}).ToArray();
        if(clipboard.SelectionKind==ClipboardSelectionKind.Pages)
        {
            if(Pages.Count+copies.Length>20000) { _dialogs.Error("В очереди не может быть больше 20 000 страниц."); return; }
            QueueEdit([],()=>
            {
                foreach(var copy in copies) AddPage(copy);
                SetQueueSelection(copies,copies.First()); ShowPrintPreview=false;
            },label:"Вставить страницы");
            Status=$"Вставлено страниц: {copies.Length}. Ctrl+Z в очереди отменяет вставку."; return;
        }
        var target=existing ?? CaptureInsertionTarget(EnsureClipboardSheet(destination));
        var sheet=copies[0].Document.Snapshot();
        var items=sheet.Objects.Select(o=>ClipboardObjectService.CloneObject(o,new(3,3))).ToArray();
        if(items.OfType<MarkdownObject>().Any() && (target.Page.Document.NotebookText!=null || target.Page.Document.Objects.OfType<MarkdownObject>().Any()))
        {Status="Для этого Markdown создайте отдельный лист или вставьте документ в очередь страниц.";return;}
        target.Page.Document.InsertBatchIntoLayout(items,sheet.Assets,target.Paper,target.PrimaryBounds);
        ShowPrintPreview=false; Tool=EditorTool.Select; SetObjectSelection(items.Select(o=>o.Id));
        NotifySheetMode(); Status=$"Вставлено объектов: {items.Length}. Ctrl+Z на листе отменяет всю группу.";
    }
}
