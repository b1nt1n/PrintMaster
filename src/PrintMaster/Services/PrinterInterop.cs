using PrintMaster.Models;
using System.ComponentModel;
using System.Drawing.Printing;
using System.Drawing;
using System.Runtime.InteropServices;

namespace PrintMaster.Services;

public sealed class PrinterInterop
{
    private const uint DmOrientation=1,DmPaperSize=2,DmPaperLength=4,DmPaperWidth=8,DmScale=16,DmCopies=256,
        DmPrintQuality=1024,DmColor=2048,DmDuplex=4096,DmYResolution=8192,DmCollate=32768;
    private const int DmOutBuffer=2,DmInBuffer=8;

    public static (PrinterSettings Printer,PageSettings Page) CreateSettings(PrintSettingsSnapshot settings)
    {
        var printer=new PrinterSettings { PrinterName=settings.PrinterName,Copies=1,Collate=false,PrintRange=PrintRange.AllPages,Duplex=Duplex.Simplex };
        if(!printer.IsValid) throw new InvalidOperationException("Принтер недоступен: "+settings.PrinterName);
        var margin=PageLayout.MmToHundredths(settings.SafeMarginMm);
        var page=new PageSettings(printer) { PaperSize=PageLayout.CreatePaper(settings,printer),Landscape=settings.Landscape,
            Color=false,Margins=new(margin,margin,margin,margin),PrinterResolution=new PrinterResolution { Kind=PrinterResolutionKind.Custom,X=settings.Dpi,Y=settings.Dpi } };
        return (printer,page);
    }

