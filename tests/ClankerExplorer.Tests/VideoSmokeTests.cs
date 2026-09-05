using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Xunit;
using ClankerExplorer.Models;
using ClankerExplorer.Services;
using ClankerExplorer.Services.Preview;
using ClankerExplorer.ViewModels;
using ClankerExplorer.Tests.TestInfrastructure;

namespace ClankerExplorer.Tests;

public sealed class VideoSmokeTests : IDisposable
{
    // Genuine valid ISO base media file format H.264 MP4 fixture (ftyp + moov + valid tracks)
    internal static readonly byte[] MinimalMp4Fixture = Convert.FromBase64String(
        "AAAAIGZ0eXBpc29tAAACAGlzb21pc28yYXZjMW1wNDEAAAPXbW9vdgAAAGxtdmhkAAAAAAAAAAAAAAAAAAAD6AAAAggAAQAAAQAAAAAAAAAAAA" +
        "AAAAEAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAgAAAwF0cmFrAAAAXHRraGQAAA" +
        "ADAAAAAAAAAAAAAAABAAAAAAAAAggAAAAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAABAAAAAAKAAAAB4AAAAAA" +
        "AkZWR0cwAAABxlbHN0AAAAAAAAAAEAAAIIAAAEAAABAAAAAAJ5bWRpYQAAACBtZGhkAAAAAAAAAAAAAAAAAAAyAAAAGgBVxAAAAAAALWhkbHIAAA" +
        "AAAAAAAHZpZGUAAAAAAAAAAAAAAABWaWRlb0hhbmRsZXIAAAACJG1pbmYAAAAUdm1oZAAAAAEAAAAAAAAAAAAAACRkaW5mAAAAHGRyZWYAAAAAAA" +
        "AAAQAAAAx1cmwgAAAAAQAAAeRzdGJsAAAAwHN0c2QAAAAAAAAAAQAAALBhdmMxAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAAAAKAAeABIAAAASAAAAA" +
        "AAAAABFUxhdmM2Mi4yOC4xMDEgbGlieDI2NAAAAAAAAAAAAAAAGP//AAAANmF2Y0MBZAAL/+EAGWdkAAus2UKEflwEQAAAAwBAAAAMg8UKZYABAA" +
        "Zo6+PEyEz9+PgAAAAAEHBhc3AAAAABAAAAAQAAABRidHJ0AAAAAAAAOMoAAAAAAAAAGHN0dHMAAAAAAAAAAQAAAA0AAAIAAAAAFHN0c3MAAAAAAA" +
        "AAAQAAAAEAAAB4Y3R0cwAAAAAAAAANAAAAAQAABAAAAAABAAAKAAAAAAEAAAQAAAAAAQAAAAAAAAABAAACAAAAAAEAAAoAAAAAAQAABAAAAAABAA" +
        "AAAAAAAAEAAAIAAAAAAQAACgAAAAABAAAEAAAAAAEAAAAAAAAAAQAAAgAAAAAcc3RzYwAAAAAAAAABAAAAAQAAAA0AAAABAAAASHN0c3oAAAAAAAA" +
        "AAAAAAA0AAALwAAAAEQAAAA4AAAAOAAAADgAAABcAAAAQAAAADgAAAA4AAAAXAAAAEAAAAA4AAAAOAAAAFHN0Y28AAAAAAAAAAQAABAcAAABidWR0" +
        "YQAAAFptZXRhAAAAAAAAACFoZGxyAAAAAAAAAABtZGlyYXBwbAAAAAAAAAAAAAAAAC1pbHN0AAAAJal0b28AAAAdZGF0YQAAAAEAAAAATGF2ZjYy" +
        "LjEyLjEwMQAAAAhmcmVlAAADuW1kYXQAAAKwBgX//6zcRem95tlIt5Ys2CDZI+7veDI2NCAtIGNvcmUgMTY1IHIzMjIzIDA0ODBjYjAgLSBILjI2" +
        "NC9NUEVHLTQgQVZDIGNvZGVjIC0gQ29weWxlZnQgMjAwMy0yMDI1IC0gaHR0cDovL3d3dy52aWRlb2xhbi5vcmcveDI2NC5odG1sIC0gb3B0aW9u" +
        "czogY2FiYWM9MSByZWY9MyBkZWJsb2NrPTE6LTM6LTMgYW5hbHlzZT0weDM6MHgxMTMgbWU9aGV4IHN1Ym1lPTcgcHN5PTEgcHN5X3JkPTIuMDA6" +
        "MC43MCBtaXhlZF9yZWY9MSBtZV9yYW5nZT0xNiBjaHJvbWFfbWU9MSB0cmVsbGlzPTEgOHg4ZGN0PTEgY3FtPTAgZGVhZHpvbmU9MjEsMTEgZmFz" +
        "dF9wc2tpcD0xIGNocm9tYV9xcF9vZmZzZXQ9LTQgdGhyZWFkcz00IGxvb2thaGVhZF90aHJlYWRzPTEgc2xpY2VkX3RocmVhZHM9MCBucj0wIGRl" +
        "Y2ltYXRlPTEgaW50ZXJsYWNlZD0wIGJsdXJheV9jb21wYXQ9MCBjb25zdHJhaW5lZF9pbnRyYT0wIGJmcmFtZXM9MyBiX3B5cmFtaWQ9MiBiX2Fk" +
        "YXB0PTEgYl9iaWFzPTAgZGlyZWN0PTEgd2VpZ2h0Yj0xIG9wZW5fZ29wPTAgd2VpZ2h0cD0yIGtleWludD0yNTAga2V5aW50X21pbj0yNSBzY2Vu" +
        "ZWN1dD00MCBpbnRyYV9yZWZyZXNoPTAgcmNfbG9va2FoZWFkPTQwIHJjPWNyZiBtYnRyZWU9MSBjcmY9MjMuMCBxY29tcD0wLjYwIHFwbWluPTAg" +
        "cXBtYXg9NjkgcXBzdGVwPTQgaXBfcmF0aW89MS40MCBhcT0xOjEuMjAAgAAAADhliIQAEc5//ufj/AptfMRxO9CSPwY9bXcqLo14dXGIU8gurtjq" +
        "jfh3cMtTgUH12BEgAF7BrrBFMQAAAA1BmiRsQQzn/qpVAB9wAAAACkGeQniHZz8APWEAAAAKAZ5hdENznwBZQAAAAAoBnmNqQ3OfAFlBAAAAE0Gaa" +
        "EmoQWiZTAh+c//+qZYAesEAAAAMQZ6GRREsOzn/AD1hAAAACgGepXRDc58AWUEAAAAKAZ6nakNznwBZQAAAABNBmqxJqEFsmUwIbnP//qeEAPOAAA" +
        "AADEGeykUVLDs5/wA9YQAAAAoBnul0Q3OfAFlAAAAACgGe62pDc58AWUA=");

