using PrintMaster.Models;
using System.Drawing;
using System.Drawing.Imaging;

namespace PrintMaster.Services;

public static class MonochromeRasterizer
{
    public static MonochromeMode Resolve(MonochromeMode requested,SourceKind source)
        => requested==MonochromeMode.Auto ? source==SourceKind.Image ? MonochromeMode.Photo : MonochromeMode.Text : requested;

    // In-place processing; only two error rows are allocated, never a full float image.
    public static unsafe void Apply(Bitmap bitmap,MonochromeMode mode,int threshold,CancellationToken cancellationToken)
    {
        if(mode==MonochromeMode.Auto) throw new ArgumentException("Автоматический режим нужно разрешить для конкретной страницы.");
        if(bitmap.PixelFormat!=PixelFormat.Format32bppArgb) throw new ArgumentException("Нужен 32bpp ARGB растр.");
        var width=bitmap.Width; var height=bitmap.Height;
        var data=bitmap.LockBits(new Rectangle(0,0,width,height),ImageLockMode.ReadWrite,PixelFormat.Format32bppArgb);
        try
        {
            var current=mode==MonochromeMode.Photo ? new float[width+2] : Array.Empty<float>();
            var next=mode==MonochromeMode.Photo ? new float[width+2] : Array.Empty<float>();
            for(var y=0;y<height;y++)
            {
                if((y&31)==0) cancellationToken.ThrowIfCancellationRequested();
                var row=(byte*)data.Scan0+y*data.Stride;
                var reverse=mode==MonochromeMode.Photo && y%2!=0;
                var direction=reverse ? -1 : 1;
                for(var x=reverse ? width-1 : 0;x>=0 && x<width;x+=direction)
                {
                    var p=row+x*4;
                    var alpha=p[3]/255f;
                    var gray=(0.2126f*p[2]+0.7152f*p[1]+0.0722f*p[0])*alpha+255*(1-alpha);
                    float old=gray;
                    if(mode==MonochromeMode.Photo) old+=current[x+1];
                    // Fixed midpoint for photos; threshold slider only applies to sharp text mode.
                    var value=(byte)(old >= (mode==MonochromeMode.Photo ? 128 : threshold) ? 255 : 0);
                    p[0]=p[1]=p[2]=value; p[3]=255;
                    if(mode!=MonochromeMode.Photo) continue;
                    var error=old-value; var i=x+1;
                    current[i+direction]+=error*7/16;
                    next[i-direction]+=error*3/16; next[i]+=error*5/16; next[i+direction]+=error/16;
                }
                if(mode==MonochromeMode.Photo) { (current,next)=(next,current); Array.Clear(next); }
            }
        }
        finally { bitmap.UnlockBits(data); }
    }
}
