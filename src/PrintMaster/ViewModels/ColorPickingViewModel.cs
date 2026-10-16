using CommunityToolkit.Mvvm.ComponentModel;

namespace PrintMaster.ViewModels;

public partial class PrintSessionViewModel
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanvasHint))] private bool isPickingProjectColor;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanPickProjectColor))] private bool isSamplingProjectColor;
    public bool CanPickProjectColor=>CanEdit && !IsPreviewLoading && EditorPreview!=null && !IsSamplingProjectColor;
    private void InitializeColorPickingNotifications()
    {
        PropertyChanged+=(_,e)=>
        {
            if(e.PropertyName is nameof(CanEdit) or nameof(IsBusy) or nameof(SelectedPage) or nameof(IsSheetSelected)
                or nameof(ShowPrintPreview) or nameof(IsPreviewLoading) or nameof(EditorPreview)) OnPropertyChanged(nameof(CanPickProjectColor));
        };
    }
}
