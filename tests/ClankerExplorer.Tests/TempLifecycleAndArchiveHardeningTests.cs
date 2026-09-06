using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClankerExplorer.AppLayer.Operations;
using ClankerExplorer.Models;
using ClankerExplorer.Services;
using ClankerExplorer.Tests.TestInfrastructure;
using Xunit;

namespace ClankerExplorer.Tests;

public sealed class TempLifecycleAndArchiveHardeningTests
{
    [Fact]
    public void TempClassification_IdentifiesScratchVersusRecoveryVersusArbitraryFiles()
    {
        var guidN = Guid.NewGuid().ToString("N");
        var guidD = Guid.NewGuid().ToString("D");

        // 1. Disposable Scratch: exact .clanker-transfer-{GUID}.tmp form
        var scratchN = $@"C:\Temp\.clanker-transfer-{guidN}.tmp";
        var scratchD = $@"C:\Temp\.clanker-transfer-{guidD}.tmp";
        Assert.True(TransferEngine.IsInternalTransferScratch(scratchN));
        Assert.True(TransferEngine.IsInternalTransferScratch(scratchD));
        Assert.True(TransferEngine.IsInternalClankerTemp(scratchN));
        Assert.False(TransferEngine.IsInternalRecoveryTemp(scratchN));

        // 2. Recovery Temp: exact .clanker-transfer-{GUID}.bak form
        var bakN = $@"C:\Temp\.clanker-transfer-{guidN}.bak";
        var bakD = $@"C:\Temp\.clanker-transfer-{guidD}.bak";
        Assert.True(TransferEngine.IsInternalRecoveryTemp(bakN));
        Assert.True(TransferEngine.IsInternalRecoveryTemp(bakD));
        Assert.True(TransferEngine.IsInternalClankerTemp(bakN));
        Assert.False(TransferEngine.IsInternalTransferScratch(bakN));

        // 3. Recovery Temp: valid generated .c_tmp_{Guid}_{originalName} form
        var renameTempN = $@"C:\Temp\.c_tmp_{guidN}_my_document.txt";
        var renameTempD = $@"C:\Temp\.c_tmp_{guidD}_my_document.txt";
        Assert.True(TransferEngine.IsInternalRecoveryTemp(renameTempN));
        Assert.True(TransferEngine.IsInternalRecoveryTemp(renameTempD));
        Assert.True(TransferEngine.IsInternalClankerTemp(renameTempN));
        Assert.False(TransferEngine.IsInternalTransferScratch(renameTempN));

        // 4. Malformed or arbitrary user files must NEVER be classified as Clanker temp
        var malformedScratch = @"C:\Temp\.clanker-transfer-not-a-guid.tmp";
        var malformedBak = @"C:\Temp\.clanker-transfer-not-a-guid.bak";
        var userNotes = @"C:\Temp\.c_tmp_user_notes.txt";
        var ordinary = @"C:\Temp\ordinary.txt";
        var emptyGuidScratch = @"C:\Temp\.clanker-transfer-.tmp";

        Assert.False(TransferEngine.IsInternalTransferScratch(malformedScratch));
        Assert.False(TransferEngine.IsInternalTransferScratch(malformedBak));
        Assert.False(TransferEngine.IsInternalTransferScratch(userNotes));
        Assert.False(TransferEngine.IsInternalTransferScratch(ordinary));
        Assert.False(TransferEngine.IsInternalTransferScratch(emptyGuidScratch));

        Assert.False(TransferEngine.IsInternalRecoveryTemp(malformedScratch));
        Assert.False(TransferEngine.IsInternalRecoveryTemp(malformedBak));
        Assert.False(TransferEngine.IsInternalRecoveryTemp(userNotes));
        Assert.False(TransferEngine.IsInternalRecoveryTemp(ordinary));
        Assert.False(TransferEngine.IsInternalRecoveryTemp(emptyGuidScratch));

        Assert.False(TransferEngine.IsInternalClankerTemp(malformedScratch));
        Assert.False(TransferEngine.IsInternalClankerTemp(malformedBak));
        Assert.False(TransferEngine.IsInternalClankerTemp(userNotes));
        Assert.False(TransferEngine.IsInternalClankerTemp(ordinary));
        Assert.False(TransferEngine.IsInternalClankerTemp(emptyGuidScratch));
    }

