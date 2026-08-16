using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using ClankerExplorer.Models;
using ClankerExplorer.Services;
using ClankerExplorer.ViewModels;
using Xunit;

namespace ClankerExplorer.Tests;

public class ImagePreviewServiceTests
{
    private static string CreateTestPng(string tempDir, string name, int width, int height)
    {
        Directory.CreateDirectory(tempDir);
        string path = Path.Combine(tempDir, name);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(fs);

        // PNG signature (8 bytes)
        writer.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        // IHDR chunk (13 bytes payload)
        writer.Write(new byte[] { 0x00, 0x00, 0x00, 0x0D }); // length
        writer.Write(new byte[] { (byte)'I', (byte)'H', (byte)'D', (byte)'R' });
        // Width (Big Endian)
        writer.Write((byte)((width >> 24) & 0xFF));
        writer.Write((byte)((width >> 16) & 0xFF));
        writer.Write((byte)((width >> 8) & 0xFF));
        writer.Write((byte)(width & 0xFF));
        // Height (Big Endian)
        writer.Write((byte)((height >> 24) & 0xFF));
        writer.Write((byte)((height >> 16) & 0xFF));
        writer.Write((byte)((height >> 8) & 0xFF));
        writer.Write((byte)(height & 0xFF));
        writer.Write((byte)8); // bit depth
        writer.Write((byte)2); // color type (RGB)
        writer.Write((byte)0); // compression
        writer.Write((byte)0); // filter
        writer.Write((byte)0); // interlace
        writer.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 }); // CRC dummy

        // IEND chunk
        writer.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 });
        writer.Write(new byte[] { (byte)'I', (byte)'E', (byte)'N', (byte)'D' });
        writer.Write(new byte[] { 0xAE, 0x42, 0x60, 0x82 });

        return path;
    }

    private static string CreateTestGif(string tempDir, string name, ushort width, ushort height)
    {
        Directory.CreateDirectory(tempDir);
        string path = Path.Combine(tempDir, name);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(fs);

        // GIF89a signature (6 bytes)
        writer.Write(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' });
        // Logical screen width and height (Little Endian)
        writer.Write(width);
        writer.Write(height);
        writer.Write((byte)0x80); // global color table flag
        writer.Write((byte)0);    // background color index
        writer.Write((byte)0);    // pixel aspect ratio
        // 2-color table (black & white)
        writer.Write(new byte[] { 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF });
        // Trailer
        writer.Write((byte)0x3B);

        return path;
    }

    [AvaloniaFact]
    public async Task ImagePreviewService_DecodesLandscapeImageAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ClankerTest_ImgPreview_" + Guid.NewGuid());
        try
        {
            string imgPath = CreateTestBmp(tempDir, "landscape.bmp", 800, 450);
            var result = await ImagePreviewService.Instance.LoadImagePreviewAsync(imgPath);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.NotNull(result.Bitmap);
            Assert.Equal(800, result.OriginalWidth);
            Assert.Equal(450, result.OriginalHeight);
            Assert.Contains("800 × 450", result.FormattedDimensions);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [AvaloniaFact]
    public async Task ImagePreviewService_DecodesPortraitImageAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ClankerTest_ImgPreview_" + Guid.NewGuid());
        try
        {
            string imgPath = CreateTestBmp(tempDir, "portrait.bmp", 300, 900);
            var result = await ImagePreviewService.Instance.LoadImagePreviewAsync(imgPath);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.NotNull(result.Bitmap);
            Assert.Equal(300, result.OriginalWidth);
            Assert.Equal(900, result.OriginalHeight);
            Assert.Contains("300 × 900", result.FormattedDimensions);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [AvaloniaFact]
    public async Task ImagePreviewService_DownsamplesEnormousImagesToSaveMemory()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ClankerTest_ImgPreview_" + Guid.NewGuid());
        try
        {
            // Create a 3000 x 2000 image
            string imgPath = CreateTestBmp(tempDir, "huge.bmp", 3000, 2000);
            var result = await ImagePreviewService.Instance.LoadImagePreviewAsync(imgPath);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.NotNull(result.Bitmap);
            Assert.Equal(3000, result.OriginalWidth);
            Assert.Equal(2000, result.OriginalHeight);
            Assert.Contains("3000 × 2000", result.FormattedDimensions);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [AvaloniaFact]
    public async Task ImagePreviewService_HandlesCorruptImageGracefully()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ClankerTest_ImgPreview_" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(tempDir);
            string corruptPath = Path.Combine(tempDir, "corrupt.jpg");
            File.WriteAllText(corruptPath, "THIS IS NOT A VALID JPEG DATA STREAM 1234567890");

            var result = await ImagePreviewService.Instance.LoadImagePreviewAsync(corruptPath);
            Assert.False(result.Success);
            Assert.Null(result.Bitmap);
            Assert.NotNull(result.ErrorMessage);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [AvaloniaFact]
    public async Task ImagePreviewService_ReusesCacheOnRepeatedLoad()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ClankerTest_ImgPreview_" + Guid.NewGuid());
        try
        {
            string imgPath = CreateTestBmp(tempDir, "cached.bmp", 400, 400);
            var result1 = await ImagePreviewService.Instance.LoadImagePreviewAsync(imgPath);
            var result2 = await ImagePreviewService.Instance.LoadImagePreviewAsync(imgPath);

            Assert.True(result1.Success);
            Assert.True(result2.Success);
            Assert.Same(result1.Bitmap, result2.Bitmap);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [AvaloniaFact]
    public async Task InspectorViewModel_LoadsImagePreviewAndZoomCommands()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ClankerTest_InspectorImg_" + Guid.NewGuid());
        try
        {
            string imgPath = CreateTestBmp(tempDir, "photo.bmp", 640, 480);
            var vm = new InspectorViewModel();

            await vm.LoadPreviewAsync(imgPath);

            Assert.True(vm.IsImagePreview);
            Assert.NotNull(vm.ImagePreview);
            Assert.Contains("640 × 480", vm.ImageDimensions);
            Assert.True(vm.IsFitMode);
            Assert.Equal("Fit", vm.ZoomPercentDisplay);

            // Zoom In command
            vm.ZoomInCommand.Execute(null);
            Assert.False(vm.IsFitMode);
            Assert.True(vm.ZoomLevel > 1.0);
            Assert.Equal("125%", vm.ZoomPercentDisplay);

            // Zoom Out command
            vm.ZoomOutCommand.Execute(null);
            Assert.Equal(1.0, vm.ZoomLevel);
            Assert.Equal("100%", vm.ZoomPercentDisplay);

            // Reset Zoom command (Fit)
            vm.ResetZoomCommand.Execute(null);
            Assert.True(vm.IsFitMode);
            Assert.Equal("Fit", vm.ZoomPercentDisplay);

            // Toggle Fit or Actual
            vm.ToggleFitOrActualCommand.Execute(null);
            Assert.False(vm.IsFitMode);
            Assert.Equal(1.0, vm.ZoomLevel);
            Assert.Equal("100%", vm.ZoomPercentDisplay);

            vm.ToggleFitOrActualCommand.Execute(null);
            Assert.True(vm.IsFitMode);
            Assert.Equal("Fit", vm.ZoomPercentDisplay);

            // Loading a text file clears image preview
            string txtPath = Path.Combine(tempDir, "notes.txt");
            File.WriteAllText(txtPath, "Hello world!");
            await vm.LoadPreviewAsync(txtPath);

            Assert.False(vm.IsImagePreview);
            Assert.Null(vm.ImagePreview);
            Assert.True(vm.IsTextPreview);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static string CreateTestBmp(string tempDir, string name, int width, int height)
    {
        Directory.CreateDirectory(tempDir);
        string path = Path.Combine(tempDir, name);

        int rowPadding = (4 - ((width * 3) % 4)) % 4;
        int rowStride = (width * 3) + rowPadding;
        int imageSize = rowStride * height;
        int fileSize = 54 + imageSize;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(fs);

        // BITMAPFILEHEADER (14 bytes)
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(fileSize);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write(54);

        // BITMAPINFOHEADER (40 bytes)
        writer.Write(40);
        writer.Write(width);
        writer.Write(height);
        writer.Write((short)1);
        writer.Write((short)24);
        writer.Write(0);
        writer.Write(imageSize);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        byte[] row = new byte[rowStride];
        for (int i = 0; i < width * 3; i += 3)
        {
            row[i] = 180;
            row[i + 1] = 200;
            row[i + 2] = 220;
        }
        for (int y = 0; y < height; y++)
        {
            writer.Write(row);
        }

        return path;
    }

    [AvaloniaFact]
    public async Task ImagePreviewService_DecodesPngDimensionsAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ClankerTest_ImgPreviewPng_" + Guid.NewGuid());
        try
        {
            string imgPath = CreateTestPng(tempDir, "sample.png", 1280, 720);
            var result = await ImagePreviewService.Instance.LoadImagePreviewAsync(imgPath);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(1280, result.OriginalWidth);
            Assert.Equal(720, result.OriginalHeight);
            Assert.Contains("1280 × 720", result.FormattedDimensions);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [AvaloniaFact]
    public async Task ImagePreviewService_DecodesGifDimensionsAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ClankerTest_ImgPreviewGif_" + Guid.NewGuid());
        try
        {
            string imgPath = CreateTestGif(tempDir, "sample.gif", 512, 256);
            var result = await ImagePreviewService.Instance.LoadImagePreviewAsync(imgPath);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(512, result.OriginalWidth);
            Assert.Equal(256, result.OriginalHeight);
            Assert.Contains("512 × 256", result.FormattedDimensions);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [AvaloniaFact]
    public async Task InspectorViewModel_RapidSelectionCancelsPreviousPreviewWithoutStaleFlash()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ClankerTest_InspectorRapid_" + Guid.NewGuid());
        try
        {
            string img1 = CreateTestBmp(tempDir, "img1.bmp", 500, 500);
            string img2 = CreateTestBmp(tempDir, "img2.bmp", 600, 600);
            string img3 = CreateTestBmp(tempDir, "img3.bmp", 700, 700);

            var vm = new InspectorViewModel();

            // Fire rapid loads in quick succession
            var t1 = vm.LoadPreviewAsync(img1);
            var t2 = vm.LoadPreviewAsync(img2);
            var t3 = vm.LoadPreviewAsync(img3);

            await Task.WhenAll(t1, t2, t3);

            Assert.True(vm.IsImagePreview);
            Assert.NotNull(vm.ImagePreview);
            Assert.Contains("700 × 700", vm.ImageDimensions);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
