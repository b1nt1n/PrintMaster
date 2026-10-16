using PrintMaster.Models;
using System.Drawing;

namespace PrintMaster.Services;

// Samples the color scene, independently of display zoom, editor guides and print monochrome.
public sealed class ProjectColorSampler
{
    private const int PreviewDpi=120;
    private const int MaxSide=1600;
    private readonly PageRasterizer _rasterizer;

    public ProjectColorSampler(PageRasterizer? rasterizer=null)=>_rasterizer=rasterizer ?? new(new());

    public string SampleSheet(SheetSnapshot sheet,PointD locationMm,CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(sheet);
        sheet.Paper.Validate(); var paper=sheet.Paper.OrientedSizeMm;
        RequirePoint(locationMm,new(0,0,paper.Width,paper.Height),nameof(locationMm));
        using var bitmap=_rasterizer.RenderSheet(sheet,PreviewDpi,false,token,MaxSide);
        token.ThrowIfCancellationRequested();
        return Pixel(bitmap,locationMm,new(paper.Width,paper.Height));
    }

    public string SampleSource(PageSnapshot page,PointD sourcePoint,CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(page);
        if(page.Kind!=SourceKind.Image || page.IsComposed)
            throw new InvalidOperationException("Цвет исходника можно взять только на отдельном изображении.");
        var logical=new SizeD(page.SourceWidth,page.SourceHeight);
        SheetValidation.Size(logical,nameof(page));
        SheetValidation.Size(new(page.WidthMm,page.HeightMm),nameof(page));
        var whole=new RectD(0,0,logical.Width,logical.Height); var crop=page.Edits.Crop;
        if(!double.IsFinite(crop.X) || !double.IsFinite(crop.Y) || !double.IsFinite(crop.Width) || !double.IsFinite(crop.Height)
            || crop.IsEmpty || crop.X<0 || crop.Y<0 || crop.Right>whole.Right || crop.Bottom>whole.Bottom)
            throw new ArgumentException("Область кадрирования выходит за исходное изображение.",nameof(page));
        RequirePoint(sourcePoint,whole,nameof(sourcePoint)); RequirePoint(sourcePoint,crop,nameof(sourcePoint));

        // Keep the immutable scene intact: the incoming point is in original source coordinates,
        // already mapped by the canvas through its crop/rotation transform.
        var fullSource=page with { Sheet=null,RotationQuarterTurns=0,Edits=new(whole,page.Edits.Annotations) };
        var scale=Math.Min(PreviewDpi/25.4*page.WidthMm/logical.Width,
            PreviewDpi/25.4*page.HeightMm/logical.Height);
        scale=Math.Min(scale,MaxSide/Math.Max(logical.Width,logical.Height));
        var width=Math.Max(1,Math.Min(MaxSide,(int)Math.Ceiling(logical.Width*scale)));
        var height=Math.Max(1,Math.Min(MaxSide,(int)Math.Ceiling(logical.Height*scale)));
        using var bitmap=_rasterizer.Render(fullSource,width,height,false,token);
        token.ThrowIfCancellationRequested();
        return Pixel(bitmap,sourcePoint,logical);
    }

    private static void RequirePoint(PointD point,RectD bounds,string name)
    {
        if(!double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X<bounds.X || point.Y<bounds.Y
            || point.X>=bounds.Right || point.Y>=bounds.Bottom)
            throw new ArgumentOutOfRangeException(name,"Точка находится за пределами видимого содержимого.");
    }

    private static string Pixel(Bitmap bitmap,PointD point,SizeD size)
    {
        var x=Math.Clamp((int)(point.X/size.Width*bitmap.Width),0,bitmap.Width-1);
        var y=Math.Clamp((int)(point.Y/size.Height*bitmap.Height),0,bitmap.Height-1);
        var color=bitmap.GetPixel(x,y); var a=color.A;
        // White paper is the background even if a future renderer returns an alpha pixel.
        var r=(color.R*a+255*(255-a)+127)/255;
        var g=(color.G*a+255*(255-a)+127)/255;
        var b=(color.B*a+255*(255-a)+127)/255;
        return $"#{r:X2}{g:X2}{b:X2}";
    }
}
