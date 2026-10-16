using PrintMaster.Models;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PrintMaster.Controls;

public sealed partial class AnnotationCanvas
{
    private void DrawStyledAnnotation(DrawingContext dc,Annotation a)
    {
        var color=(Color)ColorConverter.ConvertFromString(a.Color);
        if(a.Kind!=AnnotationKind.Text) color.A=(byte)Math.Round(255*a.EffectiveOpacity);
        var brush=new SolidColorBrush(color);
        var pen=new Pen(brush,a.Thickness) { StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round };
        SolidColorBrush? fill=null;
        if(a.Kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse && a.FillColor is {} fillColor)
        {
            var fillValue=(Color)ColorConverter.ConvertFromString(fillColor); fillValue.A=(byte)Math.Round(255*a.EffectiveOpacity);
            fill=new SolidColorBrush(fillValue);
        }
        var start=new Point(a.Start.X,a.Start.Y); var end=new Point(a.End.X,a.End.Y); var rect=ToRect(RectD.FromPoints(a.Start,a.End));
        switch(a.Kind)
        {
            case AnnotationKind.Arrow:
                dc.DrawLine(pen,start,end); var angle=Math.Atan2(end.Y-start.Y,end.X-start.X); var length=Math.Max(14,a.Thickness*4);
                dc.DrawLine(pen,end,new(end.X-length*Math.Cos(angle-0.5),end.Y-length*Math.Sin(angle-0.5)));
                dc.DrawLine(pen,end,new(end.X-length*Math.Cos(angle+0.5),end.Y-length*Math.Sin(angle+0.5))); break;
            case AnnotationKind.Rectangle: dc.DrawRectangle(fill,pen,rect); break;
            case AnnotationKind.Ellipse: dc.DrawEllipse(fill,pen,new(rect.X+rect.Width/2,rect.Y+rect.Height/2),rect.Width/2,rect.Height/2); break;
            case AnnotationKind.Text:
                if(a.TextFormat!=null) { DrawFormattedAnnotation(dc,a,brush); break; }
                var text=new FormattedText(a.Text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),a.FontSize,brush,VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(text,start); break;
            default:
                if(a.Points.Count==0) break;
                if(a.Points.Count==1){dc.DrawEllipse(brush,null,new(a.Points[0].X,a.Points[0].Y),a.Thickness/2,a.Thickness/2);break;}
                var geometry=new StreamGeometry(); using(var context=geometry.Open())
                { context.BeginFigure(new(a.Points[0].X,a.Points[0].Y),false,false); context.PolyLineTo(a.Points.Skip(1).Select(p=>new Point(p.X,p.Y)).ToArray(),true,false); }
                dc.DrawGeometry(null,pen,geometry); break;
        }
    }
}
