using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ClankerExplorer.Models;

namespace ClankerExplorer.Services;

/// <summary>
/// High-performance file icon service providing high-resolution Windows-associated icons with memory caching.
/// </summary>
public class FileIconService
{
    private static readonly Lazy<FileIconService> _instance = new(() => new FileIconService());
    public static FileIconService Instance => _instance.Value;

    private readonly ConcurrentDictionary<string, IImage?> _extensionIconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IImage?> _fileIconCache = new(StringComparer.OrdinalIgnoreCase);
    private IImage? _defaultGenericFileIcon;

    private static readonly HashSet<string> PerFileIconExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".ico", ".lnk", ".url", ".appx", ".msi"
    };

    private static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c0-d459e9f86333");
    private static readonly Guid IID_IImageList = new("46EB5926-582E-40E7-9F60-402D380C4F65");

    private const int SHIL_LARGE = 0;      // 32x32
    private const int SHIL_SMALL = 1;      // 16x16
    private const int SHIL_EXTRALARGE = 2; // 48x48
    private const int SHIL_JUMBO = 4;      // 256x256

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SYSICONINDEX = 0x000004000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    private const int ILD_TRANSPARENT = 0x00000001;
    private const uint DI_NORMAL = 0x0003;

    private IImage? _folderIcon;

    public IImage? GetFileIcon(FileItem item)
    {
        if (item == null) return null;

        if (item.IsDirectory)
        {
            return GetFolderIcon();
        }

        var ext = item.Extension ?? string.Empty;
        var fullPath = item.FullPath;

        // If file exists on disk, try exact extraction first (gives unique app/doc/media icons)
        if (!string.IsNullOrEmpty(fullPath) && File.Exists(fullPath))
        {
            return _fileIconCache.GetOrAdd(fullPath, path => ExtractFileIcon(path, isExactPath: true) ?? GetExtensionIcon(ext));
        }

        return GetExtensionIcon(ext);
    }

    public IImage? GetFolderIcon()
    {
        if (_folderIcon != null) return _folderIcon;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                // Try Jumbo (256x256) folder icon from System Image List
                var sfi = new SHFILEINFO();
                IntPtr res = SHGetFileInfo("dummy", FILE_ATTRIBUTE_DIRECTORY, ref sfi, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_SYSICONINDEX | SHGFI_USEFILEATTRIBUTES);
                if (res != IntPtr.Zero)
                {
                    int iconIndex = sfi.iIcon;
                    var iid = IID_IImageList;
                    int hr = SHGetImageList(SHIL_JUMBO, ref iid, out var imageList);
                    if (hr == 0 && imageList != null)
                    {
                        hr = imageList.GetIcon(iconIndex, ILD_TRANSPARENT, out IntPtr hIcon);
                        if (hr == 0 && hIcon != IntPtr.Zero)
                        {
                            try
                            {
                                _folderIcon = ConvertHIconToBitmap(hIcon);
                            }
                            finally
                            {
                                DestroyIcon(hIcon);
                            }
                        }
                    }
                }

                if (_folderIcon == null)
                {
                    SHGetFileInfo("dummy", FILE_ATTRIBUTE_DIRECTORY, ref sfi, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
                    if (sfi.hIcon != IntPtr.Zero)
                    {
                        try
                        {
                            _folderIcon = ConvertHIconToBitmap(sfi.hIcon);
                        }
                        finally
                        {
                            DestroyIcon(sfi.hIcon);
                        }
                    }
                }
            }
            catch { }
        }

        return _folderIcon;
    }

    public IImage? GetExtensionIcon(string extension)
    {
        if (string.IsNullOrEmpty(extension))
        {
            return _extensionIconCache.GetOrAdd(string.Empty, _ => ExtractGenericFileIcon());
        }

        return _extensionIconCache.GetOrAdd(extension, ext => ExtractFileIcon(ext, isExactPath: false) ?? ExtractGenericFileIcon());
    }

    private IImage? ExtractFileIcon(string target, bool isExactPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            // 1. If file exists on disk, try high-resolution IShellItemImageFactory (up to 256x256)
            if (isExactPath && File.Exists(target))
            {
                var highRes = ExtractFromShellItemImageFactory(target, 256);
                if (highRes != null) return highRes;
            }

            // 2. Try Jumbo / Extra-Large system image list via SHGetImageList
            string dummyPath = target.StartsWith(".") ? $"dummy{target}" : (target.Contains('.') ? target : $"dummy.{target}");
            var imageListIcon = ExtractFromImageList(dummyPath, SHIL_JUMBO) ?? ExtractFromImageList(dummyPath, SHIL_EXTRALARGE);
            if (imageListIcon != null) return imageListIcon;

            // 3. Fallback to standard SHGetFileInfo
            var sfi = new SHFILEINFO();
            uint flags = SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES;
            SHGetFileInfo(dummyPath, FILE_ATTRIBUTE_NORMAL, ref sfi, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);

            if (sfi.hIcon != IntPtr.Zero)
            {
                try
                {
                    return ConvertHIconToBitmap(sfi.hIcon);
                }
                finally
                {
                    DestroyIcon(sfi.hIcon);
                }
            }
        }
        catch
        {
            // Fall back gracefully
        }

        return null;
    }

    private IImage? ExtractFromShellItemImageFactory(string filePath, int targetSize)
    {
        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            int hr = SHCreateItemFromParsingName(filePath, IntPtr.Zero, IID_IShellItemImageFactory, out var factory);
            if (hr != 0 || factory == null) return null;

            var size = new SIZE(targetSize, targetSize);
            hr = factory.GetImage(size, SIIGBF.SIIGBF_ICONONLY | SIIGBF.SIIGBF_BIGGERSIZEOK, out hBitmap);
            if (hr == 0 && hBitmap != IntPtr.Zero)
            {
                return ConvertHBitmapToAvaloniaBitmap(hBitmap);
            }
        }
        catch
        {
            // Ignore
        }
        finally
        {
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
        }
        return null;
    }

    private IImage? ExtractFromImageList(string dummyPath, int imageListSize)
    {
        IntPtr hIcon = IntPtr.Zero;
        try
        {
            var sfi = new SHFILEINFO();
            IntPtr res = SHGetFileInfo(dummyPath, FILE_ATTRIBUTE_NORMAL, ref sfi, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_SYSICONINDEX | SHGFI_USEFILEATTRIBUTES);
            if (res == IntPtr.Zero) return null;

            int iconIndex = sfi.iIcon;
            var iid = IID_IImageList;
            int hr = SHGetImageList(imageListSize, ref iid, out var imageList);
            if (hr != 0 || imageList == null) return null;

            hr = imageList.GetIcon(iconIndex, ILD_TRANSPARENT, out hIcon);
            if (hr == 0 && hIcon != IntPtr.Zero)
            {
                return ConvertHIconToBitmap(hIcon);
            }
        }
        catch
        {
            // Ignore
        }
        finally
        {
            if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
        }
        return null;
    }

    private IImage? ExtractGenericFileIcon()
    {
        if (_defaultGenericFileIcon != null) return _defaultGenericFileIcon;

        _defaultGenericFileIcon = ExtractFileIcon(".txt", isExactPath: false)
            ?? ExtractFileIcon(".dat", isExactPath: false)
            ?? ExtractFileIcon(".log", isExactPath: false);

        return _defaultGenericFileIcon;
    }

    private static Bitmap? ConvertHIconToBitmap(IntPtr hIcon)
    {
        int width = 256;
        int height = 256;

        if (GetIconInfo(hIcon, out ICONINFO ii))
        {
            try
            {
                if (ii.hbmColor != IntPtr.Zero)
                {
                    if (GetObject(ii.hbmColor, Marshal.SizeOf<BITMAP>(), out BITMAP bmp) != 0 && bmp.bmWidth > 0 && bmp.bmHeight > 0)
                    {
                        width = bmp.bmWidth;
                        height = bmp.bmHeight;
                    }
                }
            }
            finally
            {
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            }
        }

        IntPtr hdcScreen = GetDC(IntPtr.Zero);
        IntPtr hdcMem = CreateCompatibleDC(hdcScreen);
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr hOldBmp = IntPtr.Zero;

        try
        {
            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = width;
            bmi.bmiHeader.biHeight = -height; // Top-down DIB
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0; // BI_RGB

            hBitmap = CreateDIBSection(hdcScreen, ref bmi, 0, out IntPtr pBits, IntPtr.Zero, 0);
            if (hBitmap == IntPtr.Zero || pBits == IntPtr.Zero) return null;

            hOldBmp = SelectObject(hdcMem, hBitmap);

            // Draw icon into 32bpp memory DC
            bool drawn = DrawIconEx(hdcMem, 0, 0, hIcon, width, height, 0, IntPtr.Zero, DI_NORMAL);
            if (!drawn) return null;

            GdiFlush();

            byte[] pixelData = new byte[width * height * 4];
            Marshal.Copy(pBits, pixelData, 0, pixelData.Length);

            // Check if icon has non-zero alpha channel
            bool hasAlpha = false;
            for (int i = 3; i < pixelData.Length; i += 4)
            {
                if (pixelData[i] != 0)
                {
                    hasAlpha = true;
                    break;
                }
            }

            // If no alpha was written by DrawIconEx, make non-zero color pixels fully opaque
            if (!hasAlpha)
            {
                for (int i = 0; i < pixelData.Length; i += 4)
                {
                    if (pixelData[i] != 0 || pixelData[i + 1] != 0 || pixelData[i + 2] != 0)
                    {
                        pixelData[i + 3] = 255;
                    }
                }
            }

            // Copy pixel data into WriteableBitmap
            var wbm = new WriteableBitmap(
                new Avalonia.PixelSize(width, height),
                new Avalonia.Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888,
                Avalonia.Platform.AlphaFormat.Premul);

            using (var fb = wbm.Lock())
            {
                Marshal.Copy(pixelData, 0, fb.Address, pixelData.Length);
            }

            return wbm;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hOldBmp != IntPtr.Zero) SelectObject(hdcMem, hOldBmp);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            if (hdcMem != IntPtr.Zero) DeleteDC(hdcMem);
            if (hdcScreen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    private static Bitmap? ConvertHBitmapToAvaloniaBitmap(IntPtr hBitmap)
    {
        IntPtr hdc = CreateCompatibleDC(IntPtr.Zero);
        try
        {
            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();

            if (GetDIBits(hdc, hBitmap, 0, 0, null!, ref bmi, 0) == 0) return null;

            int width = bmi.bmiHeader.biWidth;
            int height = Math.Abs(bmi.bmiHeader.biHeight);
            if (width <= 0 || height <= 0) return null;

            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0; // BI_RGB
            bmi.bmiHeader.biHeight = -height; // Top-down DIB

            byte[] pixelData = new byte[width * height * 4];
            int lines = GetDIBits(hdc, hBitmap, 0, (uint)height, pixelData, ref bmi, 0);
            if (lines == 0) return null;

            var wbm = new WriteableBitmap(
                new Avalonia.PixelSize(width, height),
                new Avalonia.Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888,
                Avalonia.Platform.AlphaFormat.Premul);

            using (var fb = wbm.Lock())
            {
                Marshal.Copy(pixelData, 0, fb.Address, pixelData.Length);
            }
            return wbm;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hdc != IntPtr.Zero) DeleteDC(hdc);
        }
    }

    #region Win32 P/Invoke & COM Interfaces

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
        public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    private enum SIIGBF
    {
        SIIGBF_RESIZETOFIT = 0x00000000,
        SIIGBF_BIGGERSIZEOK = 0x00000001,
        SIIGBF_MEMORYONLY = 0x00000002,
        SIIGBF_ICONONLY = 0x00000004,
        SIIGBF_THUMBNAILONLY = 0x00000008,
        SIIGBF_INCACHEONLY = 0x00000010
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c0-d459e9f86333")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(
            [In, MarshalAs(UnmanagedType.Struct)] SIZE size,
            [In] SIIGBF flags,
            [Out] out IntPtr phbm);
    }

    [ComImport]
    [Guid("46EB5926-582E-40E7-9F60-402D380C4F65")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig] int ImageListSetIconSize(int cx, int cy);
        [PreserveSig] int ImageListGetIconSize(out int cx, out int cy);
        [PreserveSig] int ImageListSetImageCount(int uNewCount);
        [PreserveSig] int ImageListGetImageCount(out int pi);
        [PreserveSig] int ImageListSetBkColor(int clrBk, out int pclr);
        [PreserveSig] int ImageListGetBkColor(out int pclr);
        [PreserveSig] int ImageListBeginDrag(int iTrack, int dxHotspot, int dyHotspot);
        [PreserveSig] int ImageListEndDrag();
        [PreserveSig] int ImageListDragEnter(IntPtr hwndLock, int x, int y);
        [PreserveSig] int ImageListDragLeave(IntPtr hwndLock);
        [PreserveSig] int ImageListDragMove(int x, int y);
        [PreserveSig] int ImageListSetDragCursorImage(ref IImageList punk, int iDrag, int dxHotspot, int dyHotspot);
        [PreserveSig] int ImageListDragShowNolock(int fShow);
        [PreserveSig] int ImageListGetDragImage(out POINT ppt, out POINT pptHotspot, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetItemFlags(int i, out int dwFlags);
        [PreserveSig] int ImageListGetOverlayImage(int iOverlay, out int piIndex);
        [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
    }

    [DllImport("shell32.dll", EntryPoint = "#727")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHCreateItemFromParsingName(
        [In, MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        [In] IntPtr pbc,
        [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [Out, MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbSizeFileInfo,
        uint uFlags);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GdiFlush();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetObject(IntPtr hObject, int nCount, out BITMAP lpObject);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyWidth, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, [In] ref BITMAPINFO pbmi, uint pila, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, [Out] byte[] lpBits, ref BITMAPINFO pbmi, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
    }

    #endregion
}