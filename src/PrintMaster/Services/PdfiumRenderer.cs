using PDFiumCore;
using PrintMaster.Models;
using System.Drawing;
using System.Drawing.Imaging;

namespace PrintMaster.Services;

public sealed class PdfiumRenderer
{
    private static readonly object Gate=new();
    private static bool _initialized;
    private static void Initialize() { if (_initialized) return; fpdfview.FPDF_InitLibrary(); _initialized=true; }

    public IReadOnlyList<SizeD> ReadPages(string path)
    {
        lock(Gate)
        {
            Initialize(); var document=fpdfview.FPDF_LoadDocument(path,null);
            if (document==null || document.__Instance==IntPtr.Zero) throw LoadError();
            try
            {
                var count=fpdfview.FPDF_GetPageCount(document);
                if(count<=0 || count>20000) throw new InvalidOperationException("PDF пуст или содержит более 20 000 страниц.");
                var result=new List<SizeD>(count);
                for(var i=0;i<count;i++)
                {
                    double width=0,height=0;
                    if(fpdfview.FPDF_GetPageSizeByIndex(document,i,ref width,ref height)==0 || width<=0 || height<=0)
                        throw new InvalidOperationException($"Не удалось прочитать размер страницы {i+1}.");
                    result.Add(new(width,height));
                }
                return result;
            }
            finally { fpdfview.FPDF_CloseDocument(document); }
        }
    }

    public Bitmap Render(string path,int index,int width,int height,int quarterTurns,bool forPrint,CancellationToken cancellationToken,RectD? destination=null)
    {
        BitmapLimits.Validate(width,height);
        lock(Gate)
        {
            cancellationToken.ThrowIfCancellationRequested(); Initialize();
            var document=fpdfview.FPDF_LoadDocument(path,null);
            if(document==null || document.__Instance==IntPtr.Zero) throw LoadError();
            try
            {
                var page=fpdfview.FPDF_LoadPage(document,index);
                if(page==null || page.__Instance==IntPtr.Zero) throw new InvalidOperationException($"PDF: ошибка страницы {index+1}.");
                try
                {
                    var bitmap=new Bitmap(width,height,PixelFormat.Format32bppArgb);
                    try
                    {
                        var data=bitmap.LockBits(new Rectangle(0,0,width,height),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
                        try
                        {
                            var native=fpdfview.FPDFBitmapCreateEx(width,height,(int)FPDFBitmapFormat.BGRA,data.Scan0,data.Stride);
                            if(native==null || native.__Instance==IntPtr.Zero) throw new OutOfMemoryException("PDFium не смог создать растр.");
                            try
                            {
                                fpdfview.FPDFBitmapFillRect(native,0,0,width,height,uint.MaxValue);
                                // FPDF_ANNOT | FPDF_PRINTING; the latter improves text rendering for print.
                                var bounds=destination ?? new RectD(0,0,width,height);
                                fpdfview.FPDF_RenderPageBitmap(native,page,checked((int)Math.Round(bounds.X)),checked((int)Math.Round(bounds.Y)),
                                    checked((int)Math.Ceiling(bounds.Width)),checked((int)Math.Ceiling(bounds.Height)),quarterTurns,1 | (forPrint ? 0x800 : 0));
                            }
                            finally { fpdfview.FPDFBitmapDestroy(native); }
                        }
                        finally { bitmap.UnlockBits(data); }
                        cancellationToken.ThrowIfCancellationRequested(); return bitmap;
                    }
                    catch { bitmap.Dispose(); throw; }
                }
                finally { fpdfview.FPDF_ClosePage(page); }
            }
            finally { fpdfview.FPDF_CloseDocument(document); }
        }
    }

    private static Exception LoadError() => new InvalidOperationException($"PDFium: не удалось открыть PDF (код {fpdfview.FPDF_GetLastError()}). Файл может быть поврежден или защищен паролем.");
}

public static class BitmapLimits
{
    // 600 DPI A4: ~35 million pixels, ~140 MB. Hard cap prevents exotic files exhausting RAM.
    public const long MaxPixels=55_000_000;
    public static void Validate(int width,int height)
    {
        if(width<=0 || height<=0 || width>30000 || height>30000 || (long)width*height>MaxPixels)
            throw new InvalidOperationException("Растр слишком большой. Используйте 300 DPI, меньший лист или менее узкий кроп (лимит 55 млн пикселей).");
    }
}
