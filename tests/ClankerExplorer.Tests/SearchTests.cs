using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
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

    [Fact]
    public async Task SearchWorkspaceViewModel_StaleGenerationProtection_SearchBClobbersSearchA()
    {
        var fakeProvider = new ControllableFakeSearchProvider();
        var searchService = new SearchService(fakeProvider);

        using var vm = new SearchWorkspaceViewModel(searchService, getCurrentFolder: () => @"C:\Test");

        // First search takes a while and returns Item A
        fakeProvider.DelayMs = 150;
        fakeProvider.ItemsToReturn.Clear();
        fakeProvider.ItemsToReturn.Add(new SearchResultItem { Name = "itemA.txt", FullPath = @"C:\Test\itemA.txt" });

        vm.Query = "queryA";
        vm.SubmitSearch();

        // Immediately start search B with 0 delay and Item B
        fakeProvider.DelayMs = 0;
        fakeProvider.ItemsToReturn.Clear();
        fakeProvider.ItemsToReturn.Add(new SearchResultItem { Name = "itemB.txt", FullPath = @"C:\Test\itemB.txt" });

        vm.Query = "queryB";
        vm.SubmitSearch();

        bool completed = await WaitForConditionAsync(() => !vm.IsSearching && vm.Results.Count > 0);
        Assert.True(completed);

        await Task.Delay(250);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // Only Item B should exist in Results, never Item A
        Assert.Single(vm.Results);
        Assert.Equal("itemB.txt", vm.Results[0].Name);
    }

    [Fact]
    public async Task SearchWorkspaceViewModel_ClearQueryInvalidation_PreventsLateStatusUpdate()
    {
        var fakeProvider = new ControllableFakeSearchProvider();
        fakeProvider.DelayMs = 150;
        fakeProvider.ItemsToReturn.Add(new SearchResultItem { Name = "slowItem.txt", FullPath = @"C:\Test\slowItem.txt" });

        var searchService = new SearchService(fakeProvider);
        using var vm = new SearchWorkspaceViewModel(searchService, getCurrentFolder: () => @"C:\Test");

        vm.Query = "slow";
        vm.SubmitSearch();
        Assert.True(vm.IsSearching);

        // Clear query while search is in progress
        vm.ClearQuery();

        Assert.Equal(string.Empty, vm.Query);
        Assert.Empty(vm.Results);
        Assert.False(vm.IsSearching);
        Assert.Equal("Enter a query to search", vm.StatusText);

        // Wait for the delayed task to cancel/complete
        await Task.Delay(250);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // Late callbacks must NOT have overwritten StatusText with "Search stopped" or results
        Assert.Empty(vm.Results);
        Assert.False(vm.IsSearching);
        Assert.Equal("Enter a query to search", vm.StatusText);
    }

    [Fact]
    public async Task SearchWorkspaceViewModel_OnWorkspaceHidden_CancelsActiveSearch()
    {
        var fakeProvider = new ControllableFakeSearchProvider();
        fakeProvider.DelayMs = 200;
        fakeProvider.ItemsToReturn.Add(new SearchResultItem { Name = "item.txt", FullPath = @"C:\Test\item.txt" });

        var searchService = new SearchService(fakeProvider);
        using var vm = new SearchWorkspaceViewModel(searchService, getCurrentFolder: () => @"C:\Test");

        vm.Query = "item";
        vm.SubmitSearch();
        Assert.True(vm.IsSearching);

        // Hide workspace
        vm.OnWorkspaceHidden();

        Assert.False(vm.IsSearching);
        Assert.True(fakeProvider.LastCancellationToken.IsCancellationRequested);

        await Task.Delay(250);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsSearching);
    }

    [Fact]
    public async Task SearchWorkspaceViewModel_CurrentFolderContextChange_RerunsFolderSearch()
    {
        using var fs = new TemporaryFileSystem();
        string currentFolder = fs.FolderA;

        var fakeProvider = new ControllableFakeSearchProvider();
        fakeProvider.ItemsToReturn.Add(new SearchResultItem { Name = "res.txt", FullPath = Path.Combine(fs.FolderA, "res.txt") });

        var searchService = new SearchService(fakeProvider);
        using var vm = new SearchWorkspaceViewModel(searchService, getCurrentFolder: () => currentFolder);

        vm.Query = "res";
        vm.SubmitSearch();

        bool firstSearchCompleted = await WaitForConditionAsync(() => !vm.IsSearching);
        Assert.True(firstSearchCompleted);
        Assert.Equal(1, fakeProvider.SearchCallCount);
        Assert.Equal(fs.FolderA, fakeProvider.RecordedRequests[0].CurrentFolder);

        // Hide workspace
        vm.OnWorkspaceHidden();

        // User navigates Explorer to FolderB
        var folderB = Path.Combine(Path.GetTempPath(), $"clanker-test-b-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folderB);
        try
        {
            currentFolder = folderB;

            // Reopen Search in Folder B (CurrentFolderAndSubfolders scope)
            vm.OnWorkspaceOpened();

            bool secondSearchCompleted = await WaitForConditionAsync(() => !vm.IsSearching && fakeProvider.SearchCallCount == 2);
            Assert.True(secondSearchCompleted);
            Assert.Equal(2, fakeProvider.SearchCallCount);
            Assert.Equal(folderB, fakeProvider.RecordedRequests[1].CurrentFolder);

            // Switch to Everywhere scope and change folder to Folder C
            vm.SelectedScopeOption = vm.ScopeOptions.First(o => o.Scope == SearchScope.Everywhere);
            int callCountAfterScopeChange = fakeProvider.SearchCallCount;

            var folderC = Path.Combine(Path.GetTempPath(), $"clanker-test-c-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folderC);
            try
            {
                currentFolder = folderC;
                vm.OnWorkspaceOpened();

                // Changing folder in Everywhere scope must NOT rerun search
                await Task.Delay(100);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.Equal(callCountAfterScopeChange, fakeProvider.SearchCallCount);
                Assert.Equal(folderC, vm.CurrentFolderPath);
            }
            finally
            {
                if (Directory.Exists(folderC)) Directory.Delete(folderC);
            }
        }
        finally
        {
            if (Directory.Exists(folderB)) Directory.Delete(folderB);
        }
    }

    [Fact]
    public async Task SearchWorkspaceViewModel_ProgressSkippedFolderCount_AtomicallyUpdatedInStatus()
    {
        var fakeProvider = new ControllableFakeSearchProvider();
        fakeProvider.SkippedFoldersToReport = 3;
        fakeProvider.ItemsToReturn.Add(new SearchResultItem { Name = "file1.txt", FullPath = @"C:\file1.txt" });
        fakeProvider.ItemsToReturn.Add(new SearchResultItem { Name = "file2.txt", FullPath = @"C:\file2.txt" });

        var searchService = new SearchService(fakeProvider);
        using var vm = new SearchWorkspaceViewModel(searchService, getCurrentFolder: () => @"C:\Test");

        vm.Query = "file";
        vm.SubmitSearch();

        bool completed = await WaitForConditionAsync(() => !vm.IsSearching);
        Assert.True(completed);

        Assert.Equal(2, vm.TotalResultCount);
        Assert.Equal(3, vm.FoldersSkippedCount);
        Assert.Contains("completed with 3 inaccessible folders skipped", vm.StatusText);
        Assert.Contains("2 results", vm.StatusText);
    }

    [Fact]
    public void SearchPathHelper_And_PathCycleComparer_WindowsAndWslSemantics()
    {
        // 1. Ordinary Windows paths are case-insensitive
        Assert.False(SearchPathHelper.IsCaseSensitivePath(@"C:\Users\John\Documents"));
        Assert.False(SearchPathHelper.IsCaseSensitivePath(@"D:\Data\SubFolder"));
        Assert.Equal(StringComparison.OrdinalIgnoreCase, SearchPathHelper.GetPathStringComparison(@"C:\Users"));

        // 2. WSL UNC paths are case-sensitive
        Assert.True(SearchPathHelper.IsCaseSensitivePath(@"\\wsl$\Ubuntu\home\user"));
        Assert.True(SearchPathHelper.IsCaseSensitivePath(@"\\wsl.localhost\Ubuntu\home\user"));
        Assert.True(SearchPathHelper.IsCaseSensitivePath("//wsl$/Debian/var"));
        Assert.True(SearchPathHelper.IsCaseSensitivePath("//wsl.localhost/Debian/var"));
        Assert.Equal(StringComparison.Ordinal, SearchPathHelper.GetPathStringComparison(@"\\wsl$\Ubuntu\home"));

        // 3. PathCycleComparer respects case per path type
        var comparer = PathCycleComparer.Instance;

        // Windows paths with different case should match (avoid duplicates/cycles)
        Assert.True(comparer.Equals(@"C:\Temp\DirA", @"c:\temp\dira"));
        Assert.Equal(comparer.GetHashCode(@"C:\Temp\DirA"), comparer.GetHashCode(@"c:\temp\dira"));

        // WSL paths with different case are distinct Linux directories and must NOT be collapsed
        Assert.False(comparer.Equals(@"\\wsl$\Ubuntu\home\User", @"\\wsl$\Ubuntu\home\user"));
        Assert.NotEqual(comparer.GetHashCode(@"\\wsl$\Ubuntu\Foo"), comparer.GetHashCode(@"\\wsl$\Ubuntu\foo"));
    }

    [Fact]
    public void SearchWorkspaceViewModel_OnWorkspaceOpened_FiresRequestFocusSearchBox()
    {
        using var vm = new SearchWorkspaceViewModel();
        bool focusRequested = false;
        vm.RequestFocusSearchBox += () => focusRequested = true;

        vm.OnWorkspaceOpened();

        Assert.True(focusRequested);
    }

    private static async Task<bool> WaitForConditionAsync(Func<bool> condition, int timeoutMs = 4000, int pollIntervalMs = 20)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            await Task.Delay(pollIntervalMs);
        }
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return condition();
    }

    private sealed class ControllableFakeSearchProvider : ISearchProvider
    {
        public string Id => "fake";
        public string DisplayName => "Fake Provider";
        public bool IsAvailable => true;

        public int DelayMs { get; set; } = 0;
        public List<SearchResultItem> ItemsToReturn { get; } = new();
        public int SkippedFoldersToReport { get; set; } = 0;
        public int SearchCallCount { get; private set; }
        public List<SearchRequest> RecordedRequests { get; } = new();
        public CancellationToken LastCancellationToken { get; private set; }

        public async IAsyncEnumerable<SearchResultItem> SearchAsync(
            SearchRequest request,
            IProgress<SearchProgressReport>? progress = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            SearchCallCount++;
            RecordedRequests.Add(request);
            LastCancellationToken = cancellationToken;

            if (SkippedFoldersToReport > 0)
            {
                progress?.Report(new SearchProgressReport(SkippedFoldersToReport, 0, null));
            }

            foreach (var item in ItemsToReturn)
            {
                if (DelayMs > 0)
                {
                    await Task.Delay(DelayMs, cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
        }
    }
}
