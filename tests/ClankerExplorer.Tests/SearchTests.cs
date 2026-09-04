using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClankerExplorer.Models;
using ClankerExplorer.Services.Search;
using ClankerExplorer.Tests.TestInfrastructure;
using ClankerExplorer.ViewModels;
using Xunit;

namespace ClankerExplorer.Tests;

public sealed class SearchTests
{
    [Fact]
    public async Task NativeSearchProvider_ShallowScope_ReturnsOnlyDirectChildren()
    {
        using var fs = new TemporaryFileSystem();
        // fs has FolderA (with alpha.txt, beta.txt, file2.txt, file10.txt) and Nested (with nested.txt)
        fs.CreateFile("FolderA/sub/nested_alpha.txt");

        var provider = new NativeSearchProvider();
        var request = new SearchRequest(
            Query: "alpha",
            Scope: SearchScope.CurrentFolder,
            CurrentFolder: fs.FolderA);

        var matches = new List<SearchResultItem>();
        await foreach (var item in provider.SearchAsync(request))
        {
            matches.Add(item);
        }

        Assert.Single(matches);
        Assert.Equal("alpha.txt", matches[0].Name);
        Assert.False(matches[0].IsDirectory);
    }

    [Fact]
    public async Task NativeSearchProvider_RecursiveScope_FindsNestedItems()
    {
        using var fs = new TemporaryFileSystem();
        fs.CreateFile("FolderA/sub/nested_alpha.txt");
        fs.CreateFile("FolderA/sub/deep/deep_alpha.txt");

        var provider = new NativeSearchProvider();
        var request = new SearchRequest(
            Query: "alpha",
            Scope: SearchScope.CurrentFolderAndSubfolders,
            CurrentFolder: fs.FolderA);

        var matches = new List<SearchResultItem>();
        await foreach (var item in provider.SearchAsync(request))
        {
            matches.Add(item);
        }

        Assert.Equal(3, matches.Count);
        Assert.Contains(matches, m => m.Name == "alpha.txt");
        Assert.Contains(matches, m => m.Name == "nested_alpha.txt");
        Assert.Contains(matches, m => m.Name == "deep_alpha.txt");
    }

    [Fact]
    public async Task NativeSearchProvider_CaseInsensitiveMatching_FindsUpperAndLower()
    {
        using var fs = new TemporaryFileSystem();
        var provider = new NativeSearchProvider();
        var request = new SearchRequest(
            Query: "ALPHA",
            Scope: SearchScope.CurrentFolder,
            CurrentFolder: fs.FolderA);

        var matches = new List<SearchResultItem>();
        await foreach (var item in provider.SearchAsync(request))
        {
            matches.Add(item);
        }

        Assert.Single(matches);
        Assert.Equal("alpha.txt", matches[0].Name);
    }

    [Fact]
    public async Task NativeSearchProvider_EmptyQuery_ReturnsNothing()
    {
        using var fs = new TemporaryFileSystem();
        var provider = new NativeSearchProvider();
        var request = new SearchRequest(
            Query: "   ",
            Scope: SearchScope.CurrentFolderAndSubfolders,
            CurrentFolder: fs.FolderA);

        var matches = new List<SearchResultItem>();
        await foreach (var item in provider.SearchAsync(request))
        {
            matches.Add(item);
        }

        Assert.Empty(matches);
    }

