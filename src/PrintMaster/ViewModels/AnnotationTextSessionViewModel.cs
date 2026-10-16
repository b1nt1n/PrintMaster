using PrintMaster.Models;
using PrintMaster.Services;
using System.Collections.Immutable;

namespace PrintMaster.ViewModels;

public partial class PrintSessionViewModel
{
    private Guid? _annotationEditContentId,_selectedTextAnnotationContentId;
    private bool IsEditingAnnotation=>IsEditingText && _annotationEditContentId!=null;
    private AnnotationTextPresentation? AnnotationPresentation
    {
        get
        {
            var page=IsEditingAnnotation ? _textEditPage : SelectedPage;
            var annotationId=IsEditingAnnotation ? _textEditId : SelectedSourceAnnotation?.Id;
            var contentId=IsEditingAnnotation ? _annotationEditContentId : _selectedTextAnnotationContentId ?? page?.Document.PrimaryContent?.Id;
            if(page==null || annotationId==null || contentId==null) return null;
            if(!IsEditingAnnotation && (SourceImageSelected || page.IsSheetLayout && (SelectedObjectIds.Length>1 || SelectedSheetObjectId!=contentId))) return null;
            var content=page.Document.Objects.OfType<ContentObject>().FirstOrDefault(c=>c.Id==contentId);
            var annotation=content?.Edits.Annotations.FirstOrDefault(a=>a.Id==annotationId && a.Kind==AnnotationKind.Text);
            if(content==null || annotation==null || !content.Visible) return null;
            var bounds=content.Placement.Mode==PlacementMode.Automatic ? CaptureInsertionTarget(page).PrimaryBounds : content.Placement.BoundsMm;
            return AnnotationTextLayout.Present(content,annotation,bounds);
        }
    }
    internal void BeginAnnotationTextEditing(PointD location,Annotation? existing=null,Guid? contentId=null)
    {
        if(!CanEdit || SelectedPage==null) return;
        FinishTextEditing();
        var page=SelectedPage; var content=page.Document.Objects.OfType<ContentObject>().FirstOrDefault(c=>c.Id==(contentId ?? page.Document.PrimaryContent?.Id));
        if(content==null || content.Locked || !content.Visible || page.Document.Snapshot().Assets.First(a=>a.Id==content.AssetId).Kind!=SourceKind.Image) return;
        if(existing!=null && !content.Edits.Annotations.Any(a=>a.Id==existing.Id && a.Kind==AnnotationKind.Text)) return;
        _textHistoryEdit=BeginHistoryEdit(existing==null ? "Добавить текст" : "Редактировать текст");
        page.Document.BeginEdit(existing==null ? "Добавить текст" : "Редактировать текст");
        _textEditPage=page; _annotationEditContentId=content.Id; _selectedTextAnnotationContentId=content.Id;
        _editingExistingText=existing!=null;
        var annotation=existing;
        if(annotation==null)
        {
            var bounds=content.Placement.Mode==PlacementMode.Automatic ? CaptureInsertionTarget(page).PrimaryBounds : content.Placement.BoundsMm;
            var transform=AnnotationTextLayout.Transform(content,bounds); var scale=Math.Sqrt(transform.M11*transform.M11+transform.M12*transform.M12);
            var width=Math.Max(5*scale,(content.Edits.Crop.Right-location.X-.5)*scale);
            annotation=AnnotationTextLayout.Create(location,_defaultTextStyle,scale,width);
            page.Document.SetContentEdits(content.Id,content.Edits with { Annotations=content.Edits.Annotations.Add(annotation) },"Добавить текст");
        }
        _textEditId=annotation.Id; SelectedSourceAnnotation=annotation;
        if(page.IsSheetLayout) SelectedSheetObjectId=content.Id;
        Tool=EditorTool.Select; IsEditingText=true; TextFormattingError=""; NotifyTextProperties();
        Status="Введите текст на листе. Ctrl+Enter — готово, Esc — отменить ввод.";
    }
    private void UpdateAnnotationText(string text,TextStyle style,double? widthMm=null)
    {
        if(AnnotationPresentation is not {} p) return;
        var page=IsEditingAnnotation ? _textEditPage! : SelectedPage!;
        var content=page.Document.Objects.OfType<ContentObject>().Single(c=>c.Id==p.ContentId);
        if(content.Locked) return;
        var updated=AnnotationTextLayout.Update(p.Annotation,text,style,p.MmPerUnit,widthMm);
        page.Document.SetContentEdits(content.Id,content.Edits with { Annotations=content.Edits.Annotations.Select(a=>a.Id==updated.Id ? updated : a).ToImmutableArray() },"Редактировать текст");
        SelectedSourceAnnotation=updated;
    }
    private void FinishAnnotationTextEditing(bool commit)
    {
        var page=_textEditPage!; var contentId=_annotationEditContentId!.Value; var annotationId=_textEditId;
        var annotation=AnnotationPresentation?.Annotation;
        _endingTextEdit=true;
        try
        {
            if(!commit || annotation==null || !_editingExistingText && annotation.Text.Length==0) page.Document.EndEdit(false);
            else
            {
                if(annotation.Text.Length==0)
                {
                    var content=page.Document.Objects.OfType<ContentObject>().Single(c=>c.Id==contentId);
                    page.Document.SetContentEdits(contentId,content.Edits with { Annotations=content.Edits.Annotations.Where(a=>a.Id!=annotationId).ToImmutableArray() },"Удалить текст");
                }
                page.Document.EndEdit(true);
            }
            _annotationEditContentId=null; _textEditPage=null; _textEditId=null; IsEditingText=false;
            SelectedSourceAnnotation=page.Document.Objects.OfType<ContentObject>().FirstOrDefault(c=>c.Id==contentId)?.Edits.Annotations.FirstOrDefault(a=>a.Id==annotationId);
            NotifyTextProperties(); Status=commit ? "Текст сохранен." : "Ввод текста отменен.";
        }
        finally { _endingTextEdit=false; var history=_textHistoryEdit; _textHistoryEdit=null; history?.Dispose(); }
    }
}
