using PrintMaster.Controls;
using PrintMaster.Models;
using PrintMaster.Services;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace PrintMaster.Views;

public partial class MainWindow
{
    private TextColorEditor? _colorPickTarget;
    private PrintPageItem? _colorPickPage;
    private string _colorPickContext="";
    private string _colorPickFallbackRole="InkColor";
    private CancellationTokenSource? _colorPickCancellation;
    private int _colorPickEpoch;
    private bool _applyingPickedColor;
    internal Task? ColorSamplingTask {get;private set;}
    internal Func<PageSnapshot,PointD,bool,CancellationToken,string> SampleProjectColor {get;set;}=
        (page,point,source,token)=>source ? new ProjectColorSampler().SampleSource(page,point,token) : new ProjectColorSampler().SampleSheet(page.GetSheet(),point,token);
    private void InitializeProjectColorPicker()
    {
        AddHandler(TextColorEditor.PickColorRequestedEvent,new RoutedEventHandler((_,e)=>
        {if(e.OriginalSource is TextColorEditor target) {BeginProjectColorPick(target);e.Handled=true;}}));
        Editor.ColorSampleRequested+=(_,request)=>StartColorSample(request,true);
        SheetEditor.ColorSampleRequested+=(_,request)=>StartColorSample(request,false);
        ViewModel.PropertyChanged+=(_,_)=>
        {
            if(ViewModel.IsPickingProjectColor && !_applyingPickedColor &&
                (!ViewModel.CanEdit || _colorPickPage!=ViewModel.SelectedPage || _colorPickContext!=ColorPickContext()
                 || !ViewModel.IsSamplingProjectColor && ViewModel.IsPreviewLoading)) CancelProjectColorPick();
        };
        Closed+=(_,_)=>CancelProjectColorPick();
    }
    private string ColorPickContext()=>string.Join("|",ViewModel.SelectedPage?.Id,ViewModel.Tool,ViewModel.SelectedSheetObjectId,
        string.Join(",",ViewModel.SelectedObjectIds),ViewModel.SelectedSourceAnnotation?.Id,ViewModel.SourceImageSelected,ViewModel.SelectedText?.Id);
    internal void BeginProjectColorPick(TextColorEditor? target)
    {
        if(!ViewModel.CanPickProjectColor || _imageCropOpen) return;
        ViewModel.FinishTextEditing();
        if(!ViewModel.CanPickProjectColor || target!=null && (!target.IsVisible || !target.IsEnabled)) return;
        CancelProjectColorPick();Editor.CancelGesture();SheetEditor.CancelGesture();CloseWorkMenus();
        _colorPickTarget=target;_colorPickPage=ViewModel.SelectedPage;_colorPickContext=ColorPickContext();
        _colorPickCancellation=new();_colorPickPage!.Document.PropertyChanged+=PickDocumentChanged;
        ViewModel.IsPickingProjectColor=true;Editor.Cursor=Cursors.Cross;SheetEditor.Cursor=Cursors.Cross;
        ViewModel.Status="Пипетка: выберите точку на холсте. Esc — отмена.";
    }
    internal bool BeginDefaultProjectColorPick()
    {
        if(!ViewModel.CanPickProjectColor) return false;
        var colors=FindColorEditors(this).Where(c=>c.IsVisible && c.IsEnabled && c.CanPickColor).ToArray();
        var property=ViewModel.ShowTextFormatting ? "TextColor" : ViewModel.ShowSelectedDrawingProperties ? "SelectedDrawingColor" : "InkColor";
        var target=colors.FirstOrDefault(c=>c.GetBindingExpression(TextColorEditor.ColorProperty)?.ParentBinding.Path?.Path==property);
        BeginProjectColorPick(target);if(ViewModel.IsPickingProjectColor) _colorPickFallbackRole=property;return ViewModel.IsPickingProjectColor;
    }
    private static IEnumerable<TextColorEditor> FindColorEditors(DependencyObject node)
    {
        if(node is TextColorEditor editor) yield return editor;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)
            foreach(var item in FindColorEditors(VisualTreeHelper.GetChild(node,i))) yield return item;
    }
    private void PickDocumentChanged(object? sender,PropertyChangedEventArgs e)
    {if(e.PropertyName==nameof(SheetDocument.Revision) && !_applyingPickedColor) CancelProjectColorPick();}
    private void StartColorSample(ColorSampleRequest request,bool source)
    {
        if(!ViewModel.IsPickingProjectColor || ViewModel.IsSamplingProjectColor || !ViewModel.CanPickProjectColor || _colorPickPage==null || _colorPickCancellation==null) return;
        ColorSamplingTask=SampleProjectColorAsync(request,source);
    }
    private async Task SampleProjectColorAsync(ColorSampleRequest request,bool source)
    {
        var epoch=_colorPickEpoch;var page=_colorPickPage!;var snapshot=page.Document.Snapshot();var rasterSnapshot=page.Snapshot();var target=_colorPickTarget;var token=_colorPickCancellation!.Token;
        ViewModel.IsSamplingProjectColor=true;
        try
        {
            var color=request.PaperWhite ? "#FFFFFF" : await Task.Run(()=>SampleProjectColor(rasterSnapshot,request.Point,source,token),token);
            if(token.IsCancellationRequested || epoch!=_colorPickEpoch || !ViewModel.IsPickingProjectColor || !ViewModel.CanEdit
                || ViewModel.SelectedPage!=page || page.Document.Snapshot()!=snapshot || _colorPickContext!=ColorPickContext()
                || target!=null && (!target.IsVisible || !target.IsEnabled)) return;
            _applyingPickedColor=true;
            try
            {
                if(target!=null) target.ApplySampleColor(color);
                else if(_colorPickFallbackRole=="TextColor") ViewModel.TextColor=color;
                else if(_colorPickFallbackRole=="SelectedDrawingColor") ViewModel.SelectedDrawingColor=color;
                else ViewModel.InkColor=color;
            }
            finally {_applyingPickedColor=false;}
            ViewModel.Status="Выбран цвет "+color+".";
        }
        catch(OperationCanceledException) { }
        catch(Exception ex)
        {if(epoch==_colorPickEpoch) ViewModel.Status="Не удалось взять цвет: "+ex.Message+" Проверьте исходник и попробуйте снова.";}
        finally {if(epoch==_colorPickEpoch) CancelProjectColorPick();}
    }
    internal void CancelProjectColorPick()
    {
        var wasPicking=ViewModel.IsPickingProjectColor;
        _colorPickEpoch++;
        if(_colorPickPage!=null) _colorPickPage.Document.PropertyChanged-=PickDocumentChanged;
        _colorPickCancellation?.Cancel();_colorPickCancellation?.Dispose();_colorPickCancellation=null;
        _colorPickPage=null;_colorPickTarget=null;_colorPickContext="";_colorPickFallbackRole="InkColor";
        ViewModel.IsPickingProjectColor=false;ViewModel.IsSamplingProjectColor=false;
        Editor.Cursor=ViewModel.Tool==EditorTool.Select ? Cursors.Arrow : Cursors.Cross;
        SheetEditor.Cursor=ViewModel.Tool==EditorTool.Select ? Cursors.Arrow : Cursors.Cross;
        if(wasPicking && ViewModel.Status.StartsWith("Пипетка:",StringComparison.Ordinal)) ViewModel.Status="Выбор цвета отменён.";
    }
}
