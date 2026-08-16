using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ClankerExplorer.Models;

namespace ClankerExplorer.Services;

/// <summary>
/// High-performance file icon service providing pristine high-resolution (up to 256x256 Jumbo)
/// Windows-associated icons with size-tiered memory caching.
/// </summary>
public class FileIconService
{
    private static readonly Lazy<FileIconService> _instance = new(() => new FileIconService());
    public static FileIconService Instance => _instance.Value;

    // Separate caches for small (16-32px) and large (128-256px) icons
    private readonly ConcurrentDictionary<string, IImage?> _extensionSmallIconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IImage?> _extensionLargeIconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IImage?> _fileSmallIconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IImage?> _fileLargeIconCache = new(StringComparer.OrdinalIgnoreCase);

    private IImage? _defaultGenericSmallIcon;
    private IImage? _defaultGenericLargeIcon;
    private IImage? _folderSmallIcon;
    private IImage? _folderLargeIcon;

    private static readonly HashSet<string> PerFileIconExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".ico", ".lnk", ".url", ".appx", ".msi"
    };

    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");

    private const int SHIL_LARGE = 0;      // 32x32
    private const int SHIL_SMALL = 1;      // 16x16
    private const int SHIL_EXTRALARGE = 2; // 48x48
    private const int SHIL_JUMBO = 4;      // 256x256

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SYSICONINDEX = 0x000004000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    private const uint ILD_TRANSPARENT = 0x00000001;
    private const uint DI_NORMAL = 0x0003;

    public IImage? GetFileIcon(FileItem item, bool isLarge = false)
    {
        if (item == null) return null;

        if (item.IsDirectory)
        {
            return GetFolderIcon(isLarge);
        }

        var ext = item.Extension ?? string.Empty;
        var fullPath = item.FullPath;

        // If file exists on disk and is a per-file icon type (like .exe, .lnk) or if large icon is requested,
        // try exact per-file shell extraction first
        if (!string.IsNullOrEmpty(fullPath) && (PerFileIconExtensions.Contains(ext) || isLarge) && File.Exists(fullPath))
        {
            var cache = isLarge ? _fileLargeIconCache : _fileSmallIconCache;
            return cache.GetOrAdd(fullPath, path => ExtractFileIcon(path, isExactPath: true, isLarge: isLarge) ?? GetExtensionIcon(ext, isLarge));
        }

        return GetExtensionIcon(ext, isLarge);
    }

    public IImage? GetFolderIcon(bool isLarge = false)
    {
        if (isLarge && _folderLargeIcon != null) return _folderLargeIcon;
        if (!isLarge && _folderSmallIcon != null) return _folderSmallIcon;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                int imageListSize = isLarge ? SHIL_JUMBO : SHIL_SMALL;
                var sfi = new SHFILEINFO();
                IntPtr res = SHGetFileInfo("dummy", FILE_ATTRIBUTE_DIRECTORY, ref sfi, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_SYSICONINDEX | SHGFI_USEFILEATTRIBUTES);
                if (res != IntPtr.Zero)
                {
                    int iconIndex = sfi.iIcon;
                    var icon = ExtractFromImageListByIndex(iconIndex, imageListSize);
                    if (icon != null)
                    {
                        if (isLarge) _folderLargeIcon = icon;
                        else _folderSmallIcon = icon;
                        return icon;
                    }
                }

                // Fallback to standard SHGetFileInfo
                uint flags = SHGFI_ICON | (isLarge ? SHGFI_LARGEICON : SHGFI_SMALLICON) | SHGFI_USEFILEATTRIBUTES;
                SHGetFileInfo("dummy", FILE_ATTRIBUTE_DIRECTORY, ref sfi, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
                if (sfi.hIcon != IntPtr.Zero)
                {
                    try
                    {
                        var icon = ConvertHIconToBitmap(sfi.hIcon);
                        if (isLarge) _folderLargeIcon = icon;
                        else _folderSmallIcon = icon;
                        return icon;
                    }
                    finally
                    {
                        DestroyIcon(sfi.hIcon);
                    }
                }
            }
            catch { }
        }

        return isLarge ? _folderLargeIcon : _folderSmallIcon;
    }

    public IImage? GetExtensionIcon(string extension, bool isLarge = false)
    {
        var cache = isLarge ? _extensionLargeIconCache : _extensionSmallIconCache;

        if (string.IsNullOrEmpty(extension))
        {
            return cache.GetOrAdd(string.Empty, _ => ExtractGenericFileIcon(isLarge));
        }

        return cache.GetOrAdd(extension, ext => ExtractFileIcon(ext, isExactPath: false, isLarge: isLarge) ?? ExtractGenericFileIcon(isLarge));
    }

    private IImage? ExtractFileIcon(string target, bool isExactPath, bool isLarge)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            // 1. Try System Image List (Jumbo 256x256 for large, Small 16x16 for small, Extra-Large 48x48 fallback)
            string dummyPath = target.StartsWith(".") ? $"dummy{target}" : (target.Contains('.') ? target : $"dummy.{target}");
            uint dwAttr = isExactPath ? 0 : FILE_ATTRIBUTE_NORMAL;
            uint sfiFlags = isExactPath ? SHGFI_SYSICONINDEX : (SHGFI_SYSICONINDEX | SHGFI_USEFILEATTRIBUTES);

            int imageListSize = isLarge ? SHIL_JUMBO : SHIL_SMALL;
            var imageListIcon = ExtractFromImageListByPath(target, dwAttr, sfiFlags, imageListSize);
            if (imageListIcon == null && isLarge)
            {
                imageListIcon = ExtractFromImageListByPath(target, dwAttr, sfiFlags, SHIL_EXTRALARGE) ?? ExtractFromImageListByPath(target, dwAttr, sfiFlags, SHIL_LARGE);
            }
            if (imageListIcon != null) return imageListIcon;

            // 2. Fallback to standard SHGetFileInfo
            var sfi = new SHFILEINFO();
            uint flags = SHGFI_ICON | (isLarge ? SHGFI_LARGEICON : SHGFI_SMALLICON) | (isExactPath ? 0 : SHGFI_USEFILEATTRIBUTES);
            SHGetFileInfo(target, dwAttr, ref sfi, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);

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

    private IImage? ExtractFromImageListByPath(string path, uint dwAttr, uint sfiFlags, int imageListSize)
    {
        try
        {
            var sfi = new SHFILEINFO();
            IntPtr res = SHGetFileInfo(path, dwAttr, ref sfi, (uint)Marshal.SizeOf<SHFILEINFO>(), sfiFlags);
            if (res == IntPtr.Zero) return null;

            return ExtractFromImageListByIndex(sfi.iIcon, imageListSize);
        }
        catch
        {
            return null;
        }
    }

    private IImage? ExtractFromImageListByIndex(int iconIndex, int imageListSize)
    {
        IntPtr hIcon = IntPtr.Zero;
        try
        {
            var iid = IID_IUnknown;
            int hr = SHGetImageList(imageListSize, ref iid, out IntPtr himl);
            if (hr != 0 || himl == IntPtr.Zero) return null;

            hIcon = ImageList_GetIcon(himl, iconIndex, ILD_TRANSPARENT);
            if (hIcon != IntPtr.Zero)
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

    private IImage? ExtractGenericFileIcon(bool isLarge)
    {
        if (isLarge && _defaultGenericLargeIcon != null) return _defaultGenericLargeIcon;
        if (!isLarge && _defaultGenericSmallIcon != null) return _defaultGenericSmallIcon;

        var icon = ExtractFileIcon(".txt", isExactPath: false, isLarge: isLarge)
            ?? ExtractFileIcon(".dat", isExactPath: false, isLarge: isLarge)
            ?? ExtractFileIcon(".log", isExactPath: false, isLarge: isLarge);

        if (isLarge) _defaultGenericLargeIcon = icon;
        else _defaultGenericSmallIcon = icon;

        return icon;
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

            int byteCount = width * height * 4;
            byte[] pixelData = new byte[byteCount];
            Marshal.Copy(pBits, pixelData, 0, byteCount);

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

            // If no alpha channel was written by DrawIconEx, make visible color pixels fully opaque
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

    [DllImport("shell32.dll", EntryPoint = "#727")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IntPtr ppv);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr ImageList_GetIcon(IntPtr himl, int i, uint flags);

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