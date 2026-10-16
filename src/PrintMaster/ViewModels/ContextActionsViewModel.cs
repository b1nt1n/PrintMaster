using CommunityToolkit.Mvvm.Input;
using PrintMaster.Models;
using PrintMaster.Services;

namespace PrintMaster.ViewModels;

public partial class PrintSessionViewModel
{
    private bool CanModifySheetSelection()=>CanEdit && IsSheetSelected && SelectedObjects.Count>0;
    [RelayCommand(CanExecute=nameof(CanModifySheetSelection))]
    private void AlignSheetSelection(string alignment)
    {
        FinishTextEditing(); var items=SelectedObjects.ToArray(); if(items.Length==0) return;
        var bounds=items.Select(SheetEditing.VisualBounds).ToArray();
        var x=bounds.Min(b=>b.X); var y=bounds.Min(b=>b.Y);
        var right=bounds.Max(b=>b.Right); var bottom=bounds.Max(b=>b.Bottom); var safe=SafeAreaMm;
        var (dx,dy)=PrintableAreaAlignment.Offset(new(x,y,right-x,bottom-y),safe,alignment);
        MoveSheetSelection(dx,dy);
    }
    private bool CanOrderSheetSelection(int direction)=>CanModifySheetSelection() && !SelectionOrder.Move(
        SelectedPage!.Document.Objects,o=>SelectedObjectIds.Contains(o.Id),direction).SequenceEqual(SelectedPage.Document.Objects);
    [RelayCommand(CanExecute=nameof(CanOrderSheetSelection))]
    private void OrderSheetSelection(int direction)
    {
        FinishTextEditing(); SelectedPage!.Document.SetObjectsOrder(SelectedObjectIds,direction);
    }
    internal void ApplyImageCrop(PrintPageItem page,Guid id,RectD crop)
    {
        if(!double.IsFinite(crop.X) || !double.IsFinite(crop.Y) || !double.IsFinite(crop.Width) || !double.IsFinite(crop.Height)) return;
        if(!CanEdit || SelectedPage!=page || page.Document.Objects.FirstOrDefault(o=>o.Id==id) is not ContentObject { Locked:false } content) return;
        var asset=page.Document.Snapshot().Assets.FirstOrDefault(a=>a.Id==content.AssetId);
        if(asset?.Kind!=SourceKind.Image || crop.IsEmpty || crop.Intersect(new(0,0,asset.LogicalSize.Width,asset.LogicalSize.Height))!=crop) return;
        if(crop==content.Edits.Crop) return;
        var placement=content.Placement;
        if(placement.Mode==PlacementMode.Fixed && content.KeepAspectRatio)
        {
            var before=new SizeD(content.Edits.Crop.Width,content.Edits.Crop.Height).Rotated(content.Edits.QuarterTurns);
            var after=new SizeD(crop.Width,crop.Height).Rotated(content.Edits.QuarterTurns);
            // Keep the physical width and aspect ratio of the image; never distort the cropped photo.
            var b=placement.BoundsMm;
            placement=placement with { BoundsMm=b with { Height=b.Height*(after.Height/after.Width)/(before.Height/before.Width) } };
        }
        page.Document.ReplaceObject(content with { Edits=content.Edits with { Crop=crop },Placement=placement },"Кадрировать фото");
    }
}