    [Fact]
    public void ScratchSweep_DeletesDisposableScratch_PreservesRecoveryAndUserFiles()
    {
        using var fs = new TemporaryFileSystem();
        var dir = fs.FolderA;

        var validGuid = Guid.NewGuid().ToString("N");
        var scratchFile = Path.Combine(dir, $".clanker-transfer-{validGuid}.tmp");
        var scratchDir = Path.Combine(dir, $".clanker-transfer-{Guid.NewGuid():N}.tmp");
        var bakFile = Path.Combine(dir, $".clanker-transfer-{validGuid}.bak");
        var renameTemp = Path.Combine(dir, $".c_tmp_{validGuid}_precious.txt");
        var ordinaryFile = Path.Combine(dir, "ordinary.txt");
        var malformedTmp = Path.Combine(dir, ".clanker-transfer-not-a-guid.tmp");
        var userNotes = Path.Combine(dir, ".c_tmp_user_notes.txt");

        File.WriteAllText(scratchFile, "disposable scratch");
        Directory.CreateDirectory(scratchDir);
        File.WriteAllText(Path.Combine(scratchDir, "child.txt"), "staged file");
        File.WriteAllText(bakFile, "recovery backup - must preserve");
        File.WriteAllText(renameTemp, "phase 1 rename - must preserve");
        File.WriteAllText(ordinaryFile, "user file");
        File.WriteAllText(malformedTmp, "not a guid tmp");
        File.WriteAllText(userNotes, "user notes");

        // Run safe sweep
        TransferEngine.DeleteLeftoverTransferScratchFiles(dir);

        // Valid scratch must be deleted
        Assert.False(File.Exists(scratchFile), "Valid disposable scratch file should have been deleted.");
        Assert.False(Directory.Exists(scratchDir), "Valid disposable scratch directory should have been deleted.");

        // Recovery and user files must survive
        Assert.True(File.Exists(bakFile), "Recovery .bak file must be preserved.");
        Assert.True(File.Exists(renameTemp), "Recovery .c_tmp_ rename file must be preserved.");
        Assert.True(File.Exists(ordinaryFile), "Ordinary user file must be preserved.");
        Assert.True(File.Exists(malformedTmp), "Malformed .tmp file must be preserved.");
        Assert.True(File.Exists(userNotes), "User file with .c_tmp_ prefix must be preserved.");
    }

    [Fact]
    public void ScratchSweep_PreservesActivelyRegisteredScratchFiles()
    {
        using var fs = new TemporaryFileSystem();
        var dir = fs.FolderA;

        var validGuid = Guid.NewGuid().ToString("N");
        var activeScratch = Path.Combine(dir, $".clanker-transfer-{validGuid}.tmp");
        File.WriteAllText(activeScratch, "in-flight data");

        // Register as active
        TransferEngine.RegisterActiveTempFile(activeScratch);
        try
        {
            TransferEngine.DeleteLeftoverTransferScratchFiles(dir);
            Assert.True(File.Exists(activeScratch), "Actively registered scratch file must not be swept.");
        }
        finally
        {
            TransferEngine.UnregisterActiveTempFile(activeScratch);
        }

        // After unregistering, it is abandoned and should be swept
        TransferEngine.DeleteLeftoverTransferScratchFiles(dir);
        Assert.False(File.Exists(activeScratch), "Unregistered scratch file should be swept.");
    }

    [Fact]
    public void BatchRename_ValidSwapCycle_Succeeds()
    {
        using var fs = new TemporaryFileSystem();
        var fileA = Path.Combine(fs.FolderA, "A.txt");
        var fileB = Path.Combine(fs.FolderA, "B.txt");
        File.WriteAllText(fileA, "Content A");
        File.WriteAllText(fileB, "Content B");

        var service = new FileSystemService();
        var items = new List<BatchRenameItem>
        {
            new() { OriginalPath = fileA, OriginalName = "A.txt", NewName = "B.txt", NewPath = fileB },
            new() { OriginalPath = fileB, OriginalName = "B.txt", NewName = "A.txt", NewPath = fileA }
        };

        var result = service.ExecuteBatchRenameSafe(items);

        Assert.True(result.success, result.message);
        Assert.Equal(2, result.renamedCount);
        Assert.Equal("Content A", File.ReadAllText(fileB));
        Assert.Equal("Content B", File.ReadAllText(fileA));
    }

    [Fact]
    public void BatchRename_ExternalExistingCollision_FailsBeforePhase1WithoutMovingSources()
    {
        using var fs = new TemporaryFileSystem();
        var fileA = Path.Combine(fs.FolderA, "A.txt");
        var fileC = Path.Combine(fs.FolderA, "C.txt");
        File.WriteAllText(fileA, "Content A");
        File.WriteAllText(fileC, "Content C");

        var service = new FileSystemService();
        // A wants to rename to C, but C exists and is not participating in the batch
        var items = new List<BatchRenameItem>
        {
            new() { OriginalPath = fileA, OriginalName = "A.txt", NewName = "C.txt", NewPath = fileC }
        };

        var result = service.ExecuteBatchRenameSafe(items);

        Assert.False(result.success);
        Assert.Contains("collision", result.message, StringComparison.OrdinalIgnoreCase);

        // Neither file should have been moved or modified
        Assert.True(File.Exists(fileA));
        Assert.True(File.Exists(fileC));
        Assert.Equal("Content A", File.ReadAllText(fileA));
        Assert.Equal("Content C", File.ReadAllText(fileC));

        // No leftover .c_tmp_* files should exist in the directory
        var leftovers = Directory.GetFiles(fs.FolderA, ".c_tmp_*");
        Assert.Empty(leftovers);
    }