    [AvaloniaFact]
    public async Task RealVideoFile_OpenAndYield_EnsuresExclusiveAccess()
    {
        string tempMp4 = Path.Combine(Path.GetTempPath(), $"smoke_vid_{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(tempMp4, MinimalMp4Fixture);

        try
        {
            using var player = new NativeVideoPlayer();
            bool opened = player.Open(tempMp4);

            // Yield ownership
            await player.YieldAsync(tempMp4);

            Assert.False(player.OwnsFile(tempMp4));

            // Verify file can be opened with exclusive read/write access (FileShare.None)
            using var exclusiveFs = new FileStream(tempMp4, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.NotNull(exclusiveFs);
            Assert.True(exclusiveFs.CanRead);
            Assert.True(exclusiveFs.CanWrite);
        }
        finally
        {
            if (File.Exists(tempMp4))
            {
                try { File.Delete(tempMp4); } catch { }
            }
        }
    }

    [AvaloniaFact]
    public async Task InspectorViewModel_RealVideoFile_YieldReleasesExclusiveLock()
    {
        string tempMp4 = Path.Combine(Path.GetTempPath(), $"inspector_smoke_{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(tempMp4, MinimalMp4Fixture);

        try
        {
            using var inspector = new InspectorViewModel();
            await inspector.LoadPreviewAsync(tempMp4);

            Assert.True(inspector.OwnsFile(tempMp4));

            // Yield file ownership
            await inspector.YieldFileAsync(tempMp4);

            Assert.False(inspector.OwnsFile(tempMp4));

            // Verify exclusive access can be acquired by another process
            using var exclusiveFs = new FileStream(tempMp4, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.NotNull(exclusiveFs);
        }
        finally
        {
            if (File.Exists(tempMp4))
            {
                try { File.Delete(tempMp4); } catch { }
            }
        }
    }

    [AvaloniaFact]
    public async Task PrepareForExternalOpenAsync_YieldsAllSubsystems_EnsuringExclusiveAccess()
    {
        using var fs = new TemporaryFileSystem();
        TestEnvironment.ResetGlobalSettings(fs.FolderA);

        string videoPath = Path.Combine(fs.FolderA, "external_launch.mp4");
        File.WriteAllBytes(videoPath, MinimalMp4Fixture);

        using var pane = new ExplorerPaneViewModel("SmokePane", fs.FolderA);
        using var inspector = new InspectorViewModel();
        pane.PreviewService = inspector;

        await inspector.LoadPreviewAsync(videoPath);
        Assert.True(inspector.OwnsFile(videoPath));

        // Coordinate external open preparation
        await pane.PrepareForExternalOpenAsync(videoPath);

        // Preview and thumbnail must both have yielded
        Assert.False(inspector.OwnsFile(videoPath));
        Assert.True(ThumbnailService.Instance.IsYielded(videoPath));

        // Verify exclusive lock can be acquired immediately by external process
        using var exclusiveFs = new FileStream(videoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.NotNull(exclusiveFs);

        ThumbnailService.Instance.ClearYieldGuard(videoPath);
    }

    public void Dispose() => TestEnvironment.ResetGlobalSettings(TestEnvironment.DefaultFolder);
}
