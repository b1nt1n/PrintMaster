using CommunityToolkit.Mvvm.Input;
using PrintMaster.Models;
using PrintMaster.Services;

namespace PrintMaster.ViewModels;

public partial class PrintSessionViewModel
{
    public bool CanSetObjectAngle=>CanEdit && SelectedSheetObject is { Locked:false,Visible:true } item && item is (ContentObject or ShapeObject or InkObject);
    public double ObjectAngle
    {
        get=>SelectedSheetObject?.Placement.RotationDegrees ?? 0;
        set
        {
            if(!CanSetObjectAngle || !double.IsFinite(value) || Math.Abs(value)>360000) { NotifySheetSelection(); return; }
            var item=SelectedSheetObject!; var angle=ObjectTransform.NormalizeAngle(value);
            if(Math.Abs(ObjectTransform.NormalizeAngle(angle-item.Placement.RotationDegrees))<1e-9) return;
            SelectedPage!.Document.SetPlacement(item.Id,item.Placement with { RotationDegrees=angle });
        }
    }
    public bool CanPlaceSourceImage=>CanEdit && CanCropImage && SelectedPage?.Document.PrimaryContent is { Locked:false,Visible:true };
    [RelayCommand(CanExecute=nameof(CanPlaceSourceImage))]
    private void PlaceSourceImage()
    {
        FinishTextEditing(); var target=CaptureInsertionTarget(SelectedPage!); var id=target.Page.Document.PrimaryContent!.Id;
        using var history=BeginHistoryEdit("Разместить фото на листе");
        target.Page.Document.PlacePrimaryImage(target.Paper,target.PrimaryBounds);
        SelectedSourceAnnotation=null; SourceImageSelected=false; Tool=EditorTool.Select; SetObjectSelection([id]);
        Status="Фото размещено на листе. Перетаскивайте ручки размера и поворота. Ctrl+Z — вернуться к исходному режиму.";
    }
}
