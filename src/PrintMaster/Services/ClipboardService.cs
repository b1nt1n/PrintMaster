using PrintMaster.Models;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace PrintMaster.Services;

public interface IClipboardService
{
    bool HasSupportedContent()=>true;
    Task<ClipboardSnapshot> ReadAsync(CancellationToken token);
    Task WriteAsync(ClipboardSnapshot content,CancellationToken token);
}

public sealed class ClipboardService : IClipboardService
{
    public bool HasSupportedContent()
    {
        RequireSta();
        try
        {
            return Clipboard.ContainsImage() || Clipboard.ContainsFileDropList() || Clipboard.ContainsText()
                || Clipboard.ContainsData(ObjectFormat) || Clipboard.ContainsData(PagesFormat) || Clipboard.ContainsData(ObjectsFormat);
        }
        catch(ExternalException) { return true; } // A busy clipboard can be retried by the existing paste operation.
    }
    public const string ObjectFormat="PrintMaster.Object.v1";
    public const string PagesFormat="PrintMaster.Pages.v1";
    public const string ObjectsFormat="PrintMaster.Objects.v1";
    public const int MaxSelectionBytes=64*1024*1024;
    public const int MaxImagePixels=150_000_000;
    public const int MaxPngBytes=64*1024*1024;
    private static void RequireSta()
    {
        if(Thread.CurrentThread.GetApartmentState()!=ApartmentState.STA) throw new InvalidOperationException("Буфер Windows доступен из потока интерфейса.");
    }
    public async Task<ClipboardSnapshot> ReadAsync(CancellationToken token)
    {
        for(var attempt=0;;attempt++)
        {
            token.ThrowIfCancellationRequested(); RequireSta();
            try { return ReadData(Clipboard.GetDataObject()); }
            catch(ExternalException) when(attempt<4) { await Task.Delay(60,token); }
            catch(ExternalException ex) { throw new InvalidOperationException("Буфер обмена занят другим приложением. Повторите вставку.",ex); }
        }
    }
    public async Task WriteAsync(ClipboardSnapshot content,CancellationToken token)
    {
        var data=CreateData(content);
        for(var attempt=0;;attempt++)
        {
            token.ThrowIfCancellationRequested(); RequireSta();
            try { Clipboard.SetDataObject(data,true); return; }
            catch(ExternalException) when(attempt<4) { await Task.Delay(60,token); }
            catch(ExternalException ex) { throw new InvalidOperationException("Буфер обмена занят другим приложением. Повторите копирование.",ex); }
        }
    }
    internal static ClipboardSnapshot ReadData(IDataObject? data)
    {
        if(data==null) return ClipboardSnapshot.Empty;
        foreach(var format in new[] { PagesFormat,ObjectsFormat })
        {
            if(!data.GetDataPresent(format,false)) continue;
            var bytes=data.GetData(format,false) switch
            {
                byte[] b when b.Length<=MaxSelectionBytes=>b,
                MemoryStream stream when stream.Length<=MaxSelectionBytes=>stream.ToArray(),
                _=>throw new InvalidDataException("Некорректная группа PrintMaster или размер более 64 МБ.")
            };
            return new([],SelectionArchive:bytes,SelectionKind:format==PagesFormat ? ClipboardSelectionKind.Pages : ClipboardSelectionKind.Objects);
        }
        if(data.GetDataPresent(ObjectFormat,false))
        {
            if(data.GetData(ObjectFormat,false) is not string json || json.Length>ClipboardObjectService.MaxJsonCharacters) throw new InvalidDataException("Некорректная копия объекта PrintMaster.");
            return new([],ObjectJson:json);
        }
        if(data.GetDataPresent(DataFormats.FileDrop))
        {
            if(data.GetData(DataFormats.FileDrop) is not string[] files || files.Length>20000) throw new InvalidDataException("Некорректный список файлов в буфере.");
            return new(files.ToImmutableArray());
        }
        foreach(var format in new[] { "PNG","image/png" })
        {
            if(!data.GetDataPresent(format,false)) continue;
            var value=data.GetData(format,false);
            byte[] bytes=value switch { byte[] b=>b,MemoryStream stream when stream.Length<=MaxPngBytes=>stream.ToArray(),_=>throw new InvalidDataException("Некорректное PNG-изображение в буфере.") };
            if(bytes.Length>MaxPngBytes) throw new InvalidDataException("Изображение в буфере превышает 64 МБ.");
            using var input=new MemoryStream(bytes,false); var decoder=new PngBitmapDecoder(input,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
            return new([],FreezeImage(decoder.Frames[0]));
        }
        if(data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource image) return new([],FreezeImage(image));
        var text=data.GetDataPresent(DataFormats.UnicodeText) ? data.GetData(DataFormats.UnicodeText) as string : data.GetDataPresent(DataFormats.Text) ? data.GetData(DataFormats.Text) as string : null;
        if(text?.Length>TextLimits.MaxCharacters) throw new InvalidDataException("В буфере более 1 000 000 символов.");
        return new([],Text:text);
    }
    internal static BitmapSource FreezeImage(BitmapSource image)
    {
        if(image.PixelWidth<=0 || image.PixelHeight<=0 || (long)image.PixelWidth*image.PixelHeight>MaxImagePixels) throw new InvalidDataException("Изображение превышает лимит 150 млн пикселей.");
        // A frozen BitmapFrame still retains a dispatcher-bound decoder for its
        // metadata. Detach pixels on the owning thread before a worker encodes them.
        var converted=image.Format==System.Windows.Media.PixelFormats.Bgra32 ? image
            : new FormatConvertedBitmap(image,System.Windows.Media.PixelFormats.Bgra32,null,0);
        var stride=checked(image.PixelWidth*4); var pixels=new byte[checked(stride*image.PixelHeight)];
        converted.CopyPixels(pixels,stride,0);
        var copy=BitmapSource.Create(image.PixelWidth,image.PixelHeight,image.DpiX>0 ? image.DpiX : 96,image.DpiY>0 ? image.DpiY : 96,
            System.Windows.Media.PixelFormats.Bgra32,null,pixels,stride);
        copy.Freeze(); return copy;
    }
    internal static DataObject CreateData(ClipboardSnapshot content)
    {
        var data=new DataObject();
        if(content.SelectionArchive is {} archive)
        {
            if(archive.Length>MaxSelectionBytes) throw new InvalidDataException("Группа в буфере превышает 64 МБ.");
            data.SetData(content.SelectionKind==ClipboardSelectionKind.Pages ? PagesFormat : ObjectsFormat,new MemoryStream(archive,false),false);
        }
        if(content.ObjectJson!=null) data.SetData(ObjectFormat,content.ObjectJson,false);
        if(content.Text!=null) data.SetData(DataFormats.UnicodeText,content.Text);
        if(!content.Files.IsDefaultOrEmpty) data.SetData(DataFormats.FileDrop,content.Files.ToArray());
        if(content.Image!=null)
        {
            using var stream=new MemoryStream(); var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(content.Image)); encoder.Save(stream);
            data.SetData("PNG",new MemoryStream(stream.ToArray(),false),false); data.SetData(DataFormats.Bitmap,content.Image);
        }
        return data;
    }
}
