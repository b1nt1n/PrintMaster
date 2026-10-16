using PrintMaster.Models;
using PrintMaster.Services;

namespace PrintMaster.ViewModels;

public partial class PrintSessionViewModel
{
    private Guid[] _selectedObjectIds=[];
    private bool _settingObjectSelection;
    public Guid[] SelectedObjectIds=>_selectedObjectIds;
    public IReadOnlyList<SheetObject> SelectedObjects=>SelectedPage?.Document.Objects.Where(o=>_selectedObjectIds.Contains(o.Id)).ToArray() ?? [];
    public void SetObjectSelection(IEnumerable<Guid> ids,Guid? active=null)
    {
        DrawingStyleError="";
        _selectedObjectIds=ids.Distinct().Where(id=>SelectedPage?.Document.Objects.Any(o=>o.Id==id && o.Visible && !o.Locked)==true).ToArray();
        _settingObjectSelection=true;
        try { SelectedSheetObjectId=active!=null && _selectedObjectIds.Contains(active.Value) ? active : _selectedObjectIds.Length==0 ? null : _selectedObjectIds[^1]; }
        finally { _settingObjectSelection=false; }
        OnPropertyChanged(nameof(SelectedObjectIds)); OnPropertyChanged(nameof(SelectedObjects)); NotifySheetSelection();
        RefreshHistoryView();
    }
    public void SelectAllSheetObjects()
    {
        if(!CanEdit || !IsSheetSelected) return; FinishTextEditing(); Tool=EditorTool.Select;
        SetObjectSelection(SelectedPage!.Document.Objects.Where(o=>o.Visible && !o.Locked).Select(o=>o.Id));
    }
    public void DeleteSheetSelection()
    {
        if(!CanEdit || !IsSheetSelected) return; FinishTextEditing();
        SelectedPage!.Document.RemoveObjects(_selectedObjectIds); SetObjectSelection([]);
    }
    public void DuplicateSheetSelection()
    {
        if(!CanEdit || !IsSheetSelected || _selectedObjectIds.Length==0) return; FinishTextEditing();
        var copies=SelectedObjects.Select(o=>ClipboardObjectService.CloneObject(o,new(3,3))).ToArray();
        SelectedPage!.Document.AddObjects(copies); SetObjectSelection(copies.Select(o=>o.Id));
    }
    public void MoveSheetSelection(double dx,double dy)
    {
        if(!CanEdit || !IsSheetSelected || _selectedObjectIds.Length==0) return; FinishTextEditing();
        if(Math.Abs(dx)<1e-8 && Math.Abs(dy)<1e-8) return;
        var replacements=SelectedObjects.Select(o=>o with { Placement=o.Placement with { BoundsMm=o.Placement.BoundsMm with { X=o.Placement.BoundsMm.X+dx,Y=o.Placement.BoundsMm.Y+dy } } });
        SelectedPage!.Document.ReplaceObjects(replacements,"Переместить выделенные объекты");
    }
    public void DuplicateSourceImage()
    {
        if(!CanEdit || SelectedPage is not { IsImage:true } page || page.Document.PrimaryContent is not {} primary) return;
        FinishTextEditing(); var target=CaptureInsertionTarget(page);
        var copy=ClipboardObjectService.CloneObject(primary with { Placement=primary.Placement with { BoundsMm=target.PrimaryBounds,Mode=PlacementMode.Fixed } },new(3,3));
        InsertObject(target,copy);
    }
}
