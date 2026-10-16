using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PrintMaster.Services;

public static class WindowWorkArea
{
    public static void Attach(Window window)=>_ = new Controller(window);
    internal static Size LogicalSize(Size pixels,double dpi)=>new(pixels.Width*96/dpi,pixels.Height*96/dpi);
    private sealed class Controller
    {
        private readonly Window _window;
        private readonly double _minWidth,_minHeight,_maxWidth,_maxHeight;
        private HwndSource? _source;
        private bool _pending,_applying,_closed;
        public Controller(Window window)
        {
            _window=window; _minWidth=window.MinWidth; _minHeight=window.MinHeight; _maxWidth=window.MaxWidth; _maxHeight=window.MaxHeight;
            window.SourceInitialized+=Initialized; window.Loaded+=Loaded; window.LocationChanged+=Changed; window.StateChanged+=Changed; window.Closed+=Closed;
        }
        private void Initialized(object? sender,EventArgs e)
        { _source=HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle); _source?.AddHook(Hook); Apply(); }
        private void Loaded(object sender,RoutedEventArgs e)=>Apply();
        private void Changed(object? sender,EventArgs e)=>Schedule();
        private nint Hook(nint hwnd,int message,nint wparam,nint lparam,ref bool handled)
        { if(message is 0x02E0 or 0x007E or 0x001A) Schedule(); return 0; } // DPI/display/work-area changes; WPF retains its native handling.
        private void Schedule()
        {
            if(_closed || _pending || _applying) return; _pending=true;
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=>{ _pending=false; if(!_closed) Apply(); }));
        }
        private void Apply()
        {
            if(_closed || _applying) return;
            var handle=new WindowInteropHelper(_window).Handle; if(handle==0) return;
            var info=new MonitorInfo { Size=Marshal.SizeOf<MonitorInfo>() };
            if(!GetMonitorInfoW(MonitorFromWindow(handle,2),ref info)) return;
            var dpi=GetDpiForWindow(handle); if(dpi==0) return;
            var size=LogicalSize(new(info.Work.Right-info.Work.Left,info.Work.Bottom-info.Work.Top),dpi);
            if(size.Width<=0 || size.Height<=0) return;
            _applying=true;
            try
            {
                _window.MinWidth=Math.Min(_minWidth,size.Width); _window.MinHeight=Math.Min(_minHeight,size.Height);
                // Let Windows account for its outer resize borders when maximized.
                _window.MaxWidth=_window.WindowState==WindowState.Maximized ? _maxWidth : Math.Min(_maxWidth,size.Width);
                _window.MaxHeight=_window.WindowState==WindowState.Maximized ? _maxHeight : Math.Min(_maxHeight,size.Height);
                if(!_window.IsLoaded || _window.WindowState!=WindowState.Normal || !GetWindowRect(handle,out var bounds)) return;
                var x=Math.Clamp(bounds.Left,info.Work.Left,Math.Max(info.Work.Left,info.Work.Right-(bounds.Right-bounds.Left)));
                var y=Math.Clamp(bounds.Top,info.Work.Top,Math.Max(info.Work.Top,info.Work.Bottom-(bounds.Bottom-bounds.Top)));
                if(double.IsFinite(_window.Left) && Math.Abs(x-bounds.Left)>1) _window.Left+=(x-bounds.Left)*96d/dpi;
                if(double.IsFinite(_window.Top) && Math.Abs(y-bounds.Top)>1) _window.Top+=(y-bounds.Top)*96d/dpi;
            }
            finally { _applying=false; }
        }
        private void Closed(object? sender,EventArgs e)
        {
            _closed=true; if(_source is { IsDisposed:false }) _source.RemoveHook(Hook);
            _window.SourceInitialized-=Initialized; _window.Loaded-=Loaded; _window.LocationChanged-=Changed; _window.StateChanged-=Changed; _window.Closed-=Closed;
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor,Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd,uint flags);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfoW(nint monitor,ref MonitorInfo info);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd,out NativeRect rect);
}
