using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ClankerExplorer.Models;

namespace ClankerExplorer.Services;

/// <summary>
/// High-performance file icon service providing Windows-associated icons with memory caching.
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

    public IImage? GetFileIcon(FileItem item)
    {
        if (item == null || item.IsDirectory) return null;

        var ext = item.Extension ?? string.Empty;
        var fullPath = item.FullPath;

        // For files with unique per-file embedded icons (exes, icos, shortcuts)
        if (PerFileIconExtensions.Contains(ext) && !string.IsNullOrEmpty(fullPath))
        {
            return _fileIconCache.GetOrAdd(fullPath, path => ExtractFileIcon(path, isExactPath: true) ?? GetExtensionIcon(ext));
        }

        return GetExtensionIcon(ext);
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
            var sfi = new SHFILEINFO();
            uint flags = SHGFI_ICON | SHGFI_LARGEICON;

            if (!isExactPath || !File.Exists(target))
            {
                flags |= SHGFI_USEFILEATTRIBUTES;
                string dummyPath = target.StartsWith(".") ? $"dummy{target}" : $"dummy.{target}";
                SHGetFileInfo(dummyPath, FILE_ATTRIBUTE_NORMAL, ref sfi, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
            }
            else
            {
                SHGetFileInfo(target, 0, ref sfi, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
            }

            if (sfi.hIcon != IntPtr.Zero)
            {
                try
                {
                    return ConvertHIconToBitmap(sfi.hIcon, 32, 32);
                }
                finally
                {
                    DestroyIcon(sfi.hIcon);
                }
            }
        }
        catch
        {
            // Fall back gracefully if Shell icon extraction fails
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

    private static Bitmap? ConvertHIconToBitmap(IntPtr hIcon, int width, int height)
    {
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

    #region Win32 P/Invoke

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

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint DI_NORMAL = 0x0003;

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