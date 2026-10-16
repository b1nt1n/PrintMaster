using CommunityToolkit.Mvvm.Input;
using PrintMaster.Models;
using PrintMaster.Services;
using System.Collections.Immutable;
using System.IO;

namespace PrintMaster.ViewModels;

public partial class PrintSessionViewModel
{
    private MarkdownDocument? SelectedMarkdown=>SelectedPage?.Document.Objects.OfType<MarkdownObject>().FirstOrDefault()?.Document;
    public bool HasMarkdown=>SelectedMarkdown!=null;
    public bool ShowFreeSheetPaper=>IsSheetSelected && !HasMarkdown;
    public bool CanEditMarkdown=>IsIdle && HasMarkdown;
    private void InitializeMarkdownNotifications()
    {
        PropertyChanged+=(_,e)=>{if(e.PropertyName is nameof(SelectedPage) or nameof(IsIdle) or nameof(CanEdit) or nameof(IsSheetSelected))
        {OnPropertyChanged(nameof(HasMarkdown));OnPropertyChanged(nameof(ShowFreeSheetPaper));OnPropertyChanged(nameof(CanEditMarkdown));CreateMarkdownCommand.NotifyCanExecuteChanged();PasteMarkdownCommand.NotifyCanExecuteChanged();EditMarkdownCommand.NotifyCanExecuteChanged();}};
    }
    [RelayCommand(CanExecute=nameof(IsIdle))]
    private async Task CreateMarkdownAsync()
    {
        FinishTextEditing();
        var paper=SelectedPage?.IsSheetLayout==true ? SelectedPage.Document.Paper : MarkdownPaper();
        var definition=new MarkdownDocument(Guid.NewGuid(),"",MarkdownStyle.Default,[],"Заметка Markdown");
        await ShowMarkdownEditorAsync(new(definition,[]),paper,null);
    }
    [RelayCommand(CanExecute=nameof(IsIdle))]
    private async Task PasteMarkdownAsync()
    {
        FinishTextEditing();
        try
        {
            var clipboard=await _clipboard.ReadAsync(_lifetime.Token);
            if(!IsIdle)return;
            if(string.IsNullOrWhiteSpace(clipboard.Text))
            {Status="Скопируйте текст Markdown, затем выберите «Вставить как Markdown».";return;}
            var paper=SelectedPage?.IsSheetLayout==true ? SelectedPage.Document.Paper : MarkdownPaper();
            var definition=new MarkdownDocument(Guid.NewGuid(),clipboard.Text,MarkdownStyle.Default,[],"Заметка Markdown");
            await ShowMarkdownEditorAsync(new(definition,[]),paper,null);
        }
        catch(OperationCanceledException) { }
        catch(Exception ex) {ShowOperationFailure("Не удалось вставить Markdown","Скопируйте текст и повторите вставку.",ex);}
    }
    [RelayCommand(CanExecute=nameof(CanEditMarkdown))]
    private async Task EditMarkdownAsync()
    {
        FinishTextEditing();if(SelectedMarkdown is not {} definition || SelectedPage is not {} page)return;
        var assets=Pages.Where(p=>p.Document.Objects.OfType<MarkdownObject>().Any(m=>m.DocumentId==definition.Id))
            .SelectMany(p=>p.Document.Snapshot().Assets).DistinctBy(a=>a.Id).ToImmutableArray();
        await ShowMarkdownEditorAsync(new(definition,assets),page.Document.Paper,definition.Id);
    }
    private SheetPaper MarkdownPaper()=>new(Settings.PaperSizeMm,Settings.Landscape,SheetMargins.SafeDefault)
        {Notebook=Settings.Paper==PaperPreset.Notebook ? Settings.Notebook : null};
    private async Task ShowMarkdownEditorAsync(PreparedMarkdown initial,SheetPaper paper,Guid? existingId)
    {
        var original=Pages.Where(p=>p.Document.Objects.OfType<MarkdownObject>().Any(m=>m.DocumentId==existingId)).Select(p=>(p,p.Document.Snapshot(),p.PrintOptions)).ToArray();
        var resolved=initial;
        using var editor=new MarkdownEditorViewModel(initial,paper,async(document,token)=>
        {
            var result=await PrepareMarkdownAsync(document with {Images=resolved.Document.Images},resolved.Assets,token);
            token.ThrowIfCancellationRequested();resolved=result;return result;
        });
        var discardedEffect=existingId!=null
            ? "Правки не применены. К печати доступны прежние страницы, без этих изменений."
            : "Новый документ не добавлен. Очередь печати не изменилась.";
        void UpdateEditorWarning(object? sender,System.ComponentModel.PropertyChangedEventArgs e)
        {
            if(e.PropertyName is not (nameof(MarkdownEditorViewModel.HasBlockingError) or nameof(MarkdownEditorViewModel.CanApply) or nameof(MarkdownEditorViewModel.BlockingError)))return;
            if(editor.HasBlockingError && !string.IsNullOrWhiteSpace(editor.Source))
            {
                HasActiveMarkdownDraftError=true;
                ReportMarkdownWarning(editor.BlockingError,"Черновик не применён; исправьте ошибку в редакторе. Исходник можно сохранить в .md.");
            }
            else if(editor.CanApply) {HasActiveMarkdownDraftError=false;MarkdownWarning="";}
        }
        bool accepted;
        editor.PropertyChanged+=UpdateEditorWarning;
        try {accepted=_dialogs.EditMarkdown(editor);}
        finally {editor.PropertyChanged-=UpdateEditorWarning;HasActiveMarkdownDraftError=false;}
        if(!accepted || editor.Result is not {} prepared || editor.HasBlockingError)
        {
            if(editor.HasBlockingError && !string.IsNullOrWhiteSpace(editor.Source))ReportMarkdownWarning(editor.BlockingError,discardedEffect);
            else {MarkdownWarning="";Status=discardedEffect;}
            return;
        }
        if(original.Any(s=>!Pages.Contains(s.p) || s.p.Document.Snapshot()!=s.Item2 || s.p.PrintOptions!=s.PrintOptions))
        {Status="Страницы Markdown изменились. Откройте редактор снова.";return;}
        BeginOperation(SessionOperationKind.Import);
        try{await ApplyMarkdownAsync(prepared,editor.Paper,existingId,_operation!.Token);MarkdownWarning="";}
        catch(OperationCanceledException){Status="Изменение Markdown отменено.";}
        catch(Exception ex){ReportMarkdownWarning(ex.Message,discardedEffect);ShowOperationFailure("Не удалось оформить Markdown","Проверьте текст, формулы и материалы в редакторе.",ex);}
        finally{EndOperation();}
    }
    internal async Task<PreparedMarkdown> PrepareMarkdownAsync(MarkdownDocument definition,ImmutableArray<SourceAsset> existing,CancellationToken token)
    {
        var images=ImmutableArray.CreateBuilder<MarkdownImageReference>();var assets=ImmutableArray.CreateBuilder<SourceAsset>();
        foreach(var url in MarkdownLayoutService.FindImageUrls(definition.Source))
        {
            token.ThrowIfCancellationRequested();
            var previous=definition.Images.FirstOrDefault(i=>i.Url==url);
            var asset=previous!=null ? existing.FirstOrDefault(a=>a.Id==previous.AssetId && a.Kind==SourceKind.Image && File.Exists(a.WorkingPath)) : null;
            if(asset==null)
            {
                if(Uri.TryCreate(url,UriKind.Absolute,out var uri) && !uri.IsFile)
                    throw new ArgumentException("Для Markdown используйте локальные изображения PNG/JPG/BMP: "+url);
                var path=uri?.IsFile==true ? uri.LocalPath : Path.GetFullPath(Uri.UnescapeDataString(url),definition.SourceDirectory ?? Directory.GetCurrentDirectory());
                if(!IsImageFile(path))throw new ArgumentException("Поддерживаются изображения PNG/JPG/BMP: "+url);
                var imported=await _import.ImportAsync(path,token);asset=imported.Single().Document.PrimaryAsset!;
            }
            assets.Add(asset);images.Add(new(url,asset.Id));
        }
        var result=definition with {Images=images.ToImmutable()};ProjectFileService.ValidateMarkdownDocument(result);
        return new(result,assets.DistinctBy(a=>a.Id).ToImmutableArray());
    }
    internal async Task ImportMarkdownAsync(string path,CancellationToken token)
    {
        try
        {
            if(new FileInfo(path).Length>4L*1024*1024)throw new ArgumentException("Markdown-файл слишком большой.");
            var source=await File.ReadAllTextAsync(path,token);
            var definition=new MarkdownDocument(Guid.NewGuid(),source,MarkdownStyle.Default,[],Path.GetFileName(path)) {SourceDirectory=Path.GetDirectoryName(Path.GetFullPath(path))};
            var prepared=await PrepareMarkdownAsync(definition,[],token);await ApplyMarkdownAsync(prepared,MarkdownPaper(),null,token);
        }
        catch(OperationCanceledException) {throw;}
        catch(Exception ex)
        {
            ReportMarkdownWarning($"Не удалось добавить «{Path.GetFileName(path)}»: {ex.Message}","Файл не изменён и не добавлен в очередь. Исправьте исходник и повторите импорт.");
            throw;
        }
    }
    internal async Task ApplyMarkdownAsync(PreparedMarkdown prepared,SheetPaper paper,Guid? replaceId,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();ProjectFileService.ValidateMarkdownDocument(prepared.Document);
        if(prepared.Document.LayoutEngineVersion!=MarkdownDocument.CurrentLayoutEngineVersion)
            prepared=prepared with {Document=prepared.Document with {LayoutEngineVersion=MarkdownDocument.CurrentLayoutEngineVersion}};
        var expected=Pages.Select(p=>(Page:p,State:p.Document.Snapshot(),p.Copies,p.IsIncluded,p.PrintOptions)).ToArray();
        var layout=await MarkdownLayoutService.Shared.LayoutAsync(prepared.Document,paper,prepared.Assets,token);token.ThrowIfCancellationRequested();
        if(!Pages.SequenceEqual(expected.Select(p=>p.Page)) || expected.Any(p=>p.Page.Document.Snapshot()!=p.State || p.Page.Copies!=p.Copies || p.Page.IsIncluded!=p.IsIncluded || p.Page.PrintOptions!=p.PrintOptions))
            throw new InvalidOperationException("Очередь или страницы изменились во время верстки Markdown. Откройте редактор снова.");
        var old=Pages.Where(p=>p.Document.Objects.OfType<MarkdownObject>().Any(m=>m.DocumentId==replaceId)).ToArray();
        var oldByIndex=old.ToDictionary(p=>p.Document.Objects.OfType<MarkdownObject>().Single().PageIndex);
        var generated=new List<PrintPageItem>();
        for(var i=0;i<layout.Pages.Length;i++)
        {
            var previous=oldByIndex.GetValueOrDefault(i);var snapshot=previous?.Document.Snapshot();
            var content=new MarkdownObject(Guid.NewGuid(),new(paper.SafeAreaMm),prepared.Document.Id,i) {Document=prepared.Document};
            var others=snapshot?.Objects.Where(o=>o is not MarkdownObject).ToImmutableArray() ?? [];
            var assets=prepared.Assets.AddRange((snapshot?.Assets ?? []).Where(a=>others.OfType<ContentObject>().Any(o=>o.AssetId==a.Id))).DistinctBy(a=>a.Id).ToImmutableArray();
            var sheet=new SheetSnapshot(snapshot?.Id ?? Guid.NewGuid(),paper,assets,ImmutableArray.Create<SheetObject>(content).AddRange(others),null);
            generated.Add(new(SheetDocument.Restore(sheet)){Copies=previous?.Copies ?? 1,IsIncluded=previous?.IsIncluded ?? true,PrintOptions=previous?.PrintOptions});
        }
        foreach(var previous in old.Where(p=>p.Document.Objects.OfType<MarkdownObject>().Single().PageIndex>=layout.Pages.Length))
        {
            var snapshot=previous.Document.Snapshot();var others=snapshot.Objects.Where(o=>o is not MarkdownObject).ToImmutableArray();
            if(others.IsEmpty)continue;
            var used=others.OfType<ContentObject>().Select(o=>o.AssetId).ToHashSet();
            generated.Add(new(SheetDocument.Restore(snapshot with {Objects=others,Assets=snapshot.Assets.Where(a=>used.Contains(a.Id)).ToImmutableArray(),PrimaryContentId=null}))
                {Copies=previous.Copies,IsIncluded=previous.IsIncluded,PrintOptions=previous.PrintOptions});
        }
        if(Pages.Count-old.Length+generated.Count>20000)throw new InvalidOperationException("В очереди не может быть больше 20 000 страниц.");
        using var history=BeginHistoryEdit(replaceId==null ? "Добавить Markdown" : "Изменить Markdown");
        var insertion=old.Length>0 ? Pages.IndexOf(old[0]) : Pages.Count;
        _editingQueue=true;
        try
        {
            foreach(var page in old){page.PropertyChanged-=PageChanged;page.Edits.PropertyChanged-=EditsChanged;Pages.Remove(page);}
            foreach(var page in generated)
            {page.PropertyChanged+=PageChanged;page.Edits.PropertyChanged+=EditsChanged;Pages.Insert(insertion++,page);_=EnsureThumbnailAsync(page);}
            SelectedPage=generated.FirstOrDefault();SetQueueSelection(generated,SelectedPage);NotifyQueue();MarkProjectChanged();
        }
        finally{_editingQueue=false;NotifyQueueSelection();}
        Status=$"Markdown оформлен: {RussianQuantity.Format(generated.Count,"страница","страницы","страниц")}. Исходный текст можно редактировать снова.";
    }
}
