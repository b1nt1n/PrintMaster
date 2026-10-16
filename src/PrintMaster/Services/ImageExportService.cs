using PrintMaster.Models;
using System.Drawing.Imaging;
using System.IO;

namespace PrintMaster.Services;

public sealed class ImageExportService(PageRasterizer rasterizer)
{
    public Task<string> SaveCopyAsync(PageSnapshot page,int dpi,CancellationToken cancellationToken,string? chosenOutput=null) => Task.Run(()=>
    {
        if(page.Kind!=SourceKind.Image) throw new ArgumentException("Сохранение правок доступно для изображений.");
        if(chosenOutput!=null && !string.IsNullOrEmpty(page.OriginalPath) && string.Equals(Path.GetFullPath(chosenOutput),Path.GetFullPath(page.OriginalPath),StringComparison.OrdinalIgnoreCase))
            throw new IOException("Копию нельзя сохранять поверх исходного изображения.");
        if(dpi is not (300 or 600)) throw new ArgumentException("DPI: 300 или 600.");
        var physical=new SizeD(page.WidthMm*page.Edits.Crop.Width/page.SourceWidth,page.HeightMm*page.Edits.Crop.Height/page.SourceHeight).Rotated(page.RotationQuarterTurns);
        var width=checked((int)Math.Round(physical.Width/25.4*dpi)); var height=checked((int)Math.Round(physical.Height/25.4*dpi));
        // A source-image copy retains the established meaning: crop/annotations/rotation,
        // without paper layout. Full sheet export uses ExportSheetAsync below.
        using var bitmap=rasterizer.Render(page with { Sheet=null },Math.Max(1,width),Math.Max(1,height),false,cancellationToken); bitmap.SetResolution(dpi,dpi);
        if(chosenOutput!=null)
        {
            var selectedFormat=Path.GetExtension(chosenOutput).ToLowerInvariant() switch { ".jpg" or ".jpeg"=>ImageFormat.Jpeg,".bmp"=>ImageFormat.Bmp,_=>ImageFormat.Png };
            var temp=chosenOutput+"."+Guid.NewGuid().ToString("N")+".tmp";
            try { bitmap.Save(temp,selectedFormat); cancellationToken.ThrowIfCancellationRequested(); File.Move(temp,chosenOutput,true); return chosenOutput; }
            finally { if(File.Exists(temp)) File.Delete(temp); }
        }
        var directory=Path.GetDirectoryName(page.OriginalPath)!; var stem=Path.GetFileNameWithoutExtension(page.OriginalPath); var extension=Path.GetExtension(page.OriginalPath);
        var format=extension.ToLowerInvariant() switch { ".png"=>ImageFormat.Png,".bmp"=>ImageFormat.Bmp,_=>ImageFormat.Jpeg };
        for(var number=1;number<=10000;number++)
        {
            cancellationToken.ThrowIfCancellationRequested(); var output=Path.Combine(directory,$"{stem}_edited ({number}){extension}");
            FileStream stream;
            try { stream=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.None); }
            catch(IOException) when(File.Exists(output)) { continue; }
            try
            {
                using(stream)
                {
                    if(format.Guid==ImageFormat.Jpeg.Guid)
                    {
                        var encoder=ImageCodecInfo.GetImageEncoders().First(x=>x.FormatID==ImageFormat.Jpeg.Guid);
                        using var parameters=new EncoderParameters(1); parameters.Param[0]=new EncoderParameter(Encoder.Quality,95L);
                        bitmap.Save(stream,encoder,parameters);
                    }
                    else bitmap.Save(stream,format);
                }
                return output;
            }
            catch { stream.Dispose(); File.Delete(output); throw; }
        }
        throw new IOException("Не удалось подобрать свободное имя файла.");
    },cancellationToken);

    public Task ExportSheetAsync(SheetSnapshot sheet,string outputPath,int dpi,CancellationToken cancellationToken)=>Task.Run(()=>
    {
        if(dpi is not (300 or 600)) throw new ArgumentException("DPI: 300 или 600.");
        using var bitmap=rasterizer.RenderSheet(sheet,dpi,false,cancellationToken);
        using var stream=new FileStream(outputPath,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        try { bitmap.Save(stream,ImageFormat.Png); }
        catch { stream.Dispose(); File.Delete(outputPath); throw; }
    },cancellationToken);
}
