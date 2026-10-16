using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrintMaster.Models;

namespace PrintMaster.ViewModels;

public partial class PrintSessionViewModel
{
    [ObservableProperty] private bool snapToPrintableArea=true;
    partial void OnSnapToPrintableAreaChanged(bool value)=>_environment=_environment with {SnapToPrintableArea=value};
    public IReadOnlyList<Choice<string>> PlacementAlignments {get;}=[new("center-both","По центру листа"),new("top-left","Верхний левый угол"),new("top-right","Верхний правый угол"),new("bottom-left","Нижний левый угол"),new("bottom-right","Нижний правый угол"),new("left","По левому краю"),new("right","По правому краю"),new("top","По верхнему краю"),new("bottom","По нижнему краю"),new("center","По центру горизонтально"),new("middle","По центру вертикально")];
    [ObservableProperty] private string selectedPlacementAlignment="center-both";
    [RelayCommand(CanExecute=nameof(CanModifySheetSelection))]
    private void ApplyPlacementAlignment()=>AlignSheetSelectionCommand.Execute(SelectedPlacementAlignment);
}
