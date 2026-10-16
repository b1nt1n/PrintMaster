using PrintMaster.Models;
using System.Collections.Immutable;

namespace PrintMaster.Services;

public sealed record NotebookTextResult(ImmutableArray<TextObject> Objects,int PrintedCharacters,string Remainder);

public static class NotebookTextLayout
{
    public static NotebookTextResult Layout(SheetPaper paper,NotebookTextStory story,RectD safe,IEnumerable<TextObject>? previous=null)
    {
        if(story.Id==Guid.Empty || story.Source.Length>TextLimits.MaxCharacters) throw new ArgumentException("Недопустимый текст тетради.");
        TextLayoutService.ValidateStyle(story.Style);
        var profile=paper.Notebook ?? throw new ArgumentException("Нет профиля тетради.");
        var regions=NotebookGeometry.TextRegions(paper,safe); var objects=ImmutableArray.CreateBuilder<TextObject>();
        var existing=(previous ?? []).Where(t=>t.Flow?.StoryId==story.Id).ToArray();
        var consumed=0;
        for(var part=0;part<regions.Count;part++)
        {
            var region=regions[part]; var bounds=region.NativeBounds;
            var style=story.Style with { FixedLineHeightMm=null };
            var natural=TextLayoutService.Layout("Ag",style,bounds.Width).LineHeightMm;
            if(profile.AlignTextToRuling)
            {
                var step=profile.IsGrid ? profile.GridHeightMm : profile.LineStepMm;
                var height=Math.Ceiling((natural-1e-8)/step)*step;
                style=style with { FixedLineHeightMm=height };
                var baseline=TextLayoutService.BaselineMm(style);
                var origin=profile.IsGrid ? profile.GridOriginYmm%step : profile.FirstLineMm;
                var row=origin+Math.Max(0,Math.Ceiling((bounds.Y+baseline-origin)/step))*step;
                var top=row-baseline;
                bounds=bounds with { Y=top,Height=Math.Max(0,bounds.Bottom-top) };
            }
            var remaining=story.Source[consumed..];
            var layout=TextLayoutService.Layout(remaining,style,Math.Max(0.1,bounds.Width),true);
            var capacity=Math.Max(0,(int)Math.Floor((bounds.Height-TextLayoutService.VerticalPaddingMm)/layout.LineHeightMm));
            var take=layout.WidthOverflow ? 0 : Math.Min(capacity,layout.Lines.Length);
            var length=take==0 ? 0 : layout.Lines[take-1].Start+layout.Lines[take-1].Length;
            var fragment=remaining[..length]; consumed+=length;
            var h=Math.Max(0.1,take*layout.LineHeightMm+TextLayoutService.VerticalPaddingMm); var center=NotebookGeometry.ToPaper(paper,new(bounds.X+bounds.Width/2,bounds.Y+h/2));
            var placement=new ObjectPlacement(new(center.X-bounds.Width/2,center.Y-h/2,bounds.Width,h),NotebookGeometry.IsRotated(paper) ? 90 : 0);
            objects.Add(new(existing.FirstOrDefault(t=>t.Flow!.PartIndex==part)?.Id ?? Guid.NewGuid(),placement,fragment,style,
                Flow:new(story.Id,part,part+1<regions.Count || consumed<story.Source.Length,true)));
        }
        return new(objects.ToImmutable(),consumed,story.Source[consumed..]);
    }
    public static string Remainder(SheetSnapshot sheet)
    {
        if(sheet.NotebookText is not {} story) return "";
        var count=sheet.Objects.OfType<TextObject>().Where(t=>t.Flow is { Notebook:true } link && link.StoryId==story.Id)
            .Sum(t=>t.Text.Length);
        return story.Source[Math.Clamp(count,0,story.Source.Length)..];
    }
}
