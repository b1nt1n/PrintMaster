using PrintMaster.Models;
using System.Drawing;
using System.IO;
using System.Windows.Media.Imaging;

namespace PrintMaster.Services;

public sealed class FileImportService(SessionWorkspace workspace,DocxConverter converter,PdfiumRenderer pdf)
{
    public Task<PrintPageItem> ImportClipboardImageAsync(BitmapSource image,CancellationToken token)
    {
        var frozen=ClipboardService.FreezeImage(image); var directory=workspace.NewImportDirectory(); var copy=Path.Combine(directory,"clipboard.png");
        return CompleteImportAsync(directory,Task.Run(()=>
        {
            token.ThrowIfCancellationRequested();
            using(var file=File.Open(copy,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(frozen)); encoder.Save(file); }
            using var source=Image.FromFile(copy); var dpiX=source.HorizontalResolution>10 ? source.HorizontalResolution : 96; var dpiY=source.VerticalResolution>10 ? source.VerticalResolution : 96;
            return PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Image,null,copy,0,1,new(1000,1000d*source.Height/source.Width),new(source.Width/dpiX*25.4,source.Height/dpiY*25.4)));
        },token));
    }
    public Task<SourceAsset> ImportClipboardMaterialAsync(ClipboardMaterial material,CancellationToken token)
    {
        var directory=workspace.NewImportDirectory(); var copy=Path.Combine(directory,"material"+material.Extension);
        return CompleteImportAsync(directory,Task.Run(()=>
        {
            token.ThrowIfCancellationRequested(); File.WriteAllBytes(copy,material.Bytes); var asset=material.Asset;
            if(asset.Kind==SourceKind.Image)
            {
                using var image=Image.FromFile(copy);
                if((long)image.Width*image.Height>ClipboardService.MaxImagePixels || Math.Abs(image.Width/(double)image.Height-asset.LogicalSize.Width/asset.LogicalSize.Height)>0.01) throw new InvalidDataException("Размеры изображения в копии не совпадают с материалом.");
            }
            else
            {
                var pages=pdf.ReadPages(copy);
                if(pages.Count!=asset.PageCount || asset.PageIndex>=pages.Count || Math.Abs(pages[asset.PageIndex].Width-asset.LogicalSize.Width)>0.2 || Math.Abs(pages[asset.PageIndex].Height-asset.LogicalSize.Height)>0.2) throw new InvalidDataException("Страницы PDF в копии не совпадают с материалом.");
            }
            return asset with { Id=Guid.NewGuid(),OriginalPath=null,WorkingPath=copy };
        },token));
    }
    public static readonly string[] Extensions=[".pdf",".docx",".png",".jpg",".jpeg",".bmp"];
    public async Task<IReadOnlyList<PrintPageItem>> ImportAsync(string path,CancellationToken cancellationToken,IProgress<ImportProgress>? progress=null)
    {
        path=Path.GetFullPath(path);
        var extension=Path.GetExtension(path).ToLowerInvariant();
        if(!Extensions.Contains(extension)) throw new ArgumentException("Поддерживаются PDF, DOCX, PNG, JPG и BMP.");
        var directory=workspace.NewImportDirectory(); var copy=Path.Combine(directory,"input"+extension);
        try
        {
            progress?.Report(new(ImportPhase.Copying,"Копирование исходного файла…"));
            await using(var source=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,81920,true))
            await using(var destination=new FileStream(copy,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,true))
                await source.CopyToAsync(destination,cancellationToken).ConfigureAwait(false);
            if(extension is ".pdf" or ".docx")
            {
                if(extension==".docx") progress?.Report(new(ImportPhase.ConvertingDocx,"Преобразование DOCX в PDF через LibreOffice…"));
                var working=extension==".docx" ? await converter.ConvertAsync(copy,directory,cancellationToken).ConfigureAwait(false) : copy;
                return await Task.Run(()=>
                {
                    progress?.Report(new(ImportPhase.ReadingPdfPages,"Чтение страниц PDF…"));
                    var pages=pdf.ReadPages(working); var items=new List<PrintPageItem>(pages.Count);
                    for(var i=0;i<pages.Count;i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested(); var size=pages[i];
                        items.Add(PrintPageItem.FromSource(new(Guid.NewGuid(),extension==".docx" ? SourceKind.Docx : SourceKind.Pdf,
                            path,working,i,pages.Count,size,new(size.Width/72*25.4,size.Height/72*25.4))));
                    }
                    return (IReadOnlyList<PrintPageItem>)items;
                },cancellationToken).ConfigureAwait(false);
            }
            return await Task.Run(()=>
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new(ImportPhase.DecodingImage,"Чтение изображения…"));
                copy=ImageImportOrientation.NormalizeOwnedImage(copy,cancellationToken);
                using var image=Image.FromFile(copy);
                if(image.Width<=0 || image.Height<=0 || (long)image.Width*image.Height>150_000_000)
                    throw new InvalidOperationException("Изображение слишком большое (лимит 150 млн пикселей).");
                var logicalHeight=1000.0*image.Height/image.Width;
                var dpiX=image.HorizontalResolution>10 ? image.HorizontalResolution : 96;
                var dpiY=image.VerticalResolution>10 ? image.VerticalResolution : 96;
                return (IReadOnlyList<PrintPageItem>)new[] { PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Image,path,copy,
                    0,1,new(1000,logicalHeight),new(image.Width/dpiX*25.4,image.Height/dpiY*25.4))) };
            },cancellationToken).ConfigureAwait(false);
        }
        catch { workspace.DiscardFailedImport(directory); throw; }
    }
    private async Task<T> CompleteImportAsync<T>(string directory,Task<T> operation)
    {
        try { return await operation.ConfigureAwait(false); }
        catch { workspace.DiscardFailedImport(directory); throw; }
    }
}
