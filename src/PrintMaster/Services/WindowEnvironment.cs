using PrintMaster.Models;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PrintMaster.Services;

// WINDOWPLACEMENT uses workspace coordinates. Do not pass these rectangles to SetWindowPos.
public static class WindowEnvironment
{
    public static SavedWindowPlacement? Capture(Window window)
    {
        var hwnd=new WindowInteropHelper(window).Handle; if(hwnd==0) return null;
        var native=NewPlacement(); if(!GetWindowPlacement(hwnd,ref native)) return null;
        var dpi=GetDpiForWindow(hwnd); if(dpi==0) dpi=96;
        var rect=native.NormalPosition;
        var result=new SavedWindowPlacement(rect.Left,rect.Top,rect.Right,rect.Bottom,dpi,
            native.ShowCommand==3 || native.ShowCommand==2 && (native.Flags&2)!=0);
        return result.IsValid ? result : null;
    }
    public static bool Restore(Window window,SavedWindowPlacement saved)
    {
        if(!saved.IsValid) return false;
        var hwnd=new WindowInteropHelper(window).Handle; if(hwnd==0) return false;
        var native=NewPlacement(); native.ShowCommand=1;
        native.NormalPosition=new() { Left=saved.Left,Top=saved.Top,Right=saved.Right,Bottom=saved.Bottom };
        if(!SetWindowPlacement(hwnd,ref native)) return false;
        // Move first so GetDpiForWindow describes the destination monitor. Preserve DIP size across DPI changes.
        var dpi=GetDpiForWindow(hwnd); if(dpi==0) dpi=96;
        if(!GetWindowPlacement(hwnd,ref native)) return false;
        if(dpi!=saved.Dpi)
        {
            var scaled=ScaleSize(saved,dpi);
            native.NormalPosition.Right=native.NormalPosition.Left+scaled.Width;
            native.NormalPosition.Bottom=native.NormalPosition.Top+scaled.Height;
        }
        native.Flags=0; native.ShowCommand=saved.Maximized ? 3u : 1u;
        return SetWindowPlacement(hwnd,ref native);
    }
    internal static (int Width,int Height) ScaleSize(SavedWindowPlacement saved,uint dpi)=>
        (checked((int)Math.Round((saved.Right-(long)saved.Left)*dpi/(double)saved.Dpi)),
         checked((int)Math.Round((saved.Bottom-(long)saved.Top)*dpi/(double)saved.Dpi)));
    private static NativePlacement NewPlacement()=>new()
    { Length=(uint)Marshal.SizeOf<NativePlacement>(),MinPosition=new() { X=-1,Y=-1 },MaxPosition=new() { X=-1,Y=-1 } };
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePlacement
    { public uint Length,Flags,ShowCommand; public NativePoint MinPosition,MaxPosition; public NativeRect NormalPosition; }
    [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowPlacement(nint hwnd,ref NativePlacement placement);
    [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPlacement(nint hwnd,ref NativePlacement placement);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
}
