using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ClankerExplorer.Services.Preview;

/// <summary>
/// Lightweight Win32 DirectShow Media Player for embedded video playback in the Preview Pane.
/// </summary>
public class NativeVideoPlayer : IDisposable
{
    private static readonly Guid CLSID_FilterGraph = new("e436ebb8-524f-11ce-9f53-0020af0ba770");
    private static readonly Guid IID_IGraphBuilder = new("56a868a9-0ad4-11ce-b03a-0020af0ba770");

    [ComImport, Guid("56a868a9-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphBuilder
    {
        [PreserveSig] int AddFilter(IntPtr pFilter, [MarshalAs(UnmanagedType.LPWStr)] string pName);
        [PreserveSig] int RemoveFilter(IntPtr pFilter);
        [PreserveSig] int EnumFilters(out IntPtr ppEnum);
        [PreserveSig] int FindFilterByName([MarshalAs(UnmanagedType.LPWStr)] string pName, out IntPtr ppFilter);
        [PreserveSig] int ConnectDirect(IntPtr ppinOut, IntPtr ppinIn, IntPtr pmt);
        [PreserveSig] int Reconnect(IntPtr ppin);
        [PreserveSig] int Disconnect(IntPtr ppin);
        [PreserveSig] int SetDefaultSyncSource();
        [PreserveSig] int Connect(IntPtr ppinOut, IntPtr ppinIn);
        [PreserveSig] int Render(IntPtr ppinOut);
        [PreserveSig] int RenderFile([MarshalAs(UnmanagedType.LPWStr)] string lpcwstrFile, [MarshalAs(UnmanagedType.LPWStr)] string? lpcwstrPlayList);
    }

    [ComImport, Guid("56a868b1-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IMediaControl
    {
        [PreserveSig] int Run();
        [PreserveSig] int Pause();
        [PreserveSig] int Stop();
        [PreserveSig] int GetState(int msTimeout, out int pfs);
        [PreserveSig] int RenderFile(string strFilename);
        [PreserveSig] int AddSourceFilter(string strFilename, out object ppUnk);
        [PreserveSig] int get_FilterCollection(out object ppUnk);
        [PreserveSig] int get_RegFilterCollection(out object ppUnk);
        [PreserveSig] int StopWhenReady();
    }

    [ComImport, Guid("56a868b4-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IVideoWindow
    {
        [PreserveSig] int put_Caption(string strCaption);
        [PreserveSig] int get_Caption(out string strCaption);
        [PreserveSig] int put_WindowStyle(int WindowStyle);
        [PreserveSig] int get_WindowStyle(out int WindowStyle);
        [PreserveSig] int put_WindowStyleEx(int WindowStyleEx);
        [PreserveSig] int get_WindowStyleEx(out int WindowStyleEx);
        [PreserveSig] int put_AutoShow(int AutoShow);
        [PreserveSig] int get_AutoShow(out int AutoShow);
        [PreserveSig] int put_WindowState(int WindowState);
        [PreserveSig] int get_WindowState(out int WindowState);
        [PreserveSig] int put_BackgroundPalette(int BackgroundPalette);
        [PreserveSig] int get_BackgroundPalette(out int BackgroundPalette);
        [PreserveSig] int put_Visible(int Visible);
        [PreserveSig] int get_Visible(out int Visible);
        [PreserveSig] int put_Left(int Left);
        [PreserveSig] int get_Left(out int Left);
        [PreserveSig] int put_Width(int Width);
        [PreserveSig] int get_Width(out int Width);
        [PreserveSig] int put_Top(int Top);
        [PreserveSig] int get_Top(out int Top);
        [PreserveSig] int put_Height(int Height);
        [PreserveSig] int get_Height(out int Height);
        [PreserveSig] int put_Owner(IntPtr Owner);
        [PreserveSig] int get_Owner(out IntPtr Owner);
        [PreserveSig] int put_MessageDrain(IntPtr Drain);
        [PreserveSig] int get_MessageDrain(out IntPtr Drain);
        [PreserveSig] int get_BorderColor(out int Color);
        [PreserveSig] int put_BorderColor(int Color);
        [PreserveSig] int get_FullScreenMode(out int FullScreenMode);
        [PreserveSig] int put_FullScreenMode(int FullScreenMode);
        [PreserveSig] int SetWindowForeground(int Focus);
        [PreserveSig] int NotifyOwnerMessage(IntPtr hwnd, int uMsg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int SetWindowPosition(int Left, int Top, int Width, int Height);
        [PreserveSig] int GetWindowPosition(out int Left, out int Top, out int Width, out int Height);
        [PreserveSig] int GetMinIdealImageSize(out int Width, out int Height);
        [PreserveSig] int GetMaxIdealImageSize(out int Width, out int Height);
        [PreserveSig] int GetRestorePosition(out int Left, out int Top, out int Width, out int Height);
        [PreserveSig] int HideCursor(int HideCursor);
        [PreserveSig] int IsCursorHidden(out int HideCursor);
    }

    [ComImport, Guid("36b73880-c2c8-11cf-8b46-00805f6cef60"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMediaSeeking
    {
        [PreserveSig] int GetCapabilities(out uint pCapabilities);
        [PreserveSig] int CheckCapabilities(ref uint pCapabilities);
        [PreserveSig] int IsFormatSupported(ref Guid pFormat);
        [PreserveSig] int QueryPreferredFormat(out Guid pFormat);
        [PreserveSig] int GetTimeFormat(out Guid pFormat);
        [PreserveSig] int IsUsingTimeFormat(ref Guid pFormat);
        [PreserveSig] int SetTimeFormat(ref Guid pFormat);
        [PreserveSig] int GetDuration(out long pDuration);
        [PreserveSig] int GetStopPosition(out long pStopPosition);
        [PreserveSig] int GetCurrentPosition(out long pCurrentPosition);
        [PreserveSig] int ConvertTimeFormat(out long pTarget, ref Guid pTargetFormat, long Source, ref Guid pSourceFormat);
        [PreserveSig] int SetPositions([In, Out] ref long pCurrent, uint dwCurrentFlags, [In, Out] ref long pStop, uint dwStopFlags);
        [PreserveSig] int GetPositions(out long pCurrent, out long pStop);
        [PreserveSig] int GetAvailable(out long pEarliest, out long pLatest);
        [PreserveSig] int SetRate(double dRate);
        [PreserveSig] int GetRate(out double pdRate);
        [PreserveSig] int GetPreroll(out long pllPreroll);
    }

    [ComImport, Guid("56a868b3-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IBasicAudio
    {
        [PreserveSig] int put_Volume(int lVolume);
        [PreserveSig] int get_Volume(out int plVolume);
        [PreserveSig] int put_Balance(int lBalance);
        [PreserveSig] int get_Balance(out int plBalance);
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid clsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid iid,
        out IntPtr ppv);

    private const int WS_CHILD = 0x40000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const uint AM_SEEKING_AbsolutePositioning = 0x01;

    private IntPtr _graphPtr = IntPtr.Zero;
    private IGraphBuilder? _graph;
    private IMediaControl? _mediaControl;
    private IVideoWindow? _videoWindow;
    private IMediaSeeking? _mediaSeeking;
    private IBasicAudio? _basicAudio;

    private bool _isDisposed;
    private double _currentVolume = 0.8;
    private bool _isMuted;

    public bool IsInitialized => _graph != null;
    public TimeSpan Duration { get; private set; } = TimeSpan.Zero;

    public bool Open(string filePath, IntPtr parentHwnd)
    {
        Close();
        if (!OperatingSystem.IsWindows() || !File.Exists(filePath)) return false;

        try
        {
            Guid clsid = CLSID_FilterGraph;
            Guid iid = IID_IGraphBuilder;
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, ref iid, out _graphPtr);
            if (hr != 0 || _graphPtr == IntPtr.Zero) return false;

            _graph = (IGraphBuilder)Marshal.GetObjectForIUnknown(_graphPtr);
            hr = _graph.RenderFile(filePath, null);
            if (hr != 0)
            {
                Close();
                return false;
            }

            _mediaControl = (IMediaControl)_graph;
            _videoWindow = (IVideoWindow)_graph;
            _mediaSeeking = (IMediaSeeking)_graph;
            _basicAudio = _graph as IBasicAudio;

            if (_videoWindow != null && parentHwnd != IntPtr.Zero)
            {
                _videoWindow.put_Owner(parentHwnd);
                _videoWindow.put_WindowStyle(WS_CHILD | WS_CLIPSIBLINGS | WS_CLIPCHILDREN);
                _videoWindow.put_Visible(-1 /* OATRUE */);
            }

            if (_mediaSeeking != null)
            {
                if (_mediaSeeking.GetDuration(out long durationTicks) == 0 && durationTicks > 0)
                {
                    Duration = TimeSpan.FromTicks(durationTicks);
                }
            }

            SetVolume(_currentVolume);
            return true;
        }
        catch
        {
            Close();
            return false;
        }
    }

    public void Play()
    {
        try { _mediaControl?.Run(); } catch { }
    }

    public void Pause()
    {
        try { _mediaControl?.Pause(); } catch { }
    }

    public void Stop()
    {
        try { _mediaControl?.Stop(); } catch { }
    }

    public void SetPosition(TimeSpan position)
    {
        if (_mediaSeeking == null) return;
        try
        {
            long ticks = position.Ticks;
            _mediaSeeking.SetPositions(ref ticks, AM_SEEKING_AbsolutePositioning, ref ticks, 0);
        }
        catch { }
    }

    public TimeSpan GetPosition()
    {
        if (_mediaSeeking == null) return TimeSpan.Zero;
        try
        {
            if (_mediaSeeking.GetCurrentPosition(out long posTicks) == 0)
            {
                return TimeSpan.FromTicks(posTicks);
            }
        }
        catch { }
        return TimeSpan.Zero;
    }

    public void SetBounds(int left, int top, int width, int height)
    {
        try
        {
            _videoWindow?.SetWindowPosition(left, top, Math.Max(1, width), Math.Max(1, height));
        }
        catch { }
    }

    public void SetVolume(double volume)
    {
        _currentVolume = Math.Clamp(volume, 0.0, 1.0);
        if (_basicAudio == null) return;

        try
        {
            if (_isMuted || _currentVolume <= 0.001)
            {
                _basicAudio.put_Volume(-10000); // Mute (-100 dB)
            }
            else
            {
                // DirectShow volume: 0 (max, 0dB) to -10,000 (silence, -100dB)
                // Logarithmic conversion
                int dsVol = (int)(2000.0 * Math.Log10(_currentVolume));
                dsVol = Math.Clamp(dsVol, -10000, 0);
                _basicAudio.put_Volume(dsVol);
            }
        }
        catch { }
    }

    public void SetMute(bool isMuted)
    {
        _isMuted = isMuted;
        SetVolume(_currentVolume);
    }

    public void Close()
    {
        try
        {
            if (_videoWindow != null)
            {
                _videoWindow.put_Visible(0 /* OAFALSE */);
                _videoWindow.put_Owner(IntPtr.Zero);
            }
            _mediaControl?.Stop();
        }
        catch { }

        _mediaControl = null;
        _videoWindow = null;
        _mediaSeeking = null;
        _basicAudio = null;
        _graph = null;

        if (_graphPtr != IntPtr.Zero)
        {
            Marshal.Release(_graphPtr);
            _graphPtr = IntPtr.Zero;
        }

        Duration = TimeSpan.Zero;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Close();
    }
}
