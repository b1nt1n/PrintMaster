using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PrintMaster.Services;

public static class TextColorService
{
    public static bool TryNormalize(string? text,out string normalized)
    {
        normalized="";
        var value=text?.Trim() ?? "";
        if(value.StartsWith('#')) value=value[1..];
        if(value.Length!=6 || !uint.TryParse(value,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out var rgb)) return false;
        normalized="#"+rgb.ToString("X6",CultureInfo.InvariantCulture); return true;
    }
    public static uint ToColorRef(string text)
    {
        var color=ColorTranslator.FromHtml(text);
        return (uint)(color.R | color.G<<8 | color.B<<16);
    }
    public static string FromColorRef(uint color)=>$"#{color&255:X2}{(color>>8)&255:X2}{(color>>16)&255:X2}";
}

public interface ITextColorPicker { string? Pick(IntPtr owner,string current); }

// Windows provides the complete color spectrum, luminance and RGB controls.
// Calling the common dialog directly keeps the application WPF and adds no UI dependency.
public sealed class WindowsTextColorPicker : ITextColorPicker
{
    private const uint RgbInit=0x1,FullOpen=0x2,AnyColor=0x100;
    private readonly uint[] _customColors=Enumerable.Repeat(0x00FFFFFFu,16).ToArray();
    public string? Pick(IntPtr owner,string current)
    {
        if(Thread.CurrentThread.GetApartmentState()!=ApartmentState.STA) throw new InvalidOperationException("Выбор цвета открывается из потока интерфейса.");
        var colors=Marshal.AllocHGlobal(16*sizeof(uint));
        try
        {
            for(var i=0;i<16;i++) Marshal.WriteInt32(colors,i*sizeof(uint),unchecked((int)_customColors[i]));
            var data=new ChooseColorData { Size=(uint)Marshal.SizeOf<ChooseColorData>(),Owner=owner,Color=TextColorService.ToColorRef(current),
                CustomColors=colors,Flags=RgbInit|FullOpen|AnyColor };
            if(!ChooseColorW(ref data))
            {
                var error=CommDlgExtendedError();
                if(error!=0) throw new InvalidOperationException($"Палитра Windows не открылась (0x{error:X}).");
                return null;
            }
            for(var i=0;i<16;i++) _customColors[i]=unchecked((uint)Marshal.ReadInt32(colors,i*sizeof(uint)));
            return TextColorService.FromColorRef(data.Color);
        }
        finally { Marshal.FreeHGlobal(colors); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ChooseColorData
    {
        public uint Size; public IntPtr Owner,Instance; public uint Color; public IntPtr CustomColors;
        public uint Flags; public IntPtr CustomData,Hook,Template;
    }
    [DllImport("comdlg32.dll",ExactSpelling=true)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool ChooseColorW(ref ChooseColorData color);
    [DllImport("comdlg32.dll",ExactSpelling=true)] private static extern uint CommDlgExtendedError();
}