    [Fact]
    public async Task NativeSearchProvider_Cancellation_HaltsGracefully()
    {
        using var fs = new TemporaryFileSystem();
        for (int i = 0; i < 20; i++)
        {
            fs.CreateFile($"FolderA/dummy_{i}.txt");
        }

        var provider = new NativeSearchProvider();
        using var cts = new CancellationTokenSource();
        var request = new SearchRequest("dummy", SearchScope.CurrentFolderAndSubfolders, fs.FolderA);

        var matches = new List<SearchResultItem>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in provider.SearchAsync(request, cancellationToken: cts.Token))
            {
                matches.Add(item);
                cts.Cancel();
            }
        });
    }

    [Fact]
    public async Task NativeSearchProvider_NonExistentFolder_ReportsGracefullyWithoutCrash()
    {
        var provider = new NativeSearchProvider();
        var nonExistent = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}");
        var request = new SearchRequest("test", SearchScope.CurrentFolderAndSubfolders, nonExistent);

        var matches = new List<SearchResultItem>();
        await foreach (var item in provider.SearchAsync(request))
        {
            matches.Add(item);
        }

        Assert.Empty(matches);
    }

    [Fact]
    public void SearchWorkspaceViewModel_Sorting_OrdersResultsByField()
    {
        using var vm = new SearchWorkspaceViewModel();
        vm.Results = new ObservableCollection<SearchResultItem>
        {
            new() { Name = "file10.txt", ParentPath = "C:\\B", SizeBytes = 500, ModifiedTime = new DateTime(2025, 1, 3), Extension = ".txt" },
            new() { Name = "file2.txt", ParentPath = "C:\\A", SizeBytes = 100, ModifiedTime = new DateTime(2025, 1, 1), Extension = ".txt" },
            new() { Name = "file1.txt", ParentPath = "C:\\C", SizeBytes = 1000, ModifiedTime = new DateTime(2025, 1, 2), Extension = ".doc" },
        };

        // Natural name sort ascending
        vm.Sort("Name", true);
        Assert.Equal(new[] { "file1.txt", "file2.txt", "file10.txt" }, vm.Results.Select(r => r.Name));

        // Natural name sort descending (toggle)
        vm.Sort("Name");
        Assert.Equal(new[] { "file10.txt", "file2.txt", "file1.txt" }, vm.Results.Select(r => r.Name));

        // Size sort ascending
        vm.Sort("Size", true);
        Assert.Equal(new[] { 100L, 500L, 1000L }, vm.Results.Select(r => r.SizeBytes));

        // Path sort ascending
        vm.Sort("Path", true);
        Assert.Equal(new[] { "C:\\A", "C:\\B", "C:\\C" }, vm.Results.Select(r => r.ParentPath));

        // Modified sort ascending
        vm.Sort("Modified", true);
        Assert.Equal(new[] { "file2.txt", "file1.txt", "file10.txt" }, vm.Results.Select(r => r.Name));
    }

    [Fact]
    public void SearchWorkspaceViewModel_Commands_TriggerNavigationAndFileOpenEvents()
    {
        using var vm = new SearchWorkspaceViewModel();
        string? navigatedFolder = null;
        string? selectedItemPath = null;
        string? openedFilePath = null;
        bool closeRequested = false;

        vm.RequestNavigate += (folder, select) =>
        {
            navigatedFolder = folder;
            selectedItemPath = select;
        };
        vm.RequestOpenFile += file => openedFilePath = file;
        vm.RequestClose += () => closeRequested = true;

        var fileResult = new SearchResultItem
        {
            Name = "sample.pdf",
            FullPath = @"C:\Docs\sample.pdf",
            ParentPath = @"C:\Docs",
            IsDirectory = false
        };

        var dirResult = new SearchResultItem
        {
            Name = "MyFolder",
            FullPath = @"C:\Docs\MyFolder",
            ParentPath = @"C:\Docs",
            IsDirectory = true
        };

        // Open item for file
        vm.OpenItem(fileResult);
        Assert.Equal(@"C:\Docs\sample.pdf", openedFilePath);

        // Open item for directory
        vm.OpenItem(dirResult);
        Assert.Equal(@"C:\Docs\MyFolder", navigatedFolder);
        Assert.Null(selectedItemPath);

        // Open containing folder for file
        vm.OpenContainingFolder(fileResult);
        Assert.Equal(@"C:\Docs", navigatedFolder);
        Assert.Equal(@"C:\Docs\sample.pdf", selectedItemPath);

        // Close workspace
        vm.CloseWorkspace();
        Assert.True(closeRequested);
    }

    [Fact]
    public void SearchWorkspaceViewModel_ClearQuery_ResetsResultsAndStatus()
    {
        using var vm = new SearchWorkspaceViewModel();
        vm.Results.Add(new SearchResultItem { Name = "test.txt", FullPath = @"C:\test.txt" });
        vm.Query = "test";
        vm.StatusText = "1 result";

        vm.ClearQuery();

        Assert.Equal(string.Empty, vm.Query);
        Assert.Empty(vm.Results);
        Assert.Equal("Enter a query to search", vm.StatusText);
    }

    [Fact]
    public async Task NativeSearchProvider_PathQuery_MatchesPathSegment()
    {
        using var fs = new TemporaryFileSystem();
        fs.CreateFile("FolderA/sub/nested_target.txt");

        var provider = new NativeSearchProvider();
        var separator = Path.DirectorySeparatorChar;
        var request = new SearchRequest($"sub{separator}nested", SearchScope.CurrentFolderAndSubfolders, fs.FolderA);

        var matches = new List<SearchResultItem>();
        await foreach (var item in provider.SearchAsync(request))
        {
            matches.Add(item);
        }

        Assert.Single(matches);
        Assert.Equal("nested_target.txt", matches[0].Name);
    }

    [Fact]
    public void SearchWorkspaceViewModel_ScopeChange_UpdatesScopeProperty()
    {
        using var vm = new SearchWorkspaceViewModel();
        Assert.Equal(SearchScope.CurrentFolderAndSubfolders, vm.Scope);

        vm.SelectedScopeOption = vm.ScopeOptions.First(o => o.Scope == SearchScope.CurrentFolder);
        Assert.Equal(SearchScope.CurrentFolder, vm.Scope);

        vm.SelectedScopeOption = vm.ScopeOptions.First(o => o.Scope == SearchScope.Everywhere);
        Assert.Equal(SearchScope.Everywhere, vm.Scope);
    }

    [Fact]
    public void SearchWorkspaceViewModel_ClipboardCommands_FireRequestSetClipboardText()
    {
        using var vm = new SearchWorkspaceViewModel();
        string? copiedText = null;
        vm.RequestSetClipboardText += text => copiedText = text;

        var item = new SearchResultItem
        {
            Name = "report.docx",
            FullPath = @"C:\Docs\report.docx",
            ParentPath = @"C:\Docs"
        };

        vm.CopyPath(item);
        Assert.Equal(@"C:\Docs\report.docx", copiedText);

        vm.CopyName(item);
        Assert.Equal("report.docx", copiedText);
    }

    [Fact]
    public async Task SearchService_DefaultProvider_IsNativeSearchProvider()
    {
        using var fs = new TemporaryFileSystem();
        var service = new SearchService();

        Assert.Equal("native", service.ActiveProvider.Id);
        Assert.True(service.ActiveProvider.IsAvailable);

        var request = new SearchRequest("alpha", SearchScope.CurrentFolder, fs.FolderA);
        var matches = new List<SearchResultItem>();
        await foreach (var item in service.SearchAsync(request))
        {
            matches.Add(item);
        }

        Assert.Single(matches);
        Assert.Equal("alpha.txt", matches[0].Name);
    }
}
