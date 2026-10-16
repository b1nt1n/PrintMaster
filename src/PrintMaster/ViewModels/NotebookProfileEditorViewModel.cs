using CommunityToolkit.Mvvm.ComponentModel;
using PrintMaster.Models;
using System.Globalization;

namespace PrintMaster.ViewModels;

public partial class NotebookProfileEditorViewModel : ObservableObject
{
    private readonly NotebookProfile _original;
    private readonly Dictionary<string,string> _numbers=new();
    public NotebookProfileEditorViewModel(NotebookProfile original,bool copy=false)
    {
        _original=copy ? original with { Id=Guid.NewGuid(),Name=original.Name+" — копия" } : original;
        name=_original.Name; singleMarginOnRight=original.SingleMarginOnRight;
        alignTextToRuling=original.AlignTextToRuling; showGuides=original.ShowGuides;
        foreach(var (key,value) in new[] { (nameof(WidthText),original.WidthMm),(nameof(HeightText),original.HeightMm),
            (nameof(GridWidthText),original.GridWidthMm),(nameof(GridHeightText),original.GridHeightMm),
            (nameof(OriginXText),original.GridOriginXmm),(nameof(OriginYText),original.GridOriginYmm),
            (nameof(LineStepText),original.LineStepMm),(nameof(FirstLineText),original.FirstLineMm),
            (nameof(LeftMarginText),original.LeftMarginMm),(nameof(RightMarginText),original.RightMarginMm),
            (nameof(FoldText),original.FoldMm),(nameof(FoldGapText),original.FoldGapMm),
            (nameof(TopText),original.WritingTopMm),(nameof(BottomText),original.WritingBottomMm) })
            _numbers[key]=value.ToString("0.###",CultureInfo.CurrentCulture);
        Validate();
    }
    [ObservableProperty] private string name="";
    [ObservableProperty] private bool singleMarginOnRight;
    [ObservableProperty] private bool alignTextToRuling;
    [ObservableProperty] private bool showGuides;
    public bool IsSpread=>_original.IsSpread;
    public bool IsSingle=>!IsSpread;
    public bool IsGrid=>_original.IsGrid;
    public bool IsRuled=>!IsGrid;
    public double ProfileWidth=>Number(nameof(WidthText));
    public double ProfileHeight=>Number(nameof(HeightText));
    public double FoldMaximum=>ProfileWidth-1;
    public double FoldGapMaximum=>ProfileWidth/2;
    public string Title=>NotebookProfiles.Choices[(int)_original.Variant].Label;
    public string Error { get; private set; }="";
    public bool CanApply=>Result!=null;
    public NotebookProfile? Result { get; private set; }
    private string Get(string key)=>_numbers.GetValueOrDefault(key,"");
    private void Set(string key,string value)
    { if(Get(key)==value) return; _numbers[key]=value; OnPropertyChanged(key); Validate(); }
    public string WidthText { get=>Get(nameof(WidthText)); set=>Set(nameof(WidthText),value); }
    public string HeightText { get=>Get(nameof(HeightText)); set=>Set(nameof(HeightText),value); }
    public string GridWidthText { get=>Get(nameof(GridWidthText)); set=>Set(nameof(GridWidthText),value); }
    public string GridHeightText { get=>Get(nameof(GridHeightText)); set=>Set(nameof(GridHeightText),value); }
    public string OriginXText { get=>Get(nameof(OriginXText)); set=>Set(nameof(OriginXText),value); }
    public string OriginYText { get=>Get(nameof(OriginYText)); set=>Set(nameof(OriginYText),value); }
    public string LineStepText { get=>Get(nameof(LineStepText)); set=>Set(nameof(LineStepText),value); }
    public string FirstLineText { get=>Get(nameof(FirstLineText)); set=>Set(nameof(FirstLineText),value); }
    public string LeftMarginText { get=>Get(nameof(LeftMarginText)); set=>Set(nameof(LeftMarginText),value); }
    public string RightMarginText { get=>Get(nameof(RightMarginText)); set=>Set(nameof(RightMarginText),value); }
    public string FoldText { get=>Get(nameof(FoldText)); set=>Set(nameof(FoldText),value); }
    public string FoldGapText { get=>Get(nameof(FoldGapText)); set=>Set(nameof(FoldGapText),value); }
    public string TopText { get=>Get(nameof(TopText)); set=>Set(nameof(TopText),value); }
    public string BottomText { get=>Get(nameof(BottomText)); set=>Set(nameof(BottomText),value); }
    partial void OnNameChanged(string value)=>Validate();
    partial void OnSingleMarginOnRightChanged(bool value)=>Validate();
    partial void OnAlignTextToRulingChanged(bool value)=>Validate();
    partial void OnShowGuidesChanged(bool value)=>Validate();
    private double Number(string key)=>double.TryParse(Get(key).Trim().Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out var v) ? v : double.NaN;
    private void Validate()
    {
        Result=null; Error="";
        try
        {
            var value=_original with { Name=Name.Trim(),WidthMm=Number(nameof(WidthText)),HeightMm=Number(nameof(HeightText)),
                GridWidthMm=Number(nameof(GridWidthText)),GridHeightMm=Number(nameof(GridHeightText)),
                GridOriginXmm=Number(nameof(OriginXText)),GridOriginYmm=Number(nameof(OriginYText)),
                LineStepMm=Number(nameof(LineStepText)),FirstLineMm=Number(nameof(FirstLineText)),
                LeftMarginMm=Number(nameof(LeftMarginText)),RightMarginMm=Number(nameof(RightMarginText)),
                FoldMm=Number(nameof(FoldText)),FoldGapMm=Number(nameof(FoldGapText)),WritingTopMm=Number(nameof(TopText)),WritingBottomMm=Number(nameof(BottomText)),
                SingleMarginOnRight=SingleMarginOnRight,AlignTextToRuling=AlignTextToRuling,ShowGuides=ShowGuides };
            value.Validate(); Result=value;
        }
        catch(ArgumentException ex) { Error=ex.Message; }
        OnPropertyChanged(nameof(Error)); OnPropertyChanged(nameof(CanApply));
        foreach(var name in new[] { nameof(ProfileWidth),nameof(ProfileHeight),nameof(FoldMaximum),nameof(FoldGapMaximum) }) OnPropertyChanged(name);
    }
}
