using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace ClankerExplorer.Services.Metadata.Providers;

/// <summary>
/// Extracts image metadata: dimensions, format, bit depth, DPI, aspect ratio, and camera/EXIF metadata.
/// </summary>
public class ImageMetadataProvider : IMetadataProvider
{
    public int Order => 10;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tiff", ".tif", ".ico", ".svg"
    };

    public bool CanHandle(MetadataExtractionContext context)
    {
        return !context.IsDirectory && ImageExtensions.Contains(context.Extension);
    }

    public async Task ProvideMetadataAsync(MetadataExtractionContext context, CancellationToken cancellationToken)
    {
        string path = context.FilePath;
        if (!File.Exists(path)) return;

        uint width = 0;
        uint height = 0;
        uint bitDepth = 0;
        double dpiX = 0;
        double dpiY = 0;
        string format = GetFormatName(context.Extension);

        // EXIF
        DateTimeOffset dateTaken = default;
        string? camera = null;
        string? lens = null;
        string? exposureTime = null;
        string? fNumber = null;
        string? iso = null;
        string? focalLength = null;

        // 1. Fast stream header dimension extraction
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var (w, h) = TryReadHeaderDimensions(fs, context.Extension);
            if (w > 0 && h > 0)
            {
                width = (uint)w;
                height = (uint)h;
            }
        }
        catch { }

        // 2. Windows Storage & ImageProperties + EXIF System Properties
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var storageFile = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken).ConfigureAwait(false);
                var imgProps = await storageFile.Properties.GetImagePropertiesAsync().AsTask(cancellationToken).ConfigureAwait(false);

                if (imgProps != null)
                {
                    if (width == 0) width = imgProps.Width;
                    if (height == 0) height = imgProps.Height;
                    dateTaken = imgProps.DateTaken;

                    string make = imgProps.CameraManufacturer ?? "";
                    string model = imgProps.CameraModel ?? "";
                    if (!string.IsNullOrWhiteSpace(model))
                    {
                        camera = model.StartsWith(make, StringComparison.OrdinalIgnoreCase) ? model : $"{make} {model}".Trim();
                    }
                }

                var extra = await storageFile.Properties.RetrievePropertiesAsync(new[]
                {
                    "System.Image.BitDepth",
                    "System.Image.HorizontalResolution",
                    "System.Image.VerticalResolution",
                    "System.Photo.ExposureTime",
                    "System.Photo.FNumber",
                    "System.Photo.ISOSpeed",
                    "System.Photo.FocalLength",
                    "System.Photo.LensModel"
                }).AsTask(cancellationToken).ConfigureAwait(false);

                if (extra != null)
                {
                    if (extra.TryGetValue("System.Image.BitDepth", out var bdVal) && bdVal is uint bd)
                    {
                        bitDepth = bd;
                    }
                    if (extra.TryGetValue("System.Image.HorizontalResolution", out var hrVal) && hrVal != null)
                    {
                        if (hrVal is double dX) dpiX = dX;
                        else if (hrVal is uint uX) dpiX = uX;
                    }
                    if (extra.TryGetValue("System.Image.VerticalResolution", out var vrVal) && vrVal != null)
                    {
                        if (vrVal is double dY) dpiY = dY;
                        else if (vrVal is uint uY) dpiY = uY;
                    }

                    if (extra.TryGetValue("System.Photo.ExposureTime", out var expVal) && expVal is double exp && exp > 0)
                    {
                        exposureTime = exp < 1.0 ? $"1/{(int)Math.Round(1.0 / exp)} sec" : $"{exp:F1} sec";
                    }

                    if (extra.TryGetValue("System.Photo.FNumber", out var fVal) && fVal is double fn && fn > 0)
                    {
                        fNumber = $"f/{fn:F1}";
                    }

                    if (extra.TryGetValue("System.Photo.ISOSpeed", out var isoVal) && isoVal != null)
                    {
                        iso = $"ISO {isoVal}";
                    }

                    if (extra.TryGetValue("System.Photo.FocalLength", out var flVal) && flVal is double fl && fl > 0)
                    {
                        focalLength = $"{fl:F1} mm";
                    }

                    if (extra.TryGetValue("System.Photo.LensModel", out var lmVal) && lmVal is string lm && !string.IsNullOrWhiteSpace(lm))
                    {
                        lens = lm.Trim();
                    }
                }
            }
            catch { }
        }

        // Add fields to Image Section
        if (width > 0 && height > 0)
        {
            double mp = (double)width * height / 1_000_000.0;
            string dimText = mp >= 0.1 ? $"{width} × {height} ({mp:F1} MP)" : $"{width} × {height}";
            context.AddItem("Image", "🖼️", "Dimensions", dimText, isCopyable: true, isMonospace: true);

            string aspect = GetAspectRatio(width, height);
            context.AddItem("Image", "🖼️", "Aspect Ratio", aspect, isCopyable: true);
        }

        context.AddItem("Image", "🖼️", "Format", format, isCopyable: true);

        if (bitDepth > 0)
        {
            context.AddItem("Image", "🖼️", "Bit Depth", $"{bitDepth}-bit", isCopyable: true);
        }

        if (dpiX > 0 && dpiY > 0)
        {
            string dpiText = Math.Abs(dpiX - dpiY) < 1.0 ? $"{dpiX:F0} DPI" : $"{dpiX:F0} × {dpiY:F0} DPI";
            context.AddItem("Image", "🖼️", "Resolution", dpiText, isCopyable: true, isMonospace: true);
        }

        // EXIF metadata
        if (dateTaken != default && dateTaken != DateTimeOffset.MinValue)
        {
            context.AddItem("Image", "🖼️", "Date Taken", dateTaken.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"), isCopyable: true, isMonospace: true);
        }

        if (!string.IsNullOrEmpty(camera))
        {
            context.AddItem("Image", "🖼️", "Camera", camera, isCopyable: true);
        }

        if (!string.IsNullOrEmpty(lens))
        {
            context.AddItem("Image", "🖼️", "Lens", lens, isCopyable: true);
        }

        if (!string.IsNullOrEmpty(fNumber) || !string.IsNullOrEmpty(exposureTime) || !string.IsNullOrEmpty(iso) || !string.IsNullOrEmpty(focalLength))
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(focalLength)) parts.Add(focalLength);
            if (!string.IsNullOrEmpty(fNumber)) parts.Add(fNumber);
            if (!string.IsNullOrEmpty(exposureTime)) parts.Add(exposureTime);
            if (!string.IsNullOrEmpty(iso)) parts.Add(iso);

            context.AddItem("Image", "🖼️", "Exposure", string.Join(" • ", parts), isCopyable: true);
        }
    }

    private static string GetFormatName(string ext)
    {
        return ext.ToLowerInvariant() switch
        {
            ".png" => "Portable Network Graphics (PNG)",
            ".jpg" or ".jpeg" => "JPEG Image",
            ".webp" => "WebP Image",
            ".gif" => "Graphics Interchange Format (GIF)",
            ".bmp" => "Windows Bitmap (BMP)",
            ".tiff" or ".tif" => "Tagged Image File Format (TIFF)",
            ".ico" => "Windows Icon (ICO)",
            ".svg" => "Scalable Vector Graphics (SVG)",
            _ => ext.ToUpperInvariant().TrimStart('.')
        };
    }

    private static string GetAspectRatio(uint w, uint h)
    {
        if (w == 0 || h == 0) return "—";
        double ratio = (double)w / h;

        if (Math.Abs(ratio - 16.0 / 9.0) < 0.03) return "16:9 (Widescreen)";
        if (Math.Abs(ratio - 4.0 / 3.0) < 0.03) return "4:3 (Standard)";
        if (Math.Abs(ratio - 3.0 / 2.0) < 0.03) return "3:2 (35mm Classic)";
        if (Math.Abs(ratio - 1.0) < 0.02) return "1:1 (Square)";
        if (Math.Abs(ratio - 21.0 / 9.0) < 0.04) return "21:9 (Ultrawide)";
        if (Math.Abs(ratio - 9.0 / 16.0) < 0.03) return "9:16 (Vertical)";

        uint gcd = Gcd(w, h);
        uint rw = w / gcd;
        uint rh = h / gcd;
        if (rw <= 32 && rh <= 32) return $"{rw}:{rh}";
        return $"{ratio:F2}:1";
    }

    private static uint Gcd(uint a, uint b)
    {
        while (b != 0)
        {
            uint temp = b;
            b = a % b;
            a = temp;
        }
        return a;
    }

    private static (int width, int height) TryReadHeaderDimensions(Stream stream, string ext)
    {
        try
        {
            stream.Seek(0, SeekOrigin.Begin);
            byte[] buffer = new byte[64];
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read < 8) return (0, 0);

            // PNG
            if (buffer[0] == 0x89 && buffer[1] == 0x50 && buffer[2] == 0x4E && buffer[3] == 0x47 && read >= 24)
            {
                int w = (buffer[16] << 24) | (buffer[17] << 16) | (buffer[18] << 8) | buffer[19];
                int h = (buffer[20] << 24) | (buffer[21] << 16) | (buffer[22] << 8) | buffer[23];
                return (w, h);
            }

            // BMP
            if (buffer[0] == 0x42 && buffer[1] == 0x4D && read >= 26)
            {
                int w = buffer[18] | (buffer[19] << 8) | (buffer[20] << 16) | (buffer[21] << 24);
                int h = buffer[22] | (buffer[23] << 8) | (buffer[24] << 16) | (buffer[25] << 24);
                return (Math.Abs(w), Math.Abs(h));
            }

            // GIF
            if (buffer[0] == 0x47 && buffer[1] == 0x49 && buffer[2] == 0x46 && read >= 10)
            {
                int w = buffer[6] | (buffer[7] << 8);
                int h = buffer[8] | (buffer[9] << 8);
                return (w, h);
            }

            // WEBP
            if (buffer[0] == 0x52 && buffer[1] == 0x49 && buffer[2] == 0x46 && buffer[3] == 0x46 && read >= 30 &&
                buffer[8] == 0x57 && buffer[9] == 0x45 && buffer[10] == 0x42 && buffer[11] == 0x50)
            {
                if (buffer[12] == 0x56 && buffer[13] == 0x50 && buffer[14] == 0x38 && buffer[15] == 0x20 && read >= 30)
                {
                    int w = (buffer[26] | (buffer[27] << 8)) & 0x3FFF;
                    int h = (buffer[28] | (buffer[29] << 8)) & 0x3FFF;
                    return (w, h);
                }
                if (buffer[12] == 0x56 && buffer[13] == 0x50 && buffer[14] == 0x38 && buffer[15] == 0x4C && read >= 25)
                {
                    int b0 = buffer[21], b1 = buffer[22], b2 = buffer[23], b3 = buffer[24];
                    int w = 1 + (((b1 & 0x3F) << 8) | b0);
                    int h = 1 + (((b3 & 0xF) << 10) | (b2 << 2) | ((b1 & 0xC0) >> 6));
                    return (w, h);
                }
                if (buffer[12] == 0x56 && buffer[13] == 0x50 && buffer[14] == 0x38 && buffer[15] == 0x58 && read >= 30)
                {
                    int w = 1 + (buffer[24] | (buffer[25] << 8) | (buffer[26] << 16));
                    int h = 1 + (buffer[27] | (buffer[28] << 8) | (buffer[29] << 16));
                    return (w, h);
                }
            }
        }
        catch { }

        return (0, 0);
    }
}
