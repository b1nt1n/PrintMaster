using CommunityToolkit.Mvvm.ComponentModel;
using PrintMaster.Models;
using PrintMaster.Services;
using System.Collections.Immutable;
using System.Windows.Media.Imaging;

namespace PrintMaster.ViewModels;

public sealed partial class PhotoGridViewModel : ObservableObject,IDisposable
{
    private readonly bool _renderPreview;
    private readonly Func<SheetSnapshot,CancellationToken,BitmapSource> _previewRenderer;
    private CancellationTokenSource? _previewCancellation;
    private long _previewEpoch;
    private bool _selectingPreset,_disposed;
    internal Task PreviewTask { get; private set; }=Task.CompletedTask;

    public PhotoGridViewModel(PhotoGridRequest request,bool renderPreview=true,
        Func<SheetSnapshot,CancellationToken,BitmapSource>? previewRenderer=null)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request=request; _renderPreview=renderPreview; _previewRenderer=previewRenderer ?? RenderPreview;
        var count=request.PhotoIds.IsDefault ? 0 : request.PhotoIds.Distinct().Count(); repeat=count==1;
        columns=count<=1 ? 2 : Math.Min(10,(int)Math.Ceiling(Math.Sqrt(count)));
        rows=count<=1 ? 2 : Math.Min(10,(int)Math.Ceiling(count/(double)columns));
        selectedPreset=PresetFor(rows,columns); Rebuild();
    }

    public PhotoGridRequest Request { get; }
    public PhotoGridOptions Options=>new(Rows,Columns,GapMm,InsetMm,Scaling,Repeat);
    public PhotoGridPlan? Plan { get; private set; }
    public SheetSnapshot? DraftSheet { get; private set; }
    public bool CanApply=>!_disposed && Plan!=null && Error.Length==0 && PreviewError.Length==0;
    public bool CanRepeat=>!Request.PhotoIds.IsDefault && Request.PhotoIds.Distinct().Count()==1;
    public IReadOnlyList<Choice<string>> Presets { get; }=[new("2x2","2 × 2"),new("2x3","2 × 3"),new("3x3","3 × 3"),new("custom","Пользовательская")];
    public IReadOnlyList<Choice<ScaleMode>> ScaleChoices { get; }=[new(ScaleMode.Fit,"Вписать · без обрезки"),new(ScaleMode.Fill,"Заполнить · центральный кроп")];
    public bool ShowFillNotice=>Scaling==ScaleMode.Fill;
    public bool HasPreviewError=>PreviewError.Length!=0;
    public string Summary=>Plan is {} plan
        ? $"Фото в макете: {plan.Photos.Length} · ячеек: {plan.Cells.Length}. Копии печати задаются отдельно."
        : "Копии печати задаются отдельно от числа фото в макете.";
    public string PreviewStatus=>IsPreviewLoading ? "Обновляем предварительный вид…" : Preview!=null ? "Предварительный вид · до 120 DPI" : "Предварительный вид";
    public string RotationNotice=>"Сетка выпрямит свободный угол размещения; поворот на 90° сохраняется.";
    public string FillNotice=>"«Заполнить» обратимо обрежет края фото до пропорций ячейки. Исходник и аннотации сохраняются; кадрирование можно изменить после размещения.";

    [ObservableProperty] private int rows=2;
    [ObservableProperty] private int columns=2;
    [ObservableProperty] private double gapMm=3;
    [ObservableProperty] private double insetMm;
    [ObservableProperty] private ScaleMode scaling=ScaleMode.Fit;
    [ObservableProperty] private bool repeat;
    [ObservableProperty] private string selectedPreset="2x2";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApply))] private string error="";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPreviewError),nameof(CanApply))] private string previewError="";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PreviewStatus))] private bool isPreviewLoading;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PreviewStatus))] private BitmapSource? preview;

    private static string PresetFor(int rowCount,int columnCount)=>(rowCount,columnCount) switch {(2,2)=>"2x2",(2,3)=>"2x3",(3,3)=>"3x3",_=>"custom"};
    partial void OnRowsChanged(int value)=>DimensionsChanged();
    partial void OnColumnsChanged(int value)=>DimensionsChanged();
    partial void OnGapMmChanged(double value)=>Rebuild();
    partial void OnInsetMmChanged(double value)=>Rebuild();
    partial void OnScalingChanged(ScaleMode value) { OnPropertyChanged(nameof(ShowFillNotice)); Rebuild(); }
    partial void OnRepeatChanged(bool value)
    {
        if(value && !CanRepeat) { Repeat=false; return; }
        Rebuild();
    }
    partial void OnSelectedPresetChanged(string value)
    {
        if(_selectingPreset || value=="custom") return;
        var dimensions=value switch {"2x2"=>(2,2),"2x3"=>(2,3),"3x3"=>(3,3),_=>(Rows,Columns)};
        _selectingPreset=true;
        try { Rows=dimensions.Item1; Columns=dimensions.Item2; }
        finally { _selectingPreset=false; }
        Rebuild();
    }
    private void DimensionsChanged()
    {
        if(_selectingPreset) return;
        _selectingPreset=true;
        try { SelectedPreset=PresetFor(Rows,Columns); }
        finally { _selectingPreset=false; }
        Rebuild();
    }
    private void Rebuild()
    {
        if(_disposed || _selectingPreset) return;
        _previewCancellation?.Cancel(); ++_previewEpoch; PreviewError="";
        try
        {
            var plan=PhotoGridLayout.Create(Request,Options);
            var selected=plan.OriginalPhotoIds.ToHashSet(); var replacements=plan.Photos.ToDictionary(p=>p.Id);
            var objects=ImmutableArray.CreateBuilder<SheetObject>();
            foreach(var item in Request.Sheet.Objects)
            {
                if(selected.Contains(item.Id))
                {
                    if(plan.Repeat) objects.AddRange(plan.Photos);
                    else objects.Add(replacements[item.Id]);
                }
                else objects.Add(item);
            }
            Plan=plan; DraftSheet=Request.Sheet with { Objects=objects.ToImmutable() }; Error="";
            if(_renderPreview) SchedulePreview(DraftSheet,_previewEpoch);
            else IsPreviewLoading=false;
        }
        catch(Exception ex) when(ex is ArgumentException or InvalidOperationException)
        { Plan=null; DraftSheet=null; Error=ex.Message; IsPreviewLoading=false; Preview=null; }
        OnPropertyChanged(nameof(Options)); OnPropertyChanged(nameof(Plan)); OnPropertyChanged(nameof(DraftSheet)); OnPropertyChanged(nameof(CanApply)); OnPropertyChanged(nameof(Summary));
    }
    private void SchedulePreview(SheetSnapshot snapshot,long epoch)
    {
        _previewCancellation?.Dispose(); _previewCancellation=new(); IsPreviewLoading=true;
        PreviewTask=UpdatePreviewAsync(snapshot,epoch,_previewCancellation.Token);
    }
    private async Task UpdatePreviewAsync(SheetSnapshot snapshot,long epoch,CancellationToken token)
    {
        try
        {
            await Task.Delay(150,token);
            var image=await Task.Run(()=>_previewRenderer(snapshot,token),token);
            if(_disposed || token.IsCancellationRequested || epoch!=_previewEpoch) return;
            Preview=image; PreviewError="";
        }
        catch(OperationCanceledException) { }
        catch(Exception ex)
        {
            if(!_disposed && !token.IsCancellationRequested && epoch==_previewEpoch)
            { Preview=null; PreviewError="Не удалось построить предварительный вид: "+ex.Message+" Проверьте доступ к исходным фото."; }
        }
        finally { if(!_disposed && epoch==_previewEpoch) IsPreviewLoading=false; }
    }
    private static BitmapSource RenderPreview(SheetSnapshot snapshot,CancellationToken token)
    {
        using var image=new PageRasterizer(new PdfiumRenderer()).RenderSheet(snapshot,120,false,token,1000);
        token.ThrowIfCancellationRequested(); return BitmapBridge.ToWpf(image);
    }
    public void Dispose()
    {
        if(_disposed) return;
        _disposed=true; ++_previewEpoch; _previewCancellation?.Cancel(); _previewCancellation?.Dispose(); _previewCancellation=null;
        IsPreviewLoading=false; OnPropertyChanged(nameof(CanApply));
    }
}
