using System.Collections.Immutable;

namespace PrintMaster.Models;

public enum NotebookVariant { GridSingle,GridSpread,RuledSingle,RuledSpread }

// A value snapshot belongs to a sheet. Updating the catalog never changes that sheet.
public sealed record NotebookProfile
{
    public Guid Id { get; init; }=Guid.NewGuid();
    public string Name { get; init; }="Моя тетрадь";
    public NotebookVariant Variant { get; init; }
    public double WidthMm { get; init; }=167;
    public double HeightMm { get; init; }=208;
    public double GridWidthMm { get; init; }=5;
    public double GridHeightMm { get; init; }=5;
    public double GridOriginXmm { get; init; }
    public double GridOriginYmm { get; init; }
    public double LineStepMm { get; init; }=8;
    public double FirstLineMm { get; init; }=14;
    public bool SingleMarginOnRight { get; init; }=true;
    public double LeftMarginMm { get; init; }=20;
    public double RightMarginMm { get; init; }=20;
    public double FoldMm { get; init; }=167;
    public double FoldGapMm { get; init; }=5;
    public double WritingTopMm { get; init; }=5;
    public double WritingBottomMm { get; init; }=5;
    public bool AlignTextToRuling { get; init; }=true;
    public bool ShowGuides { get; init; }=true;
    public bool IsSpread=>Variant is NotebookVariant.GridSpread or NotebookVariant.RuledSpread;
    public bool IsGrid=>Variant is NotebookVariant.GridSingle or NotebookVariant.GridSpread;
    public SizeD NativeSize=>new(WidthMm,HeightMm);
    public SizeD FeedSize=>IsSpread ? new(HeightMm,WidthMm) : NativeSize;
    public SheetPaper ToPaper(SheetMargins? margins=null,bool? landscape=null)
    { Validate(); return new(FeedSize,landscape ?? IsSpread,margins ?? SheetMargins.SafeDefault) { Notebook=this }; }
    public void Validate()
    {
        if(Id==Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length>80 || !Enum.IsDefined(Variant))
            throw new ArgumentException("Укажите название и вид профиля тетради.");
        Check(WidthMm,25,1000,"Ширина листа"); Check(HeightMm,25,1000,"Высота листа");
        Check(GridWidthMm,1,50,"Ширина клетки"); Check(GridHeightMm,1,50,"Высота клетки");
        Check(LineStepMm,1,100,"Шаг строки"); Check(FirstLineMm,0,HeightMm,"Первая строка");
        Check(GridOriginXmm,0,WidthMm,"Начало сетки по X"); Check(GridOriginYmm,0,HeightMm,"Начало сетки по Y");
        Check(LeftMarginMm,0,WidthMm,"Левое поле"); Check(RightMarginMm,0,WidthMm,"Правое поле");
        Check(WritingTopMm,0,HeightMm,"Отступ сверху"); Check(WritingBottomMm,0,HeightMm,"Отступ снизу");
        Check(FoldGapMm,0,WidthMm/2,"Отступ от сгиба");
        if(WritingTopMm+WritingBottomMm>=HeightMm-1) throw new ArgumentException("Отступы занимают всю высоту листа.");
        if(IsSpread)
        {
            Check(FoldMm,1,WidthMm-1,"Положение сгиба");
            if(LeftMarginMm+FoldGapMm>=FoldMm-1 || RightMarginMm+FoldGapMm>=WidthMm-FoldMm-1)
                throw new ArgumentException("Поля и сгиб не оставляют места для текста.");
        }
        else if((SingleMarginOnRight ? RightMarginMm : LeftMarginMm)>=WidthMm-6)
            throw new ArgumentException("Красное поле занимает весь лист.");
    }
    private static void Check(double value,double minimum,double maximum,string label)
    { if(!double.IsFinite(value) || value<minimum || value>maximum) throw new ArgumentException($"{label}: допустимо {minimum:g}–{maximum:g} мм."); }
}

public static class NotebookProfiles
{
    public static IReadOnlyList<Choice<NotebookVariant>> Choices { get; }=
    [new(NotebookVariant.GridSingle,"В клетку — одинарный"),new(NotebookVariant.GridSpread,"В клетку — двойной"),
     new(NotebookVariant.RuledSingle,"В линейку — одинарный"),new(NotebookVariant.RuledSpread,"В линейку — двойной")];
    public static ImmutableArray<NotebookProfile> Defaults { get; }=Enumerable.Range(0,4).Select(i=>new NotebookProfile
    {
        Id=Guid.Parse($"b29d2000-6105-4000-8000-{i+1:000000000000}"),Variant=(NotebookVariant)i,
        Name=Choices[i].Label+" · начальный",WidthMm=i%2==1 ? 334 : 167
    }).ToImmutableArray();
    public static NotebookProfile Default(NotebookVariant variant)=>Defaults[(int)variant];
}

public sealed record NotebookTextStory(Guid Id,string Source,TextStyle Style);

public sealed record PrinterPaperCalibration(string PrinterName,SizeD PaperSizeMm,bool Landscape,
    double OffsetXmm=0,double OffsetYmm=0,bool OutputFaceDown=true,int BackRotationDegrees=0)
{
    public bool Matches(string name,SheetPaper paper)=>string.Equals(PrinterName,name,StringComparison.OrdinalIgnoreCase)
        && PaperSizeMm==paper.SizeMm && Landscape==paper.Landscape;
    public void Validate()
    {
        if(string.IsNullOrWhiteSpace(PrinterName) || !double.IsFinite(OffsetXmm) || !double.IsFinite(OffsetYmm)
            || Math.Abs(OffsetXmm)>30 || Math.Abs(OffsetYmm)>30 || BackRotationDegrees is not (0 or 180))
            throw new ArgumentException("Недопустимая калибровка бумаги.");
        SheetValidation.Size(PaperSizeMm,nameof(PaperSizeMm));
    }
}
