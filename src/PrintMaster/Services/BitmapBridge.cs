using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace PrintMaster.Services;

public static class BitmapBridge
{
    public static BitmapSource ToWpf(Bitmap bitmap)
    {
        var data=bitmap.LockBits(new Rectangle(0,0,bitmap.Width,bitmap.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        try
        {
            var source=BitmapSource.Create(bitmap.Width,bitmap.Height,96,96,PixelFormats.Bgra32,null,
                data.Scan0,checked(data.Stride*bitmap.Height),data.Stride); source.Freeze(); return source;
        }
        finally { bitmap.UnlockBits(data); }
    }
    public static BitmapImage LoadPreview(string path,int maxPixelWidth)
    {
        using var stream=File.OpenRead(path); var image=new BitmapImage(); image.BeginInit();
        image.CacheOption=BitmapCacheOption.OnLoad; image.DecodePixelWidth=Math.Max(1,maxPixelWidth); image.StreamSource=stream;
        // StreamSource with OnLoad has no URI cache entry. IgnoreImageCache expects a URI.
        image.EndInit(); image.Freeze(); return image;
    }
    public static Bitmap ToGdi(BitmapSource source)
    {
        var converted=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0); converted.Freeze();
        var bitmap=new Bitmap(converted.PixelWidth,converted.PixelHeight,PixelFormat.Format32bppArgb);
        var data=bitmap.LockBits(new Rectangle(0,0,bitmap.Width,bitmap.Height),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
        try { converted.CopyPixels(System.Windows.Int32Rect.Empty,data.Scan0,checked(data.Stride*bitmap.Height),data.Stride); }
        catch { bitmap.UnlockBits(data); bitmap.Dispose(); throw; }
        bitmap.UnlockBits(data); return bitmap;
    }
}
