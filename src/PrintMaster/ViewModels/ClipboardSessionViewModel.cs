using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrintMaster.Models;
using PrintMaster.Services;
using System.Collections.Immutable;
using System.Windows.Media.Imaging;

namespace PrintMaster.ViewModels;

public partial class PrintSessionViewModel
{
    private readonly IClipboardService _clipboard;
    public bool CanPasteClipboard=>IsIdle && _clipboard.HasSupportedContent();
    [ObservableProperty] private Annotation? selectedSourceAnnotation;
    [ObservableProperty] private bool sourceImageSelected;
    private string? _lastPastedObject;
    private int _pasteOffsetIndex;
    public bool CanCopySelection=>IsIdle && !ShowPrintPreview && (SelectedObjects.Count>0 || IsImageSelected && (SourceImageSelected || SelectedSourceAnnotation!=null));
    partial void OnSelectedSourceAnnotationChanged(Annotation? value) { DrawingStyleError=""; CopySelectionCommand.NotifyCanExecuteChanged(); RefreshHistoryView(); NotifyTextProperties(); }
    partial void OnSourceImageSelectedChanged(bool value) { DrawingStyleError=""; CopySelectionCommand.NotifyCanExecuteChanged(); RefreshHistoryView(); NotifyInspector(); }
    [RelayCommand(CanExecute=nameof(CanCopySelection))]
    private async Task CopySelectionAsync()
        =>await CopyContextAsync(false);
    private async Task<bool> CopySelectionCoreAsync()
    {
        FinishTextEditing(); var page=SelectedPage; if(page==null) return false;
        var item=SourceImageSelected && page.IsImage ? page.Document.PrimaryContent : SelectedSheetObject;
        var annotation=SelectedSourceAnnotation; var sheet=page.Document.Snapshot();
        if(item==null && annotation==null) return false;
        if(item is ContentObject { Placement.Mode:PlacementMode.Automatic } original)
        {
            var target=CaptureInsertionTarget(page);
            item=original with { Placement=original.Placement with { Mode=PlacementMode.Fixed,BoundsMm=target.PrimaryBounds } };
            sheet=sheet with { Paper=target.Paper };
        }
        // An object copy is a standalone block. A notebook fragment cannot retain
        // a link to the source story, which is deliberately absent from this packet.
        if(item is TextObject textObject) { item=textObject with {Flow=null};sheet=sheet with {NotebookText=null}; }
        BeginOperation(SessionOperationKind.ClipboardCopy);
        try
        {
            var snapshot=await Task.Run(()=>
            {
                var package=item!=null ? ClipboardObjectService.Capture(sheet,item) : new ClipboardObjectPackage(ClipboardObjectService.AnnotationVersion(annotation!),null,[],annotation,new(page.SourceWidth,page.SourceHeight));
                var json=ClipboardObjectService.Serialize(package); BitmapSource? image=null;
                if(item!=null)
                {
                    var bounds=SheetEditing.VisualBounds(item); var factor=Math.Min(120/25.4,1600/Math.Max(bounds.Width,bounds.Height));
                    var width=Math.Max(1,(int)Math.Ceiling(bounds.Width*factor)); var height=Math.Max(1,(int)Math.Ceiling(bounds.Height*factor));
                    var single=page.Snapshot() with { Sheet=sheet with { Objects=[item],PrimaryContentId=null } };
                    using var raster=_rasterizer.RenderViewport(single,width,height,new(0,0,width,height),false,_operation!.Token,bounds); image=BitmapBridge.ToWpf(raster);
                }
                return new ClipboardSnapshot([],image,item is TextObject t ? t.Text : annotation?.Kind==AnnotationKind.Text ? annotation.Text : null,json);
            },_operation!.Token);
            await _clipboard.WriteAsync(snapshot,_operation.Token); _operation.Token.ThrowIfCancellationRequested(); Status="Объект скопирован вместе с оформлением и материалом.";
            return true;
        }
        catch(OperationCanceledException) { Status="Копирование отменено."; }
        catch(Exception ex) { ShowOperationFailure("Не удалось скопировать","Проверьте исходники и доступность буфера обмена. Повторите копирование; содержимое не вырезано при ошибке.",ex); }
        finally { EndOperation(); }
        return false;
    }
    [RelayCommand(CanExecute=nameof(IsIdle))]
    private async Task PasteClipboardAsync(PasteDestination destination)
    {
        if(!IsIdle) return; FinishTextEditing(); var requested=SelectedPage;
        ClipboardSnapshot snapshot;
        BeginOperation(SessionOperationKind.ClipboardRead);
        try { snapshot=await _clipboard.ReadAsync(_operation!.Token); _operation.Token.ThrowIfCancellationRequested(); }
        catch(OperationCanceledException) { Status="Вставка отменена."; return; }
        catch(Exception ex) { ShowOperationFailure("Не удалось прочитать буфер","Повторите копирование и вставку после закрытия приложения, занимающего буфер.",ex); return; }
        finally { EndOperation(); }
        if(!IsIdle) return;
        if(snapshot.SelectionArchive==null && snapshot.ObjectJson==null && snapshot.Files.IsDefaultOrEmpty && snapshot.Image==null && string.IsNullOrEmpty(snapshot.Text))
        { Status="В буфере нет файлов, изображений, текста или объектов PrintMaster."; return; }
        if(destination==PasteDestination.Auto && requested!=null)
        {
            if(!Pages.Contains(requested)) { Status="Целевой лист изменился или удален. Повторите вставку на выбранный лист."; return; }
            SelectedPage=requested;
        }
        FinishTextEditing(); using var history=BeginHistoryEdit("Вставить из буфера");
        if(snapshot.SelectionArchive!=null) { await PasteSelectionArchiveAsync(snapshot,destination); return; }
        if(snapshot.ObjectJson!=null) { await PasteObjectAsync(snapshot.ObjectJson,destination); return; }
        if(!snapshot.Files.IsDefaultOrEmpty) { await HandleFileDropAsync(snapshot.Files,destination==PasteDestination.Auto); return; }
        if(snapshot.Image!=null) { await PasteImageAsync(snapshot.Image,destination); return; }
        if(snapshot.Text!=null) { PasteText(snapshot.Text,destination); return; }
        Status="В буфере нет файлов, изображений, текста или объектов PrintMaster.";
    }
    private PrintPageItem EnsureClipboardSheet(PasteDestination destination)
    {
        if(destination!=PasteDestination.Queue && CanInsertOnCurrentPage) return SelectedPage!;
        var page=PrintPageItem.CreateBlank(new(Settings.PaperSizeMm,Settings.Landscape,SheetMargins.SafeDefault));
        ShowPrintPreview=false; AddPage(page); SelectedPage=page; return page;
    }
    private void PasteText(string text,PasteDestination destination)
    {
        if(text.Length==0) return;
        if(text.Length>TextLimits.MaxCharacters) { _dialogs.Error("В буфере слишком большой текст."); return; }
        FinishTextEditing(); var page=EnsureClipboardSheet(destination); var target=CaptureInsertionTarget(page); ShowPrintPreview=false;
        var count=SelectedPage!.Document.Objects.Count; var offset=Math.Min(count%8,6)*3;
        InsertObject(target,SheetEditing.CreateText(text,_defaultTextStyle,new(target.Safe.X+offset,target.Safe.Y+offset),target.Safe));
        Status="Обычный текст вставлен отдельным блоком. Двойной щелчок — редактировать; Markdown остается обычным текстом.";
    }
    private async Task PasteImageAsync(BitmapSource image,PasteDestination destination)
    {
        FinishTextEditing(); var target=destination==PasteDestination.Auto && CanInsertOnCurrentPage ? CaptureInsertionTarget(SelectedPage!) : null;
        ShowPrintPreview=false; BeginOperation(SessionOperationKind.ClipboardInsert);
        try
        {
            var page=await _import.ImportClipboardImageAsync(image,_operation!.Token);
            if(target==null) { AddPage(page); SelectedPage=page; ShowPrintPreview=false; _=EnsureThumbnailAsync(page); }
            else
            {
                var source=page.Document.PrimaryAsset!; var area=ImageInsertionArea(target.Safe);
                var bounds=PageLayout.Place(source.LogicalSize,area,ScaleMode.Fit); var content=new ContentObject(Guid.NewGuid(),new(bounds),source.Id,ContentEdits.Empty(source.LogicalSize));
                InsertObject(target,content,source);
            }
            Status=target==null ? "Изображение из буфера добавлено отдельной страницей." : "Изображение из буфера вставлено на лист. Материал будет сохранен в проекте.";
        }
        catch(OperationCanceledException) { Status="Вставка изображения отменена."; }
        catch(Exception ex) { ShowOperationFailure("Не удалось вставить изображение","Повторите копирование изображения или вставьте его через файл.",ex); }
        finally { EndOperation(); }
    }
    private async Task PasteObjectAsync(string json,PasteDestination destination)
    {
        FinishTextEditing(); ClipboardObjectPackage package;
        try { package=ClipboardObjectService.Deserialize(json); }
        catch(Exception ex) { ShowOperationFailure("Не удалось вставить объект","Скопируйте объект снова или выберите подходящий лист для вставки.",ex); return; }
        if(_lastPastedObject!=json) { _lastPastedObject=json; _pasteOffsetIndex=0; }
        var index=Math.Min(++_pasteOffsetIndex,10);
        if(destination==PasteDestination.Auto && package.Annotation!=null && CanCropImage && SelectedPage!=null)
        {
            ShowPrintPreview=false;
            var copy=ClipboardAnnotationService.Clone(package.Annotation,package.AnnotationSourceSize!.Value,new(SelectedPage.SourceWidth,SelectedPage.SourceHeight),index);
            SelectedPage.Edits.Add(copy); SelectedSourceAnnotation=copy; Tool=EditorTool.Select; Status="Аннотация вставлена как редактируемая копия."; return;
        }
        var existingTarget=destination==PasteDestination.Auto && CanInsertOnCurrentPage ? CaptureInsertionTarget(SelectedPage!) : null; BeginOperation(SessionOperationKind.ClipboardInsert);
        try
        {
            SourceAsset? source=null;
            if(package.Object is MarkdownObject markdown)
            {
                var materials=ImmutableArray.CreateBuilder<SourceAsset>();var ids=new Dictionary<Guid,Guid>();
                foreach(var material in package.Materials)
                {var imported=await _import.ImportClipboardMaterialAsync(material,_operation!.Token);materials.Add(imported);ids.Add(material.Asset.Id,imported.Id);}
                var definition=MarkdownGraphCloner.RemapDocument(markdown.Document!,ids);
                var markdownTarget=existingTarget ?? CaptureInsertionTarget(EnsureClipboardSheet(destination));ShowPrintPreview=false;
                if(markdownTarget.Page.Document.NotebookText!=null || markdownTarget.Page.Document.Objects.OfType<MarkdownObject>().Any())
                    throw new InvalidOperationException("Для этого Markdown создайте отдельный лист или вставьте документ в очередь страниц.");
                var markdownCopy=markdown with {Id=Guid.NewGuid(),DocumentId=definition.Id,Document=definition,Placement=markdown.Placement with {BoundsMm=markdown.Placement.BoundsMm with {X=markdown.Placement.BoundsMm.X+index*3,Y=markdown.Placement.BoundsMm.Y+index*3}}};
                markdownTarget.Page.Document.InsertBatchIntoLayout([markdownCopy],materials,markdownTarget.Paper,markdownTarget.PrimaryBounds);
                SelectedSheetObjectId=markdownCopy.Id;NotifySheetMode();Status="Markdown вставлен с исходником и материалами.";return;
            }
            if(package.Materials.Length==1) source=await _import.ImportClipboardMaterialAsync(package.Materials[0],_operation!.Token);
            var target=existingTarget ?? CaptureInsertionTarget(EnsureClipboardSheet(destination)); ShowPrintPreview=false; var safe=target.Safe;
            SheetObject copy;
            if(package.Annotation!=null) copy=ClipboardAnnotationService.ToSheet(package.Annotation,package.AnnotationSourceSize!.Value,safe,index);
            else
            {
                copy=ClipboardObjectService.CloneObject(package.Object!,new(index*3,index*3),source?.Id);
            }
            InsertObject(target,copy,source); Status="Объект вставлен. Размеры, оформление и правки сохранены; Ctrl+Z отменяет вставку.";
        }
        catch(OperationCanceledException) { Status="Вставка объекта отменена."; }
        catch(Exception ex) { ShowOperationFailure("Не удалось вставить объект","Проверьте материалы и скопируйте объект снова. Уже имеющийся макет сохраняется.",ex); }
        finally { EndOperation(); }
    }
}

