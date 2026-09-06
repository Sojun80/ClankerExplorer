using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Xunit;
using ClankerExplorer.Models;
using ClankerExplorer.Services.Preview;
using ClankerExplorer.ViewModels;
using ClankerExplorer.Tests.TestInfrastructure;

namespace ClankerExplorer.Tests;

public class PreviewLifecycleAndYieldTests
{
    private class MockPreviewService : IPreviewService
    {
        public List<string> YieldedPaths { get; } = new();
        public bool ShouldOwnFile { get; set; } = true;

        public bool OwnsFile(string? filePath) => ShouldOwnFile;

        public Task YieldFileAsync(string filePath)
        {
            YieldedPaths.Add(filePath);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void NativeVideoPlayer_OwnsFile_ReturnsCorrectly()
    {
        using var player = new NativeVideoPlayer();
        Assert.False(player.OwnsFile(null));
        Assert.False(player.OwnsFile(string.Empty));
        Assert.False(player.OwnsFile(@"C:\Videos\test.mp4"));
    }

    [Fact]
    public async Task NativeVideoPlayer_YieldAsync_WhenNotOwningFile_CompletesCleanly()
    {
        using var player = new NativeVideoPlayer();
        string tempFile = Path.Combine(Path.GetTempPath(), $"test_vid_{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(tempFile, VideoSmokeTests.MinimalMp4Fixture);
        try
        {
            await player.YieldAsync(tempFile);
            Assert.False(player.OwnsFile(tempFile));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [AvaloniaFact]
    public void InspectorViewModel_ImplementsIPreviewService()
    {
        using var inspector = new InspectorViewModel();
        Assert.IsAssignableFrom<IPreviewService>(inspector);
    }

    [AvaloniaFact]
    public async Task InspectorViewModel_YieldFileAsync_StopsPlaybackAndCleansUpSession()
    {
        using var fs = new TemporaryFileSystem();
        string testFile = Path.Combine(fs.FolderA, "sample.txt");
        File.WriteAllText(testFile, "sample text content");

        using var inspector = new InspectorViewModel();
        await inspector.LoadPreviewAsync(testFile);

        // Simulate active video/audio playback session
        inspector.IsVideoPlaying = true;
        inspector.IsVideoSessionActive = true;

        Assert.True(inspector.OwnsFile(testFile));

        // Yield file
        await inspector.YieldFileAsync(testFile);

        Assert.False(inspector.IsVideoPlaying);
        Assert.False(inspector.IsVideoSessionActive);
    }

    [AvaloniaFact]
    public async Task InspectorViewModel_YieldFileAsync_PreventsAutomaticReacquisitionUntilSelectionChanges()
    {
        using var fs = new TemporaryFileSystem();
        string fileA = Path.Combine(fs.FolderA, "fileA.txt");
        string fileB = Path.Combine(fs.FolderA, "fileB.txt");
        File.WriteAllText(fileA, "content A");
        File.WriteAllText(fileB, "content B");

        using var inspector = new InspectorViewModel();
        await inspector.LoadPreviewAsync(fileA);
        Assert.Equal("text", inspector.ActivePreviewType);

        // Simulate playback and yield fileA (e.g. user opens file externally)
        inspector.IsVideoPlaying = true;
        inspector.IsVideoSessionActive = true;
        await inspector.YieldFileAsync(fileA);

        // Spurious selection / focus event for fileA occurs while external player opens
        await inspector.LoadPreviewAsync(fileA);
        Assert.False(inspector.IsVideoPlaying);
        Assert.False(inspector.IsVideoSessionActive);

        // Selection moves to fileB
        await inspector.LoadPreviewAsync(fileB);
        Assert.Equal("text", inspector.ActivePreviewType);

        // User now selects fileA again (new user intent)
        await inspector.LoadPreviewAsync(fileA);
        Assert.Equal("text", inspector.ActivePreviewType);
    }

    [AvaloniaFact]
    public async Task InspectorViewModel_PlayVideo_ResetsYieldGuard()
    {
        using var fs = new TemporaryFileSystem();
        string testFile = Path.Combine(fs.FolderA, "fileA.txt");
        File.WriteAllText(testFile, "content A");

        using var inspector = new InspectorViewModel();
        await inspector.LoadPreviewAsync(testFile);

        // Yield file
        await inspector.YieldFileAsync(testFile);
        Assert.False(inspector.IsVideoPlaying);

        // Explicit user Play interaction resets the yield guard
        inspector.PlayVideo();

        // The yield guard was cleared so another LoadPreviewAsync is accepted
        await inspector.LoadPreviewAsync(testFile);
        Assert.Equal("text", inspector.ActivePreviewType);
    }

    [AvaloniaFact]
    public async Task ExplorerPaneViewModel_OpenItem_YieldsPreviewServiceBeforeOpening()
    {
        using var fs = new TemporaryFileSystem();
        TestEnvironment.ResetGlobalSettings(fs.FolderA);

        using var pane = new ExplorerPaneViewModel("TestPane", fs.FolderA);
        var mockPreview = new MockPreviewService();
        pane.PreviewService = mockPreview;

        // Target file does not exist on disk, verifying yielding happens before launch without spawning external GUI process
        string targetFile = Path.Combine(fs.FolderA, "nonexistent_document.txt");

        var fileItem = new FileItem
        {
            FullPath = targetFile,
            Name = "nonexistent_document.txt",
            IsDirectory = false
        };

        await pane.OpenItem(fileItem);

        Assert.Contains(targetFile, mockPreview.YieldedPaths);
    }

    [AvaloniaFact]
    public async Task ExplorerPaneViewModel_OpenWith_YieldsPreviewServiceBeforeOpening()
    {
        using var fs = new TemporaryFileSystem();
        TestEnvironment.ResetGlobalSettings(fs.FolderA);

        using var pane = new ExplorerPaneViewModel("TestPane", fs.FolderA);
        var mockPreview = new MockPreviewService();
        pane.PreviewService = mockPreview;

        // Path does not exist on disk, so OpenWith will yield first then cleanly exit without showing dialog
        string targetFile = Path.Combine(fs.FolderA, "nonexistent.png");

        var fileItem = new FileItem
        {
            FullPath = targetFile,
            Name = "nonexistent.png",
            IsDirectory = false
        };

        await pane.OpenWith(fileItem);

        Assert.Contains(targetFile, mockPreview.YieldedPaths);
    }

    [AvaloniaFact]
    public async Task ExplorerPaneViewModel_EditItem_YieldsPreviewServiceBeforeOpening()
    {
        using var fs = new TemporaryFileSystem();
        TestEnvironment.ResetGlobalSettings(fs.FolderA);

        using var pane = new ExplorerPaneViewModel("TestPane", fs.FolderA);
        var mockPreview = new MockPreviewService();
        pane.PreviewService = mockPreview;

        // Path does not exist on disk, so EditItem yields first then cleanly exits without launching editor
        string targetFile = Path.Combine(fs.FolderA, "nonexistent.cs");

        var fileItem = new FileItem
        {
            FullPath = targetFile,
            Name = "nonexistent.cs",
            IsDirectory = false
        };
        pane.SelectedTab?.Items.Add(fileItem);
        if (pane.SelectedTab != null) pane.SelectedTab.SelectedItem = fileItem;

        await pane.EditItem();

        Assert.Contains(targetFile, mockPreview.YieldedPaths);
    }

    [AvaloniaFact]
    public async Task InspectorViewModel_YieldFileAsync_UnrelatedFile_DoesNotAffectActivePreview()
    {
        using var fs = new TemporaryFileSystem();
        string fileA = Path.Combine(fs.FolderA, "active_video.mp4");
        string fileB = Path.Combine(fs.FolderA, "unrelated.mp4");
        File.WriteAllBytes(fileA, VideoSmokeTests.MinimalMp4Fixture);
        File.WriteAllBytes(fileB, VideoSmokeTests.MinimalMp4Fixture);

        using var inspector = new InspectorViewModel();
        await inspector.LoadPreviewAsync(fileA);

        inspector.IsVideoPlaying = true;
        inspector.IsVideoSessionActive = true;

        Assert.True(inspector.OwnsFile(fileA));
        Assert.False(inspector.OwnsFile(fileB));

        // Yield unrelated file B
        await inspector.YieldFileAsync(fileB);

        // File A playback and session should remain completely intact
        Assert.True(inspector.IsVideoPlaying);
        Assert.True(inspector.IsVideoSessionActive);
        Assert.True(inspector.OwnsFile(fileA));

        // Clean up active file before disposing temporary directory
        await inspector.YieldFileAsync(fileA);
    }

    [AvaloniaFact]
    public async Task ExplorerPaneViewModel_OpenItem_YieldsBothPreviewAndThumbnailService()
    {
        using var fs = new TemporaryFileSystem();
        TestEnvironment.ResetGlobalSettings(fs.FolderA);

        using var pane = new ExplorerPaneViewModel("TestPane", fs.FolderA);
        var mockPreview = new MockPreviewService();
        pane.PreviewService = mockPreview;

        // Target file does not exist on disk, verifying yielding happens before launch without spawning external GUI process
        string targetFile = Path.Combine(fs.FolderA, "nonexistent_video.mp4");

        var fileItem = new FileItem
        {
            FullPath = targetFile,
            Name = "nonexistent_video.mp4",
            IsDirectory = false
        };

        await pane.OpenItem(fileItem);

        // Both preview and thumbnail services must have been yielded
        Assert.Contains(targetFile, mockPreview.YieldedPaths);
        Assert.True(ClankerExplorer.Services.ThumbnailService.Instance.IsYielded(targetFile));

        ClankerExplorer.Services.ThumbnailService.Instance.ClearYieldGuard(targetFile);
    }

    [Fact]
    public async Task NativeVideoPlayer_YieldAsync_VerifiesExclusiveAccess()
    {
        using var player = new NativeVideoPlayer();
        string tempFile = Path.Combine(Path.GetTempPath(), $"exclusive_test_{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(tempFile, VideoSmokeTests.MinimalMp4Fixture);
        try
        {
            player.Open(tempFile);
            await player.YieldAsync(tempFile);

            // File must be openable with FileShare.None (exclusive access)
            using var fs = new FileStream(tempFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.NotNull(fs);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task VideoThumbnailService_ConcurrentOperations_DoNotCancelEachOther_AndCancelWorkForFileAwaitsCompletion()
    {
        var service = ClankerExplorer.Services.VideoThumbnailService.Instance;
        string testPath = Path.Combine(Path.GetTempPath(), $"vid_multi_{Guid.NewGuid():N}.mp4");

        using var ctsDuration = new CancellationTokenSource();
        var tcsDuration = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.RegisterExtractionForTest(testPath, ctsDuration, tcsDuration.Task, out long opDuration);

        using var ctsFrame = new CancellationTokenSource();
        var tcsFrame = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.RegisterExtractionForTest(testPath, ctsFrame, tcsFrame.Task, out long opFrame);

        // Neither operation should cancel the other
        Assert.False(ctsDuration.IsCancellationRequested);
        Assert.False(ctsFrame.IsCancellationRequested);
        Assert.Equal(2, service.GetActiveExtractionCount(testPath));

        // Start CancelWorkForFileAsync
        var cancelTask = service.CancelWorkForFileAsync(testPath);

        // Both snapshot operations should have been cancelled
        Assert.True(ctsDuration.IsCancellationRequested);
        Assert.True(ctsFrame.IsCancellationRequested);

        // CancelWorkForFileAsync must NOT return until the tracked operations finish
        Assert.False(cancelTask.IsCompleted);

        // Complete the first operation
        tcsDuration.SetResult(true);
        service.UnregisterExtractionForTest(testPath, opDuration);
        await Task.Delay(20);
        Assert.False(cancelTask.IsCompleted);

        // Complete the second operation
        tcsFrame.SetResult(true);
        service.UnregisterExtractionForTest(testPath, opFrame);

        // Now cancelTask should complete
        await cancelTask;
        Assert.True(cancelTask.IsCompletedSuccessfully);
        Assert.Equal(0, service.GetActiveExtractionCount(testPath));
    }

    [Fact]
    public async Task NativeVideoPlayer_YieldA_DoesNotClear_SubsequentOpenB()
    {
        using var player = new NativeVideoPlayer();
        string tempFileA = Path.Combine(Path.GetTempPath(), $"vid_a_{Guid.NewGuid():N}.mp4");
        string tempFileB = Path.Combine(Path.GetTempPath(), $"vid_b_{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(tempFileA, VideoSmokeTests.MinimalMp4Fixture);
        File.WriteAllBytes(tempFileB, VideoSmokeTests.MinimalMp4Fixture);

        try
        {
            // 1. Open A
            bool openA = player.Open(tempFileA);
            Assert.True(openA);
            Assert.True(player.OwnsFile(tempFileA));

            // 2. Start Yield A (which will probe the file off-thread)
            var yieldTaskA = player.YieldAsync(tempFileA);

            // 3. Immediately Open B before Yield A finishes
            bool openB = player.Open(tempFileB);
            Assert.True(openB);
            Assert.True(player.OwnsFile(tempFileB));

            // 4. Await Yield A completion
            await yieldTaskA;

            // 5. Yield A completion must NOT clear B's ownership or media
            Assert.True(player.OwnsFile(tempFileB));
            Assert.False(player.OwnsFile(tempFileA));
            Assert.NotNull(player.VlcMediaPlayer?.Media);

            // Cleanup B
            await player.YieldAsync(tempFileB);
            Assert.False(player.OwnsFile(tempFileB));
        }
        finally
        {
            if (File.Exists(tempFileA)) File.Delete(tempFileA);
            if (File.Exists(tempFileB)) File.Delete(tempFileB);
        }
    }

    [Fact]
    public async Task NativeVideoPlayer_RapidLifecycleInterleavings_NoCrashOrObjectDisposedException()
    {
        using var player = new NativeVideoPlayer();
        string tempFileA = Path.Combine(Path.GetTempPath(), $"stress_a_{Guid.NewGuid():N}.mp4");
        string tempFileB = Path.Combine(Path.GetTempPath(), $"stress_b_{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(tempFileA, VideoSmokeTests.MinimalMp4Fixture);
        File.WriteAllBytes(tempFileB, VideoSmokeTests.MinimalMp4Fixture);

        try
        {
            for (int i = 0; i < 20; i++)
            {
                player.Open(tempFileA);
                player.Close();
                player.Open(tempFileB);
                var yTask = player.YieldAsync(tempFileB);
                player.Open(tempFileA);
                player.Play();
                player.Stop();
                await yTask;
            }

            player.Close();
        }
        finally
        {
            if (File.Exists(tempFileA)) File.Delete(tempFileA);
            if (File.Exists(tempFileB)) File.Delete(tempFileB);
        }
    }
}