    [Fact]
    public async Task ManagedZip_ExtractWithOverwriteFalse_DoesNotClobberExistingFile()
    {
        using var fs = new TemporaryFileSystem();
        var archivePath = Path.Combine(fs.FolderB, "test.zip");
        var destination = fs.CreateDirectory("FolderB/extracted");
        var existingFile = Path.Combine(destination, "data.txt");
        File.WriteAllText(existingFile, "Original Content");

        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("data.txt");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("Archive Content");
        }

        var result = await ArchiveService.Instance.ExtractManagedZipAsync(archivePath, destination, overwrite: false, CancellationToken.None);

        Assert.True(result.success, result.message);
        Assert.Equal("Original Content", File.ReadAllText(existingFile));
    }

    [Fact]
    public async Task ManagedZip_ExtractWithOverwriteTrue_ReplacesExistingFile()
    {
        using var fs = new TemporaryFileSystem();
        var archivePath = Path.Combine(fs.FolderB, "test_overwrite.zip");
        var destination = fs.CreateDirectory("FolderB/extracted_overwrite");
        var existingFile = Path.Combine(destination, "data.txt");
        File.WriteAllText(existingFile, "Original Content");

        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("data.txt");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("Archive Content");
        }

        var result = await ArchiveService.Instance.ExtractManagedZipAsync(archivePath, destination, overwrite: true, CancellationToken.None);

        Assert.True(result.success, result.message);
        Assert.Equal("Archive Content", File.ReadAllText(existingFile));

        // Ensure no sibling temp files remain
        var siblingTemps = Directory.GetFiles(destination, ".clanker-transfer-*.tmp");
        Assert.Empty(siblingTemps);
    }

    [Fact]
    public async Task ManagedZip_Cancellation_RethrowsAndCleansTempWithoutCorruptingDestination()
    {
        using var fs = new TemporaryFileSystem();
        var archivePath = Path.Combine(fs.FolderB, "cancel_test.zip");
        var destination = fs.CreateDirectory("FolderB/extracted_cancel");
        var existingFile = Path.Combine(destination, "file1.txt");
        File.WriteAllText(existingFile, "Existing Data");

        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("file1.txt");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync(new string('X', 10000));
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await ArchiveService.Instance.ExtractManagedZipAsync(archivePath, destination, overwrite: true, cts.Token);
        });

        // Destination must be intact
        Assert.Equal("Existing Data", File.ReadAllText(existingFile));

        // No leftover temp files
        var temps = Directory.GetFiles(destination, ".clanker-transfer-*.tmp");
        Assert.Empty(temps);
    }

    [Fact]
    public void PromoteStagingDirectory_HonorsOverwriteAndCreatesMissingDirectories()
    {
        using var fs = new TemporaryFileSystem();
        var staging = fs.CreateDirectory("FolderA/staging");
        var dest = fs.CreateDirectory("FolderB/dest");

        // Set up staging files and subdirectories
        var subDir = Path.Combine(staging, "nested", "sub");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "staged.txt"), "Staged Nested");
        File.WriteAllText(Path.Combine(staging, "root_staged.txt"), "Staged Root");

        // Existing file in dest
        File.WriteAllText(Path.Combine(dest, "root_staged.txt"), "Original Root");

        // Promote with overwrite = false
        var result = ArchiveService.PromoteStagingDirectory(staging, dest, overwrite: false);

        Assert.True(result.success, result.message);
        // Original root file was preserved
        Assert.Equal("Original Root", File.ReadAllText(Path.Combine(dest, "root_staged.txt")));
        // New nested file was created
        Assert.True(File.Exists(Path.Combine(dest, "nested", "sub", "staged.txt")));
        Assert.Equal("Staged Nested", File.ReadAllText(Path.Combine(dest, "nested", "sub", "staged.txt")));
    }

    [Fact]
    public async Task SevenZip_ExtractAndCancellation_KillsProcessAndCleansStaging()
    {
        var exe = @"C:\Program Files\7-Zip\7z.exe";
        if (!File.Exists(exe)) return;

        using var fs = new TemporaryFileSystem();
        var archivePath = Path.Combine(fs.FolderB, "sevenzip_test.zip");
        var destination = fs.CreateDirectory("FolderB/sevenzip_dest");

        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("data.txt");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("SevenZip Data");
        }

        // Test normal 7-Zip extraction
        var result = await ArchiveService.Instance.ExtractWith7ZipAsync(exe, archivePath, destination, overwrite: true, CancellationToken.None);
        Assert.True(result.success, result.message);
        Assert.True(File.Exists(Path.Combine(destination, "data.txt")));
        Assert.Equal("SevenZip Data", File.ReadAllText(Path.Combine(destination, "data.txt")));

        // Verify staging directory is cleaned up
        var remainingStagingDirs = Directory.GetDirectories(destination, ".clanker-transfer-*.tmp");
        Assert.Empty(remainingStagingDirs);

        // Test cancelled extraction
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled token

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await ArchiveService.Instance.ExtractWith7ZipAsync(exe, archivePath, destination, overwrite: true, cts.Token);
        });

        // Staging directory must be cleaned up even after cancellation
        remainingStagingDirs = Directory.GetDirectories(destination, ".clanker-transfer-*.tmp");
        Assert.Empty(remainingStagingDirs);
    }
}
