using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace PrintMaster.Services;

/// <summary>Normalizes a newly imported, owned copy before any crop coordinates are created.</summary>
public static class ImageImportOrientation
{
    public static string NormalizeOwnedImage(string path,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var normalized=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,
            Path.GetFileNameWithoutExtension(path)+"-oriented-"+Guid.NewGuid().ToString("N")+".png");
        try
        {
            using(var image=(Bitmap)Image.FromFile(path))
            {
                const int orientationTag=0x112;
                if(!image.PropertyIdList.Contains(orientationTag))return path;
                var property=image.GetPropertyItem(orientationTag);
                if(property?.Type!=3 || property.Value is not {Length:>=2} value)return path;
                var orientation=value[0]|value[1]<<8;
                if(orientation is <2 or >8)return path;
                // Source-photo limits differ from a rendered print bitmap: a 64 MP
                // camera image may still be printed at A4 after downsampling.
                if(image.Width<=0 || image.Height<=0 || (long)image.Width*image.Height>150_000_000)
                    throw new InvalidOperationException("Изображение слишком большое (лимит 150 млн пикселей).");
                token.ThrowIfCancellationRequested();
                var xResolution=image.HorizontalResolution;var yResolution=image.VerticalResolution;
                image.RotateFlip(orientation switch
                {
                    2=>RotateFlipType.RotateNoneFlipX,3=>RotateFlipType.Rotate180FlipNone,
                    4=>RotateFlipType.Rotate180FlipX,5=>RotateFlipType.Rotate90FlipX,
                    6=>RotateFlipType.Rotate90FlipNone,7=>RotateFlipType.Rotate270FlipX,
                    _=>RotateFlipType.Rotate270FlipNone
                });
                image.RemovePropertyItem(orientationTag);
                if(float.IsFinite(xResolution) && xResolution>0 && float.IsFinite(yResolution) && yResolution>0)
                    image.SetResolution(orientation>=5 ? yResolution : xResolution,orientation>=5 ? xResolution : yResolution);
                token.ThrowIfCancellationRequested();image.Save(normalized,ImageFormat.Png);token.ThrowIfCancellationRequested();
            }
            // Never overwrite/reinterpret an existing project's raw crop coordinates.
            // The caller supplies only its new import copy; the user's original is retained.
            File.Delete(path);return normalized;
        }
        catch
        {
            if(File.Exists(normalized))File.Delete(normalized);
            throw;
        }
    }
}
