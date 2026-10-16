namespace PrintMaster.ViewModels;

public partial class PrintSessionViewModel
{
    // The view owns draft fields; headless/domain commands have no pending UI input.
    internal Func<bool>? CommitInputs { get; set; }
    private bool CommitInputForAction()
    {
        if(CommitInputs?.Invoke()!=false) return true;
        Status="Исправьте числовое поле с ошибкой или нажмите в нем Esc, чтобы вернуть примененное значение.";
        return false;
    }
    public double DrawingWidthMinimum=>IsSheetSelected ? 0.05 : 0.1;
    public double DrawingWidthMaximum=>IsSheetSelected ? 20 : 200;
    public string DrawingWidthUnit=>IsSheetSelected ? "мм" : "ед.";
    public string DrawingContextKey=>$"{SelectedPage?.Id}:{string.Join(',',SelectedObjectIds)}:{SelectedSourceAnnotation?.Id}";
    public string LayoutPaperLimits=>"Размер макета: 25–1000 мм. P1102: ширина подачи 76–216 мм, длина 127–356 мм. Альбомная ориентация поворачивает макет; бумага подается коротким краем.";
}