    // Returns an owned native buffer with both public and driver-private DEVMODE data.
    public static IntPtr CreateValidatedDevMode(PrintSettingsSnapshot settings,PrinterSettings printer,PageSettings page)
    {
        if(!OpenPrinter(settings.PrinterName,out var handle,IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(),"Не удалось открыть очередь принтера.");
        IntPtr buffer=IntPtr.Zero;
        try
        {
            var size=DocumentProperties(IntPtr.Zero,handle,settings.PrinterName,IntPtr.Zero,IntPtr.Zero,0);
            if(size<102 || size>1024*1024) throw new InvalidOperationException("Драйвер вернул недопустимый размер DEVMODE.");
            buffer=Marshal.AllocHGlobal(size);
            if(DocumentProperties(IntPtr.Zero,handle,settings.PrinterName,buffer,IntPtr.Zero,DmOutBuffer)!=1)
                throw new InvalidOperationException("Не удалось прочитать настройки драйвера.");
            // DEVMODEW public offsets; retain all remaining bytes, especially dmDriverExtra.
            var fields=(uint)Marshal.ReadInt32(buffer,72);
            fields |= DmOrientation|DmPaperSize|DmCopies|DmScale|DmColor|DmDuplex|DmPrintQuality|DmYResolution|DmCollate;
            Marshal.WriteInt16(buffer,76,(short)(settings.Landscape ? 2 : 1));
            if(page.PaperSize.Kind==PaperKind.Custom)
            {
                fields |= DmPaperLength|DmPaperWidth;
                Marshal.WriteInt16(buffer,78,256); // DMPAPER_USER, no persistent custom form needed.
                Marshal.WriteInt16(buffer,80,checked((short)Math.Round(settings.PaperSizeMm.Height*10)));
                Marshal.WriteInt16(buffer,82,checked((short)Math.Round(settings.PaperSizeMm.Width*10)));
            }
            else
            {
                fields &= ~(DmPaperLength|DmPaperWidth);
                Marshal.WriteInt16(buffer,78,(short)page.PaperSize.RawKind);
            }
            Marshal.WriteInt32(buffer,72,unchecked((int)fields));
            Marshal.WriteInt16(buffer,84,100); Marshal.WriteInt16(buffer,86,1);
            Marshal.WriteInt16(buffer,90,(short)settings.Dpi); Marshal.WriteInt16(buffer,92,1);
            Marshal.WriteInt16(buffer,94,1); Marshal.WriteInt16(buffer,96,(short)settings.Dpi); Marshal.WriteInt16(buffer,100,0);
            if(DocumentProperties(IntPtr.Zero,handle,settings.PrinterName,buffer,buffer,DmInBuffer|DmOutBuffer)!=1)
                throw new InvalidOperationException("Драйвер отклонил настройки бумаги/ориентации/DPI.");
            printer.SetHdevmode(buffer); SetPageDevMode(page,buffer);
            // Spooler and GDI see a single copy; repetitions are controlled by our own queue.
            printer.Copies=1; printer.Collate=false; printer.Duplex=Duplex.Simplex;
            return buffer;
        }
        catch { if(buffer!=IntPtr.Zero) Marshal.FreeHGlobal(buffer); throw; }
        finally { ClosePrinter(handle); }
    }

    public PrinterProfile Probe(PrintSettingsSnapshot settings)
    {
        var (printer,page)=CreateSettings(settings);
        var mode=CreateValidatedDevMode(settings,printer,page);
        try
        {
            var dc=CreateDC("WINSPOOL",settings.PrinterName,null,mode);
            if(dc==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"Не удалось получить геометрию принтера.");
            try
            {
                var dpiX=GetDeviceCaps(dc,88); var dpiY=GetDeviceCaps(dc,90);
                if(dpiX<=0 || dpiY<=0) throw new InvalidOperationException("Драйвер не сообщает разрешение принтера.");
                var size=new SizeD(GetDeviceCaps(dc,110)*25.4/dpiX,GetDeviceCaps(dc,111)*25.4/dpiY);
                var area=new RectD(GetDeviceCaps(dc,112)*25.4/dpiX,GetDeviceCaps(dc,113)*25.4/dpiY,
                    GetDeviceCaps(dc,8)*25.4/dpiX,GetDeviceCaps(dc,10)*25.4/dpiY);
                var expected=settings.OrientedPaperMm;
                if(Math.Abs(expected.Width-size.Width)>1.5 || Math.Abs(expected.Height-size.Height)>1.5)
                    throw new InvalidOperationException($"Драйвер заменил бумагу на {size.Width:F1} × {size.Height:F1} мм. Требуется {expected.Width:F1} × {expected.Height:F1} мм. Выберите поддерживаемый формат или настройте драйвер.");
                if(area.IsEmpty) throw new InvalidOperationException("Драйвер вернул пустую печатную область.");
                return new(settings.PrinterName,size,area,true,$"Поля драйвера: {area.X:F1} / {area.Y:F1} / {Math.Max(0,size.Width-area.Right):F1} / {Math.Max(0,size.Height-area.Bottom):F1} мм");
            }
            finally { DeleteDC(dc); }
        }
        finally { Marshal.FreeHGlobal(mode); }
    }

    public static IReadOnlyList<string> ListPrinters() => PrinterSettings.InstalledPrinters.Cast<string>().OrderBy(x=>x).ToArray();

    public static void SetPageDevMode(PageSettings page,IntPtr mode)
    {
        page.SetHdevmode(mode);
        // PageSettings maps a native user size to Custom/RawKind=0. HP's host driver
        // treats paper ID 0 as A4 even when width/length are present. Preserve DMPAPER_USER.
        if(page.PaperSize.Kind==PaperKind.Custom) page.PaperSize.RawKind=256;
    }

    public PrinterProfile ReadGraphicsProfile(Graphics graphics,string name)
    {
        var dc=graphics.GetHdc();
        try
        {
            var dpiX=GetDeviceCaps(dc,88); var dpiY=GetDeviceCaps(dc,90);
            if(dpiX<=0 || dpiY<=0) throw new InvalidOperationException("Не удалось получить разрешение текущей страницы.");
            var size=new SizeD(GetDeviceCaps(dc,110)*25.4/dpiX,GetDeviceCaps(dc,111)*25.4/dpiY);
            var area=new RectD(GetDeviceCaps(dc,112)*25.4/dpiX,GetDeviceCaps(dc,113)*25.4/dpiY,
                GetDeviceCaps(dc,8)*25.4/dpiX,GetDeviceCaps(dc,10)*25.4/dpiY);
            if(area.IsEmpty || size.Width<=0 || size.Height<=0) throw new InvalidOperationException("GDI не сообщает геометрию текущей страницы.");
            return new(name,size,area,true,"Геометрия текущей страницы GDI");
        }
        finally { graphics.ReleaseHdc(dc); }
    }

    [DllImport("winspool.drv",EntryPoint="OpenPrinterW",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool OpenPrinter(string name,out IntPtr handle,IntPtr defaults);
    [DllImport("winspool.drv",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool ClosePrinter(IntPtr handle);
    [DllImport("winspool.drv",EntryPoint="DocumentPropertiesW",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern int DocumentProperties(IntPtr owner,IntPtr printer,string name,IntPtr output,IntPtr input,int mode);
    [DllImport("gdi32.dll",EntryPoint="CreateDCW",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern IntPtr CreateDC(string driver,string device,string? output,IntPtr devmode);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr dc,int index);
    [DllImport("gdi32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
}
