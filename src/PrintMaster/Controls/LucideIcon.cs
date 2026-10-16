using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Collections;
using System.Resources;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Xml.Linq;

namespace PrintMaster.Controls;

// Official SVG assets are bundled, never fetched while the app is running.
public sealed class LucideIcon : FrameworkElement
{
    private static readonly ConcurrentDictionary<string,Geometry[]> Cache=new();
    public static readonly DependencyProperty IconProperty=DependencyProperty.Register(nameof(Icon),typeof(string),typeof(LucideIcon),new FrameworkPropertyMetadata("printer",FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty=TextElement.ForegroundProperty.AddOwner(typeof(LucideIcon),new FrameworkPropertyMetadata(Brushes.Black,FrameworkPropertyMetadataOptions.AffectsRender|FrameworkPropertyMetadataOptions.Inherits));
    public string Icon { get=>(string)GetValue(IconProperty); set=>SetValue(IconProperty,value); }
    public Brush Foreground { get=>(Brush)GetValue(ForegroundProperty); set=>SetValue(ForegroundProperty,value); }
    public LucideIcon() { Width=20; Height=20; Focusable=false; IsHitTestVisible=false; }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var shapes=Cache.GetOrAdd(Icon,Load);
        var scale=Math.Min(ActualWidth,ActualHeight)/24;
        dc.PushTransform(new TranslateTransform((ActualWidth-24*scale)/2,(ActualHeight-24*scale)/2));
        dc.PushTransform(new ScaleTransform(scale,scale));
        var pen=new Pen(Foreground,2) { StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round };
        foreach(var shape in shapes) dc.DrawGeometry(null,pen,shape);
        dc.Pop(); dc.Pop();
    }
    private static Geometry[] Load(string name)
    {
        if(string.IsNullOrWhiteSpace(name) || name.Any(c=>!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c!='-')) throw new ArgumentException("Lucide names use kebab-case.");
        // A private reader avoids both Application/pack-URI lifetime coupling and
        // sharing a cached resource stream's position between rendering threads.
        using var bundle=typeof(LucideIcon).Assembly.GetManifestResourceStream("PrintMaster.g.resources")
            ?? throw new InvalidOperationException("Нет встроенных ресурсов PrintMaster.");
        using var reader=new ResourceReader(bundle);
        XDocument? svg=null;
        foreach(DictionaryEntry entry in reader)
            if(entry.Key is string key && key==$"assets/icons/{name}.svg" && entry.Value is Stream stream)
            {svg=XDocument.Load(stream);break;}
        if(svg==null) throw new InvalidOperationException($"Нет иконки Lucide {name}.");
        var result=new List<Geometry>();
        foreach(var node in svg.Root!.Elements())
        {
            double N(string attribute,double fallback=0)=>double.TryParse((string?)node.Attribute(attribute),NumberStyles.Float,CultureInfo.InvariantCulture,out var v) ? v : fallback;
            Geometry? geometry=node.Name.LocalName switch
            {
                "path"=>Geometry.Parse((string)node.Attribute("d")!),
                "line"=>new LineGeometry(new(N("x1"),N("y1")),new(N("x2"),N("y2"))),
                "circle"=>new EllipseGeometry(new(N("cx"),N("cy")),N("r"),N("r")),
                "ellipse"=>new EllipseGeometry(new(N("cx"),N("cy")),N("rx"),N("ry")),
                "rect"=>new RectangleGeometry(new(N("x"),N("y"),N("width"),N("height")),N("rx"),N("ry",N("rx"))),
                "polyline" or "polygon"=>Geometry.Parse("M"+(string)node.Attribute("points")!+(node.Name.LocalName=="polygon" ? "Z" : "")),
                _=>null
            };
            if(geometry!=null) { geometry.Freeze(); result.Add(geometry); }
        }
        return result.ToArray();
    }
}

