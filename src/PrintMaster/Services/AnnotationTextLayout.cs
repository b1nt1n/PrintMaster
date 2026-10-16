using PrintMaster.Models;

namespace PrintMaster.Services;

public sealed record AnnotationTextPresentation(Guid ContentId,Annotation Annotation,TextObject Text,Affine SourceToPaper,double MmPerUnit);

public static class AnnotationTextLayout
{
    public static double LayoutScale(Annotation annotation)=>annotation.TextFormat?.LayoutMmPerUnit ?? 25.4/96;
    public static TextStyle Style(Annotation annotation,double? mmPerUnit=null)
    {
        var format=annotation.TextFormat ?? new TextAnnotationFormat();
        return new(format.FontFamily,annotation.FontSize*(mmPerUnit ?? LayoutScale(annotation))*72/25.4,annotation.Color,format.Bold,format.Italic,format.Alignment,format.LineSpacing);
    }
    // A virtual 96-DPI coordinate system allows the same line metrics as sheet text.
    public static TextObject LogicalText(Annotation annotation)
    {
        var mmPerUnit=LayoutScale(annotation);
        return new(annotation.Id,new(new(annotation.Start.X*mmPerUnit,annotation.Start.Y*mmPerUnit,
            Math.Max(.01,annotation.End.X-annotation.Start.X)*mmPerUnit,Math.Max(.01,annotation.End.Y-annotation.Start.Y)*mmPerUnit)),annotation.Text,Style(annotation));
    }
    public static Affine Transform(ContentObject content,RectD destination)
    {
        var raw=ImageGeometry.CreateTransform(content.Edits.Crop,content.Edits.QuarterTurns,destination);
        var angle=content.Placement.RotationDegrees*Math.PI/180; var c=Math.Cos(angle); var s=Math.Sin(angle);
        var cx=destination.X+destination.Width/2; var cy=destination.Y+destination.Height/2;
        return new(raw.M11*c-raw.M12*s,raw.M11*s+raw.M12*c,raw.M21*c-raw.M22*s,raw.M21*s+raw.M22*c,
            cx+(raw.OffsetX-cx)*c-(raw.OffsetY-cy)*s,cy+(raw.OffsetX-cx)*s+(raw.OffsetY-cy)*c);
    }
    public static AnnotationTextPresentation Present(ContentObject content,Annotation annotation,RectD destination)
    {
        if(annotation.Kind!=AnnotationKind.Text) throw new ArgumentException("Выберите текстовую аннотацию.");
        var transform=Transform(content,destination); var scale=Math.Sqrt(transform.M11*transform.M11+transform.M12*transform.M12);
        var width=Math.Max(.01,annotation.End.X-annotation.Start.X)*scale;
        var height=Math.Max(.01,annotation.End.Y-annotation.Start.Y)*scale;
        var center=transform.Transform(new((annotation.Start.X+annotation.End.X)/2,(annotation.Start.Y+annotation.End.Y)/2));
        var rotation=Math.Atan2(transform.M12,transform.M11)*180/Math.PI;
        var text=new TextObject(annotation.Id,new(new(center.X-width/2,center.Y-height/2,width,height),rotation),annotation.Text,Style(annotation,scale));
        return new(content.Id,annotation,text,transform,scale);
    }
    public static Annotation Update(Annotation annotation,string text,TextStyle style,double mmPerUnit,double? widthMm=null)
    {
        TextLayoutService.ValidateStyle(style);
        if(text.Length>TextLimits.MaxCharacters || !double.IsFinite(mmPerUnit) || mmPerUnit<=0) throw new ArgumentException("Недопустимый текст или масштаб.");
        var width=Math.Max(.01,widthMm ?? Math.Max(.01,annotation.End.X-annotation.Start.X)*mmPerUnit);
        var height=TextLayoutService.Layout(text,style,width).HeightMm;
        return annotation with { Text=text,Color=style.Color,FontSize=style.FontSizePt*25.4/72/mmPerUnit,
            End=new(annotation.Start.X+width/mmPerUnit,annotation.Start.Y+height/mmPerUnit),
            TextFormat=new(style.FontFamily,style.Bold,style.Italic,style.Alignment,style.LineSpacing,mmPerUnit) };
    }
    public static Annotation Create(PointD point,TextStyle style,double mmPerUnit,double widthMm)
        =>Update(Annotation.Create(AnnotationKind.Text,point,new(point.X+widthMm/mmPerUnit,point.Y+1),style.Color,1),"",style,mmPerUnit,widthMm);
    public static Annotation? HitText(ContentObject content,PointD point,RectD destination)
    {
        var source=Transform(content,destination).Inverse(point);
        if(!content.Edits.Crop.Contains(source)) return null;
        return content.Edits.Annotations.Reverse().FirstOrDefault(a=>a.Kind==AnnotationKind.Text && AnnotationLayout.VisualBounds(a).Contains(source));
    }
}
