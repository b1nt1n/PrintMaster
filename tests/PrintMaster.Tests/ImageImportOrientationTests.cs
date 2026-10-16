using PrintMaster.Services;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using Xunit;

namespace PrintMaster.Tests;

public sealed class ImageImportOrientationTests
{
    [Theory]
    [InlineData(2,1,0,3,2,false)] [InlineData(3,3,2,1,0,false)] [InlineData(4,2,3,0,1,false)]
    [InlineData(5,0,2,1,3,false)] [InlineData(6,2,0,3,1,false)] [InlineData(7,3,1,2,0,false)] [InlineData(8,1,3,0,2,false)]
    [InlineData(2,1,0,3,2,true)] [InlineData(3,3,2,1,0,true)] [InlineData(4,2,3,0,1,true)]
    [InlineData(5,0,2,1,3,true)] [InlineData(6,2,0,3,1,true)] [InlineData(7,3,1,2,0,true)] [InlineData(8,1,3,0,2,true)]
    public void NormalizeOwnedImage_CameraOrientation_NormalizesPixelsDpiAndRemovesTag(int orientation,int topLeft,int topRight,int bottomLeft,int bottomRight,bool bigEndian)
    {
        var directory=Directory.CreateTempSubdirectory("PrintMaster-exif-").FullName;
        try
        {
            var original=Path.Combine(directory,"camera-original.jpg"); CreateJpeg(original,(ushort)orientation,bigEndian);
            var hash=SHA256.HashData(File.ReadAllBytes(original));
            var owned=Path.Combine(directory,"owned-copy.jpg"); File.Copy(original,owned);

            var normalized=ImageImportOrientation.NormalizeOwnedImage(owned,default);

            Assert.NotEqual(owned,normalized); Assert.Equal(".png",Path.GetExtension(normalized));
            Assert.False(File.Exists(owned)); Assert.Equal(hash,SHA256.HashData(File.ReadAllBytes(original)));
            using var image=new Bitmap(normalized); var swaps=orientation>=5;
            Assert.Equal(swaps ? 80 : 120,image.Width); Assert.Equal(swaps ? 120 : 80,image.Height);
            Assert.InRange(image.HorizontalResolution,swaps ? 149.98f : 299.98f,swaps ? 150.02f : 300.02f);
            Assert.InRange(image.VerticalResolution,swaps ? 299.98f : 149.98f,swaps ? 300.02f : 150.02f);
            Assert.DoesNotContain(0x112,image.PropertyIdList);
            var expected=new[]{topLeft,topRight,bottomLeft,bottomRight};
            var samples=new[]{image.GetPixel(image.Width/4,image.Height/4),image.GetPixel(image.Width*3/4,image.Height/4),
                image.GetPixel(image.Width/4,image.Height*3/4),image.GetPixel(image.Width*3/4,image.Height*3/4)};
            for(var index=0;index<samples.Length;index++)
            {
                var color=Colors[expected[index]]; var pixel=samples[index];
                Assert.True(Math.Abs(color.R-pixel.R)+Math.Abs(color.G-pixel.G)+Math.Abs(color.B-pixel.B)<30,
                    $"EXIF {orientation}, quadrant {index}: expected {color}, got {pixel}");
            }
            // The preview sees the same normalized coordinate system as GDI printing.
            var preview=BitmapBridge.LoadPreview(normalized,image.Width);
            Assert.Equal(image.Width,preview.PixelWidth); Assert.Equal(image.Height,preview.PixelHeight);
        }
        finally {Directory.Delete(directory,true);}
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(9)]
    public void NormalizeOwnedImage_NoUsableRotation_PreservesOriginalEncoding(int orientation)
    {
        var directory=Directory.CreateTempSubdirectory("PrintMaster-exif-").FullName;
        try
        {
            var path=Path.Combine(directory,"owned.jpg"); CreateJpeg(path,(ushort)orientation);
            var bytes=File.ReadAllBytes(path);
            Assert.Equal(path,ImageImportOrientation.NormalizeOwnedImage(path,default));
            Assert.Equal(bytes,File.ReadAllBytes(path)); Assert.Single(Directory.GetFiles(directory));
        }
        finally {Directory.Delete(directory,true);}
    }

    [Fact]
    public void NormalizeOwnedImage_Cancelled_PreservesOwnedInputWithoutPartialOutput()
    {
        var directory=Directory.CreateTempSubdirectory("PrintMaster-exif-").FullName;
        try
        {
            var path=Path.Combine(directory,"owned.jpg"); CreateJpeg(path,6); var bytes=File.ReadAllBytes(path);
            using var cancellation=new CancellationTokenSource(); cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(()=>ImageImportOrientation.NormalizeOwnedImage(path,cancellation.Token));
            Assert.Equal(bytes,File.ReadAllBytes(path)); Assert.Single(Directory.GetFiles(directory));
        }
        finally {Directory.Delete(directory,true);}
    }

    private static readonly Color[] Colors=[Color.Red,Color.Lime,Color.Blue,Color.Yellow];
    internal static void CreateJpeg(string path,ushort orientation,bool bigEndian=false)
    {
        byte[] encoded;
        using(var image=new Bitmap(120,80))
        {
            image.SetResolution(300,150);
            using(var graphics=Graphics.FromImage(image))
                for(var i=0;i<Colors.Length;i++)
                {using var brush=new SolidBrush(Colors[i]);graphics.FillRectangle(brush,i%2*60,i/2*40,60,40);}
            using var stream=new MemoryStream(); image.Save(stream,ImageFormat.Jpeg); encoded=stream.ToArray();
        }
        if(orientation==0){File.WriteAllBytes(path,encoded);return;}
        byte[] exif=[0xff,0xe1,0,34,(byte)'E',(byte)'x',(byte)'i',(byte)'f',0,0,
            (byte)'I',(byte)'I',42,0,8,0,0,0,1,0,0x12,1,3,0,1,0,0,0,(byte)orientation,0,0,0,0,0,0,0];
        if(bigEndian)exif=[0xff,0xe1,0,34,(byte)'E',(byte)'x',(byte)'i',(byte)'f',0,0,
            (byte)'M',(byte)'M',0,42,0,0,0,8,0,1,1,0x12,0,3,0,0,0,1,0,(byte)orientation,0,0,0,0,0,0];
        using var output=File.Create(path); output.Write(encoded.AsSpan(0,2)); output.Write(exif); output.Write(encoded.AsSpan(2));
    }
}
