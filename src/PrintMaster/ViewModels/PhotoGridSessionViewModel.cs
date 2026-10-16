using CommunityToolkit.Mvvm.Input;
using PrintMaster.Models;
using PrintMaster.Services;
using System.Collections.Immutable;

namespace PrintMaster.ViewModels;

public partial class PrintSessionViewModel
{
    private ImmutableArray<Guid> GridPhotoIds()
    {
        if(!CanEdit || SelectedPage is not {} page || page.HasMissingSources)return [];
        var sheet=page.Document.Snapshot();
        bool IsPhoto(SheetObject item)=>item is ContentObject {Visible:true,Locked:false} photo
            && sheet.Assets.Any(a=>a.Id==photo.AssetId && a.Kind==SourceKind.Image);
        if(!page.IsSheetLayout)return sheet.PrimaryContent is {} primary && IsPhoto(primary) ? [primary.Id] : [];
        var chosen=SelectedObjectIds.Length>0 ? sheet.Objects.Where(o=>SelectedObjectIds.Contains(o.Id)).ToArray()
            : sheet.Objects.Where(IsPhoto).ToArray();
        return chosen.Length>0 && chosen.All(IsPhoto) ? chosen.Select(o=>o.Id).ToImmutableArray() : [];
    }
    public bool CanArrangePhotoGrid=>GridPhotoIds().Length>0;
    public string PhotoGridAvailability=>CanArrangePhotoGrid ? "Разместить фото по сетке или повторить одно фото на листе."
        : !CanEdit ? EditorActionAvailability : "Добавьте фотографии или выделите только доступные фото на листе.";
    private void InitializePhotoGridNotifications()
    {
        PropertyChanged+=(_,e)=>
        {
            if(e.PropertyName is nameof(CanEdit) or nameof(SelectedPage) or nameof(SelectedObjectIds) or nameof(SelectedSheetObject) or nameof(SelectedSheetObjectId) or nameof(IsSheetSelected) or nameof(IsPreviewLoading))
            {OnPropertyChanged(nameof(CanArrangePhotoGrid));OnPropertyChanged(nameof(PhotoGridAvailability));ArrangePhotoGridCommand.NotifyCanExecuteChanged();}
        };
    }
    [RelayCommand(CanExecute=nameof(CanArrangePhotoGrid))]
    private void ArrangePhotoGrid()
    {
        FinishTextEditing();
        if(!CanArrangePhotoGrid || SelectedPage is not {} page)return;
        var before=page.Document.Snapshot();var request=CapturePhotoGridRequest(page);
        var options=_dialogs.PickPhotoGrid(request);
        if(options is not null)ApplyPhotoGrid(page,before,request,options);
    }
    internal PhotoGridRequest CapturePhotoGridRequest(PrintPageItem page)
    {
        var target=CaptureInsertionTarget(page);
        var paper=target.Paper;
        return new(page.Document.Snapshot() with {Paper=paper},GridPhotoIds(),target.Safe);
    }
    internal bool ApplyPhotoGrid(PrintPageItem page,SheetSnapshot before,PhotoGridRequest request,PhotoGridOptions options)
    {
        if(!CanEdit || SelectedPage!=page || page.Document.Snapshot()!=before || request.SafeAreaMm!=SafeAreaMm || !GridPhotoIds().SequenceEqual(request.PhotoIds))
        {Status="Лист или выделение изменились. Откройте размещение фото по сетке снова.";return false;}
        try
        {
            var plan=PhotoGridLayout.Create(request,options);
            using var history=BeginHistoryEdit("Разместить фото по сетке");
            page.Document.SetPhotoGrid(request.Sheet.Paper,plan);
            SelectedSourceAnnotation=null;SourceImageSelected=false;Tool=EditorTool.Select;SetObjectSelection(plan.Photos.Select(p=>p.Id));NotifySheetMode();
            Status=$"По сетке размещено {plan.Photos.Length} фото. Ctrl+Z — отменить размещение.";
            return true;
        }
        catch(ArgumentException ex){Status=ex.Message;return false;}
    }
}
