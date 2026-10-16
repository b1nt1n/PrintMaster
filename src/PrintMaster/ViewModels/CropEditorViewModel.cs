using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrintMaster.Models;
using PrintMaster.Services;
using System.ComponentModel;
using System.Globalization;

namespace PrintMaster.ViewModels;

// This page belongs only to the dialog. The owner's document changes once, on Apply.
public sealed partial class CropEditorViewModel : ObservableObject,IDisposable
{
    private readonly SizeD _sourceSize;
    private SizeD _pixelSize;
    public SizeD PixelSize=>_pixelSize;
    public PrintPageItem DraftPage { get; }
    public CropEditorViewModel(SourceAsset asset,ContentEdits edits)
    {
        _sourceSize=asset.LogicalSize;
        _pixelSize=_sourceSize;
        DraftPage=new(SheetDocument.FromSource(asset,edits:edits));
        DraftPage.Edits.PropertyChanged+=DraftChanged;
    }
    public IReadOnlyList<Choice<double>> Ratios { get; }=[new(0,"Свободные пропорции"),new(1,"1 : 1"),new(4d/3,"4 : 3"),new(3d/2,"3 : 2"),new(16d/9,"16 : 9"),new(210d/297,"A4 портрет"),new(297d/210,"A4 альбом")];
    [ObservableProperty] private double ratio;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApply))] private bool isLoading=true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanApply))] private string error="";
    public bool CanApply=>!IsLoading && Error.Length==0;
    public RectD ResultCrop=>DraftPage.Edits.Crop;
    public RectD OrientedCrop=>CropOrientation.ToOriented(ResultCrop,_sourceSize,DraftPage.RotationQuarterTurns);
    public RectD ImageBounds { get { var size=_sourceSize.Rotated(DraftPage.RotationQuarterTurns); return new(0,0,size.Width,size.Height); } }
    private SizeD VisiblePixels=>_pixelSize.Rotated(DraftPage.RotationQuarterTurns);
    private double ScaleX=>VisiblePixels.Width/ImageBounds.Width;
    private double ScaleY=>VisiblePixels.Height/ImageBounds.Height;
    private double MinimumSize=>Math.Min(1,Math.Min(1/ScaleX,1/ScaleY));
    public double LogicalRatio=>Ratio*ScaleY/ScaleX;
    public double XMaximum=>Math.Max(0,ImageBounds.Width-OrientedCrop.Width)*ScaleX;
    public double YMaximum=>Math.Max(0,ImageBounds.Height-OrientedCrop.Height)*ScaleY;
    public double WidthMaximum=>(LogicalRatio>0 ? Math.Min(ImageBounds.Width-OrientedCrop.X,(ImageBounds.Height-OrientedCrop.Y)*LogicalRatio) : ImageBounds.Width-OrientedCrop.X)*ScaleX;
    public double HeightMaximum=>(LogicalRatio>0 ? Math.Min(ImageBounds.Height-OrientedCrop.Y,(ImageBounds.Width-OrientedCrop.X)/LogicalRatio) : ImageBounds.Height-OrientedCrop.Y)*ScaleY;
    public string XText { get=>Number(OrientedCrop.X*ScaleX); set=>SetNumber(value,0); }
    public string YText { get=>Number(OrientedCrop.Y*ScaleY); set=>SetNumber(value,1); }
    public string WidthText { get=>Number(OrientedCrop.Width*ScaleX); set=>SetNumber(value,2); }
    public string HeightText { get=>Number(OrientedCrop.Height*ScaleY); set=>SetNumber(value,3); }
    public string Summary=>$"Область {OrientedCrop.Width*ScaleX:0.###} × {OrientedCrop.Height*ScaleY:0.###} px · исходник {VisiblePixels.Width:0.###} × {VisiblePixels.Height:0.###} px";
    public void SetPixelSize(SizeD size)
    {
        if(!double.IsFinite(size.Width) || !double.IsFinite(size.Height) || size.Width<=0 || size.Height<=0) throw new ArgumentException("Неверный размер изображения.");
        _pixelSize=size; OnPropertyChanged(nameof(PixelSize)); RefreshGeometry();
    }
    private static string Number(double value)=>value.ToString("G",CultureInfo.InvariantCulture);
    private void SetNumber(string text,int field)
    {
        if(!NumericInputRules.TryParse(text,out var value) || !double.IsFinite(value)) return;
        value/=field is 0 or 2 ? ScaleX : ScaleY;
        var rect=OrientedCrop;
        var next=field switch
        {
            0=>rect with { X=value },1=>rect with { Y=value },
            2=>CropSelectionGeometry.ResizeTo(rect,value,rect.Height,ImageBounds,LogicalRatio,true,MinimumSize),
            _=>CropSelectionGeometry.ResizeTo(rect,rect.Width,value,ImageBounds,LogicalRatio,false,MinimumSize)
        };
        var actual=field switch {0=>next.X,1=>next.Y,2=>next.Width,_=>next.Height};
        if(Math.Abs(actual-value)>Math.Max(1,Math.Abs(value))*1e-10 || next.IsEmpty || next.X<0 || next.Y<0 || next.Right>ImageBounds.Right+1e-8 || next.Bottom>ImageBounds.Bottom+1e-8) return;
        SetOrientedCrop(next);
    }
    private void SetOrientedCrop(RectD crop)=>DraftPage.Edits.SetCrop(CropOrientation.ToSource(crop,_sourceSize,DraftPage.RotationQuarterTurns).Intersect(new(0,0,_sourceSize.Width,_sourceSize.Height)));
    partial void OnRatioChanged(double value)
    {
        if(double.IsFinite(value) && value>0) SetOrientedCrop(CropSelectionGeometry.FitRatio(OrientedCrop,LogicalRatio,ImageBounds,MinimumSize));
        RefreshGeometry();
    }
    [RelayCommand] private void Reset()
    {
        Ratio=0; DraftPage.Edits.SetCrop(new(0,0,_sourceSize.Width,_sourceSize.Height)); RefreshGeometry();
    }
    private void DraftChanged(object? sender,PropertyChangedEventArgs e)=>RefreshGeometry();
    private void RefreshGeometry()
    {
        // Local Undo may restore a frame from before the ratio choice. Keep that frame exactly.
        if(Ratio>0 && Math.Abs(OrientedCrop.Width/OrientedCrop.Height-LogicalRatio)>Math.Max(1,LogicalRatio)*1e-8) Ratio=0;
        foreach(var name in new[] {nameof(XText),nameof(YText),nameof(WidthText),nameof(HeightText),nameof(XMaximum),nameof(YMaximum),nameof(WidthMaximum),nameof(HeightMaximum),nameof(Summary),nameof(ResultCrop),nameof(OrientedCrop),nameof(LogicalRatio)}) OnPropertyChanged(name);
    }
    public void Dispose()=>DraftPage.Edits.PropertyChanged-=DraftChanged;
}
