using PrintMaster.Models;

namespace PrintMaster.Services;

public sealed record NotebookTextRegion(RectD NativeBounds,ObjectPlacement Placement);
public sealed record NotebookGuide(PointD Start,PointD End,bool IsMargin=false,bool IsFold=false);

public static class NotebookGeometry
{
    public static bool IsRotated(SheetPaper paper)=>paper.Notebook is {} p && paper.Landscape!=p.IsSpread;
    public static PointD ToPaper(SheetPaper paper,PointD point)=>IsRotated(paper)
        ? new(paper.Notebook!.HeightMm-point.Y,point.X) : point;
    private static RectD ToNative(SheetPaper paper,RectD rect)=>IsRotated(paper)
        ? new(rect.Y,paper.Notebook!.HeightMm-rect.Right,rect.Height,rect.Width) : rect;
    public static IReadOnlyList<NotebookTextRegion> TextRegions(SheetPaper paper,RectD safe)
    {
        var p=paper.Notebook ?? throw new ArgumentException("Листу нужен профиль тетради."); p.Validate();
        var nativeSafe=ToNative(paper,safe); var top=Math.Max(p.WritingTopMm,nativeSafe.Y);
        var bottom=Math.Min(p.HeightMm-p.WritingBottomMm,nativeSafe.Bottom);
        var regions=new List<RectD>();
        if(p.IsSpread)
        {
            regions.Add(new(p.LeftMarginMm,top,p.FoldMm-p.FoldGapMm-p.LeftMarginMm,bottom-top));
            regions.Add(new(p.FoldMm+p.FoldGapMm,top,p.WidthMm-p.RightMarginMm-p.FoldMm-p.FoldGapMm,bottom-top));
        }
        else
        {
            var left=p.SingleMarginOnRight ? nativeSafe.X : Math.Max(nativeSafe.X,p.LeftMarginMm);
            var right=p.SingleMarginOnRight ? Math.Min(nativeSafe.Right,p.WidthMm-p.RightMarginMm) : nativeSafe.Right;
            regions.Add(new(left,top,right-left,bottom-top));
        }
        return regions.Select(r=>r.Intersect(nativeSafe)).Select(r=>
        {
            if(r.IsEmpty) throw new InvalidOperationException("В рабочей области тетради не осталось места для текста.");
            var center=ToPaper(paper,new(r.X+r.Width/2,r.Y+r.Height/2));
            return new NotebookTextRegion(r,new(new(center.X-r.Width/2,center.Y-r.Height/2,r.Width,r.Height),IsRotated(paper) ? 90 : 0));
        }).ToArray();
    }
    public static IEnumerable<NotebookGuide> Guides(SheetPaper paper)
    {
        if(paper.Notebook is not {} p || !p.ShowGuides) yield break;
        var step=p.IsGrid ? p.GridHeightMm : p.LineStepMm;
        var first=p.IsGrid ? p.GridOriginYmm%step : p.FirstLineMm;
        for(var y=first;y<p.HeightMm;y+=step) yield return Map(paper,new(0,y),new(p.WidthMm,y));
        if(p.IsGrid)
            for(var x=p.GridOriginXmm%p.GridWidthMm;x<p.WidthMm;x+=p.GridWidthMm)
                yield return Map(paper,new(x,0),new(x,p.HeightMm));
        if(p.IsSpread || !p.SingleMarginOnRight) yield return Map(paper,new(p.LeftMarginMm,0),new(p.LeftMarginMm,p.HeightMm),true);
        if(p.IsSpread || p.SingleMarginOnRight) yield return Map(paper,new(p.WidthMm-p.RightMarginMm,0),new(p.WidthMm-p.RightMarginMm,p.HeightMm),true);
        if(p.IsSpread) yield return Map(paper,new(p.FoldMm,0),new(p.FoldMm,p.HeightMm),false,true);
    }
    private static NotebookGuide Map(SheetPaper paper,PointD from,PointD to,bool margin=false,bool fold=false)
        =>new(ToPaper(paper,from),ToPaper(paper,to),margin,fold);
}
