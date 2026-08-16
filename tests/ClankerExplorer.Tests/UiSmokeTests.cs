using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClankerExplorer.Tests.TestInfrastructure;
using ClankerExplorer.Models;
using ClankerExplorer.Services;
using ClankerExplorer.ViewModels;
using ClankerExplorer.Views;

namespace ClankerExplorer.Tests;

public sealed class UiSmokeTests
{
    [AvaloniaFact]
    public void ApplicationAndMainWindow_InitializeWithoutException()
    {
        using var fs = new TemporaryFileSystem();
        TestEnvironment.ResetGlobalSettings(fs.FolderA);
        using var main = new MainViewModel(loadSidebarData: false);

        var exception = Record.Exception(() =>
        {
            var window = new MainWindow { DataContext = main };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.Close();
        });

        Assert.Null(exception);
        Assert.NotEmpty(main.LeftPane.Tabs);
        Assert.NotNull(main.LeftPane.SelectedTab);
    }

    [AvaloniaFact]
    public void ExplorerPane_RendersTabHeaderFromTabsCollection()
    {
        using var fs = new TemporaryFileSystem();
        using var pane = new ExplorerPaneViewModel("left", fs.FolderA, "PANE 1");
        var view = new ExplorerPaneView { DataContext = pane };
        var window = new Window { Content = view, Width = 900, Height = 600 };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var renderedTitle = view.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(text => text.Text == pane.SelectedTab!.Title);

        Assert.NotNull(renderedTitle);
        Assert.True(renderedTitle.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void MainWindow_TopmostBindingTracksViewModelToggle()
    {
        using var fs = new TemporaryFileSystem();
        TestEnvironment.ResetGlobalSettings(fs.FolderA);
        using var main = new MainViewModel(loadSidebarData: false);
        var window = new MainWindow { DataContext = main };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.Topmost);

            main.ToggleAlwaysOnTop();
            Dispatcher.UIThread.RunJobs();

            Assert.True(window.Topmost);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void MainWindow_PreviewColumnCollapsesAndRestoresImmediately()
    {
        using var fs = new TemporaryFileSystem();
        TestEnvironment.ResetGlobalSettings(fs.FolderA);
        using var main = new MainViewModel(loadSidebarData: false)
        {
            ShowInspector = true,
            InspectorWidth = 410
        };
        var window = new MainWindow { DataContext = main, Width = 1360, Height = 800 };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var grid = Assert.IsType<Grid>(window.FindControl<Grid>("MainContentGrid"));
            var previewColumn = grid.ColumnDefinitions[3];
            Assert.Equal(240, previewColumn.MinWidth);
            Assert.Equal(410, previewColumn.Width.Value, precision: 1);

            main.ToggleInspector();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, previewColumn.MinWidth);
            Assert.Equal(0, previewColumn.MaxWidth);
            Assert.Equal(0, previewColumn.Width.Value);

            main.ToggleInspector();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(240, previewColumn.MinWidth);
            Assert.Equal(410, previewColumn.Width.Value, precision: 1);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ExplorerPane_TabOverflowControlsAppearAndDisappearWithTabCount()
    {
        using var fs = new TemporaryFileSystem();
        TestEnvironment.ResetGlobalSettings(fs.FolderB);
        using var pane = new ExplorerPaneViewModel("left", fs.FolderB) { TabWidth = 160 };
        for (int index = 0; index < 6; index++)
        {
            pane.AddNewTab(fs.FolderB);
        }

        var view = new ExplorerPaneView { DataContext = pane };
        var window = new Window { Content = view, Width = 520, Height = 500 };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var buttons = Assert.IsType<StackPanel>(view.FindControl<StackPanel>("TabScrollButtonsPanel"));
            Assert.True(buttons.IsVisible);

            foreach (var tab in pane.Tabs.Skip(1).ToArray())
            {
                pane.CloseTab(tab);
            }
            Dispatcher.UIThread.RunJobs();

            Assert.False(buttons.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ExplorerPane_ThumbnailModeKeepsFileItemsVisible()
    {
        using var fs = new TemporaryFileSystem();
        TestEnvironment.ResetGlobalSettings(fs.FolderB);
        fs.CreateFile("FolderB/visible-in-thumbnails.txt", "hello world thumbnail content");

        using var pane = new ExplorerPaneViewModel("left", fs.FolderB);
        await pane.SelectedTab!.RefreshAsync();
        pane.ViewMode = "Thumbnails";

        var view = new ExplorerPaneView { DataContext = pane };
        var window = new Window { Content = view, Width = 900, Height = 600 };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var detailsGrid = Assert.IsType<DataGrid>(view.FindControl<DataGrid>("FileDataGrid"));
            Assert.False(detailsGrid.IsVisible);
            var thumbnails = Assert.IsType<ListBox>(view.FindControl<ListBox>("ThumbnailListBox"));
            Assert.True(thumbnails.IsVisible);
            Assert.Contains(
                view.GetVisualDescendants().OfType<TextBlock>(),
                text => text.IsVisible && text.Text == "visible-in-thumbnails.txt");

            var item = Assert.Single(pane.SelectedTab.FilteredItems);
            pane.SelectedTab.SelectThumbnailItem(item, control: false, shift: false);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(item, pane.SelectedTab.SelectedItem);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ThumbnailGrid_VirtualizesFiftyThousandItems()
    {
        using var fs = new TemporaryFileSystem();
        TestEnvironment.ResetGlobalSettings(fs.FolderB);
        using var pane = new ExplorerPaneViewModel("left", fs.FolderB);
        await pane.SelectedTab!.RefreshAsync();
        pane.SelectedTab.Items = new System.Collections.ObjectModel.ObservableCollection<FileItem>(
            Enumerable.Range(0, 50_000).Select(index => new FileItem
            {
                Name = $"image{index}.png",
                Extension = ".png",
                FullPath = Path.Combine(fs.FolderB, $"image{index}.png"),
                SizeBytes = 100
            }));
        pane.SelectedTab.ApplyFilter();
        pane.UpdateThumbnailViewportWidth(900);

        var view = new ExplorerPaneView { DataContext = pane };
        var window = new Window { Content = view, Width = 900, Height = 600 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            int realizedDetailRows = view.GetVisualDescendants().OfType<DataGridRow>().Count();
            Assert.InRange(realizedDetailRows, 1, 200);

            pane.ViewMode = "Thumbnails";
            Dispatcher.UIThread.RunJobs();

            int realizedCards = view.GetVisualDescendants()
                .OfType<Border>()
                .Count(border => border.Classes.Contains("thumbnail-card"));
            Assert.InRange(realizedCards, 1, 200);
            Assert.True(pane.ThumbnailRows.Count < pane.SelectedTab.FilteredItems.Count);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ExplorerPane_RestoresFolderColumnOrderAndScrollPosition()
    {
        using var fs = new TemporaryFileSystem();
        using var store = new FolderViewStateService(fs.CreateDirectory("ui-folder-state"));
        store.Set(fs.FolderB, new FolderViewState
        {
            ViewMode = "Details",
            SmartColumnSizing = false,
            ColumnOrder = new List<string> { "Size", "Name", "Ext" },
            DetailsVerticalOffset = 240,
            DetailsTopItemPath = Path.Combine(fs.FolderB, "file250.txt")
        });
        using var pane = new ExplorerPaneViewModel("left", fs.FolderB, folderViewStateService: store);
        await pane.SelectedTab!.RefreshAsync();
        pane.SelectedTab.Items = new System.Collections.ObjectModel.ObservableCollection<FileItem>(
            Enumerable.Range(0, 300).Select(index => new FileItem
            {
                Name = $"file{index}.txt",
                Extension = ".txt",
                FullPath = Path.Combine(fs.FolderB, $"file{index}.txt")
            }));
        pane.SelectedTab.ApplyFilter();

        var view = new ExplorerPaneView { DataContext = pane };
        var window = new Window { Content = view, Width = 900, Height = 500 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var grid = Assert.IsType<DataGrid>(view.FindControl<DataGrid>("FileDataGrid"));
            Assert.Equal("Size", grid.Columns.OrderBy(column => column.DisplayIndex).First().Header?.ToString());
            var createdColumn = Assert.Single(grid.Columns, column =>
                column.Header?.ToString()?.StartsWith("Date Created", StringComparison.Ordinal) == true);
            createdColumn.Sort();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Created", pane.SelectedTab.SortColumn);
            Assert.Equal("Date Created ↑", createdColumn.Header?.ToString());
            Assert.Contains(
                grid.GetVisualDescendants().OfType<DataGridRow>(),
                row => (row.DataContext as FileItem)?.Name == "file250.txt");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void MainWindow_RendersBuildNumberAndTimestampInBottomRight()
    {
        using var fs = new TemporaryFileSystem();
        TestEnvironment.ResetGlobalSettings(fs.FolderA);
        using var main = new MainViewModel(loadSidebarData: false);

        Assert.False(string.IsNullOrWhiteSpace(main.BuildDisplayString));
        Assert.StartsWith("Build #", main.BuildDisplayString);
        Assert.Contains(BuildInfoService.ShortDateTime, main.BuildDisplayString);

        var window = new MainWindow { DataContext = main };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var buildTextBlock = window.GetVisualDescendants()
                .OfType<TextBlock>()
                .FirstOrDefault(tb => tb.Text == main.BuildDisplayString);

            Assert.NotNull(buildTextBlock);
            Assert.True(buildTextBlock.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task DetailsView_TruncatesLongFilenames_AndShowsTooltipOnlyWhenTrimmed()
    {
        using var fs = new TemporaryFileSystem();
        using var pane = new ExplorerPaneViewModel("left", fs.FolderA, "PANE 1");
        await pane.SelectedTab!.RefreshAsync();
        
        var longFileName = "Very_Long_File_Name_That_Exceeds_Column_Width_To_Verify_Ellipsis_And_Tooltip.txt";
        var shortFileName = "short.txt";

        pane.SelectedTab.Items = new System.Collections.ObjectModel.ObservableCollection<FileItem>(new[]
        {
            new FileItem { Name = longFileName, FullPath = Path.Combine(fs.FolderA, longFileName), IsDirectory = false },
            new FileItem { Name = shortFileName, FullPath = Path.Combine(fs.FolderA, shortFileName), IsDirectory = false }
        });
        pane.SelectedTab.ApplyFilter();

        var view = new ExplorerPaneView { DataContext = pane };
        var window = new Window { Content = view, Width = 800, Height = 500 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var textBlocks = view.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(tb => tb.Text == longFileName || tb.Text == shortFileName)
                .ToList();

            Assert.NotEmpty(textBlocks);
            foreach (var tb in textBlocks)
            {
                Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, tb.TextTrimming);
                Assert.True(ClankerExplorer.Behaviors.AutoToolTip.GetShowWhenTrimmed(tb));
            }

            // Test AutoToolTip.IsTextTrimmed behavior with simulated narrow vs wide bounds
            var narrowTb = new TextBlock
            {
                Text = longFileName,
                FontSize = 12.5,
                Width = 60,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
            };
            narrowTb.Measure(new Size(60, 20));
            narrowTb.Arrange(new Rect(0, 0, 60, 20));
            Assert.True(ClankerExplorer.Behaviors.AutoToolTip.IsTextTrimmed(narrowTb));

            var wideTb = new TextBlock
            {
                Text = shortFileName,
                FontSize = 12.5,
                Width = 500,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
            };
            wideTb.Measure(new Size(500, 20));
            wideTb.Arrange(new Rect(0, 0, 500, 20));
            Assert.False(ClankerExplorer.Behaviors.AutoToolTip.IsTextTrimmed(wideTb));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ThumbnailSelection_SmoothReSelection_AndThemeBrushConfigured()
    {
        using var fs = new TemporaryFileSystem();
        var itemA = new FileItem { Name = "a.jpg", FullPath = Path.Combine(fs.FolderA, "a.jpg") };
        var itemB = new FileItem { Name = "b.jpg", FullPath = Path.Combine(fs.FolderA, "b.jpg") };

        using var tab = new ExplorerTabViewModel(fs.FolderA);
        tab.Items = new System.Collections.ObjectModel.ObservableCollection<FileItem>(new[] { itemA, itemB });
        tab.ApplyFilter();

        // 1. Initial selection
        tab.SelectThumbnailItem(itemA, control: false, shift: false);
        Assert.True(itemA.IsThumbnailSelected);
        Assert.Single(tab.SelectedItems);
        Assert.Equal(itemA, tab.SelectedItem);

        // Track if IsThumbnailSelected property changed on re-clicking already selected item
        int changedCount = 0;
        itemA.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(FileItem.IsThumbnailSelected)) changedCount++;
        };

        // 2. Click already selected item (simulating 2nd click of double-click)
        tab.SelectThumbnailItem(itemA, control: false, shift: false);
        Assert.True(itemA.IsThumbnailSelected);
        Assert.Single(tab.SelectedItems);
        Assert.Equal(itemA, tab.SelectedItem);
        // Should not have toggled false then true
        Assert.Equal(0, changedCount);

        // 3. Select different item
        tab.SelectThumbnailItem(itemB, control: false, shift: false);
        Assert.False(itemA.IsThumbnailSelected);
        Assert.True(itemB.IsThumbnailSelected);
        Assert.Single(tab.SelectedItems);
        Assert.Equal(itemB, tab.SelectedItem);

        // 4. Verify ThemeManager configures AppThumbnailSelectedBgBrush
        var settings = new AppSettings { SelectedBackgroundColor = "#283548" };
        ThemeManager.ApplyTheme(settings);
        Assert.True(Avalonia.Application.Current?.Resources.ContainsKey("AppThumbnailSelectedBgBrush"));
    }

    [AvaloniaFact]
    public async Task PasteFiles_SelectsSingleAndMultiplePastedItems_AndPreservesNonPastedItems()
    {
        using var fs = new TemporaryFileSystem();
        TestEnvironment.ResetGlobalSettings(fs.FolderA);

        var existingFile = Path.Combine(fs.FolderB, "existing.txt");
        File.WriteAllText(existingFile, "already here");

        var source1 = Path.Combine(fs.FolderA, "alpha.txt");
        var source2 = Path.Combine(fs.FolderA, "beta.txt");

        using var pane = new ExplorerPaneViewModel("left", fs.FolderB, "PANE 1");
        await pane.SelectedTab!.RefreshAsync();

        // 1. Copy 2 files from FolderA
        ClipboardFileService.Copy(new[] { source1, source2 });

        // 2. Paste into FolderB
        await pane.PasteFilesAsync();

        // 3. Verify newly pasted items are selected in multi-selection
        Assert.Equal(2, pane.SelectedTab.SelectedItems.Count);
        Assert.Contains(pane.SelectedTab.SelectedItems, item => item.Name == "alpha.txt");
        Assert.Contains(pane.SelectedTab.SelectedItems, item => item.Name == "beta.txt");
        Assert.DoesNotContain(pane.SelectedTab.SelectedItems, item => item.Name == "existing.txt");
        Assert.NotNull(pane.SelectedTab.SelectedItem);

        // 4. Also verify thumbnail view selection flag
        var alphaItem = pane.SelectedTab.FilteredItems.First(i => i.Name == "alpha.txt");
        var betaItem = pane.SelectedTab.FilteredItems.First(i => i.Name == "beta.txt");
        var existingItem = pane.SelectedTab.FilteredItems.First(i => i.Name == "existing.txt");

        Assert.True(alphaItem.IsThumbnailSelected);
        Assert.True(betaItem.IsThumbnailSelected);
        Assert.False(existingItem.IsThumbnailSelected);
    }

    [AvaloniaFact]
    public async Task ExplorerPane_MiddleMouseAutoScroll_InitializesAndRendersAnchor()
    {
        using var fs = new TemporaryFileSystem();
        using var pane = new ExplorerPaneViewModel("left", fs.FolderA, "PANE 1");
        await pane.SelectedTab!.RefreshAsync();

        var view = new ExplorerPaneView { DataContext = pane };
        var window = new Window { Content = view, Width = 800, Height = 600 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var autoScrollCanvas = view.FindControl<Canvas>("AutoScrollCanvas");
            var autoScrollAnchor = view.FindControl<Border>("AutoScrollAnchor");

            Assert.NotNull(autoScrollCanvas);
            Assert.NotNull(autoScrollAnchor);
            Assert.False(autoScrollCanvas.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task RightClickSelection_UnselectedAndMultiSelection_PreservesCorrectSelection()
    {
        using var fs = new TemporaryFileSystem();
        TestEnvironment.ResetGlobalSettings(fs.FolderA);

        var file1 = Path.Combine(fs.FolderA, "alpha.txt");
        var file2 = Path.Combine(fs.FolderA, "beta.txt");
        var file3 = Path.Combine(fs.FolderA, "gamma.txt");
        File.WriteAllText(file3, "gamma");

        using var pane = new ExplorerPaneViewModel("left", fs.FolderA, "PANE 1");
        await pane.SelectedTab!.RefreshAsync();

        var tab = pane.SelectedTab;
        var item1 = tab.FilteredItems.First(i => i.Name == "alpha.txt");
        var item2 = tab.FilteredItems.First(i => i.Name == "beta.txt");
        var item3 = tab.FilteredItems.First(i => i.Name == "gamma.txt");

        // 1. Initial multi-selection in Thumbnail mode
        tab.SelectThumbnailItem(item1, control: false, shift: false);
        tab.SelectThumbnailItem(item2, control: true, shift: false);
        Assert.Equal(2, tab.SelectedItems.Count);
        Assert.True(item1.IsThumbnailSelected);
        Assert.True(item2.IsThumbnailSelected);

        // 2. Right-click on item2 (which is part of the multi-selection)
        // Thumbnail mode simulation
        if (item2.IsThumbnailSelected || tab.SelectedItems.Contains(item2))
        {
            tab.SelectedItem = item2;
        }
        else
        {
            tab.SelectThumbnailItem(item2, control: false, shift: false);
        }

        // Multi-selection is preserved!
        Assert.Equal(2, tab.SelectedItems.Count);
        Assert.Contains(item1, tab.SelectedItems);
        Assert.Contains(item2, tab.SelectedItems);
        Assert.Equal(item2, tab.SelectedItem);

        // Verify context menu actions operate on multi-selection
        var selectedFiles = pane.GetSelectedFileItems();
        Assert.Equal(2, selectedFiles.Count);
        Assert.Contains(item1, selectedFiles);
        Assert.Contains(item2, selectedFiles);

        pane.CopyFiles();
        Assert.Equal(2, ClipboardFileService.StoredPaths.Count);
        Assert.Contains(file1, ClipboardFileService.StoredPaths);
        Assert.Contains(file2, ClipboardFileService.StoredPaths);

        // 3. Right-click on item3 (unselected item)
        if (item3.IsThumbnailSelected || tab.SelectedItems.Contains(item3))
        {
            tab.SelectedItem = item3;
        }
        else
        {
            tab.SelectThumbnailItem(item3, control: false, shift: false);
        }

        // Selected only item3
        Assert.Single(tab.SelectedItems);
        Assert.Equal(item3, tab.SelectedItem);
        Assert.True(item3.IsThumbnailSelected);
        Assert.False(item1.IsThumbnailSelected);
        Assert.False(item2.IsThumbnailSelected);
    }

    [AvaloniaFact]
    public async Task ThumbnailSorting_FieldAndDirectionControls_PreservesSortState()
    {
        using var fs = new TemporaryFileSystem();
        TestEnvironment.ResetGlobalSettings(fs.FolderA);

        using var pane = new ExplorerPaneViewModel("left", fs.FolderA, "PANE 1");
        await pane.SelectedTab!.RefreshAsync();

        // 1. Initial default sort is Name Ascending
        Assert.Equal("Name", pane.SelectedTab.SortColumn);
        Assert.True(pane.SelectedTab.SortAscending);
        Assert.Equal("Sort: Name ↑", pane.ThumbnailSortDisplay);
        Assert.True(pane.IsSortByName);
        Assert.True(pane.IsSortAscending);

        // 2. Change sort to Date Modified
        pane.SetThumbnailSort("Modified");
        Assert.Equal("Modified", pane.SelectedTab.SortColumn);
        Assert.True(pane.SelectedTab.SortAscending);
        Assert.Equal("Sort: Date Modified ↑", pane.ThumbnailSortDisplay);
        Assert.True(pane.IsSortByModified);

        // 3. Change direction to Descending
        pane.SetThumbnailSort("desc");
        Assert.False(pane.SelectedTab.SortAscending);
        Assert.Equal("Sort: Date Modified ↓", pane.ThumbnailSortDisplay);
        Assert.True(pane.IsSortDescending);

        // 4. Change sort to Size
        pane.SetThumbnailSort("Size");
        Assert.Equal("Size", pane.SelectedTab.SortColumn);
        Assert.True(pane.SelectedTab.SortAscending);
        Assert.Equal("Sort: Size ↑", pane.ThumbnailSortDisplay);
        Assert.True(pane.IsSortBySize);

        // 5. Change thumbnail size (must not reset sort)
        pane.ThumbnailSize = 220;
        Assert.Equal("Size", pane.SelectedTab.SortColumn);
        Assert.True(pane.SelectedTab.SortAscending);

        // 6. Navigate away and back to test folder view state restoration
        pane.SelectedTab.NavigateTo(fs.FolderB);
        await pane.SelectedTab.RefreshAsync();

        pane.SelectedTab.NavigateTo(fs.FolderA);
        await pane.SelectedTab.RefreshAsync();

        Assert.Equal("Size", pane.SelectedTab.SortColumn);
        Assert.True(pane.SelectedTab.SortAscending);
        Assert.Equal(220, pane.ThumbnailSize);
    }

    [AvaloniaFact]
    public void FileIcon_ExtractsAndCachesAssociatedIconsForFiles()
    {
        var textFile = new FileItem
        {
            Name = "notes.txt",
            Extension = ".txt",
            FullPath = @"C:\Fake\notes.txt",
            IsDirectory = false
        };

        var folder = new FileItem
        {
            Name = "MyFolder",
            Extension = "",
            FullPath = @"C:\Fake\MyFolder",
            IsDirectory = true
        };

        if (OperatingSystem.IsWindows())
        {
            Assert.NotNull(folder.FileIcon);
            Assert.True(folder.HasFileIcon);
        }

        // Files obtain an icon
        var icon1 = textFile.FileIcon;
        var icon2 = textFile.FileIcon;

        // Caching returns identical image reference
        Assert.Same(icon1, icon2);

        // Same extension on another FileItem reuses cached icon from FileIconService
        var textFile2 = new FileItem
        {
            Name = "todo.txt",
            Extension = ".txt",
            FullPath = @"C:\Another\todo.txt",
            IsDirectory = false
        };

        Assert.Same(icon1, textFile2.FileIcon);

        // Unknown extension gets generic file icon
        var unknownFile = new FileItem
        {
            Name = "data.xyz123random",
            Extension = ".xyz123random",
            FullPath = @"C:\Fake\data.xyz123random",
            IsDirectory = false
        };

        var unknownIcon = unknownFile.FileIcon;
        if (OperatingSystem.IsWindows())
        {
            Assert.NotNull(unknownIcon);
        }
    }

    [Fact]
    public async Task Refresh_PreservesSelectedAndFocusedItems_AcrossDirectoryUpdates()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ClankerTest_RefreshSelection_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var file1 = Path.Combine(tempDir, "fileA.txt");
            var file2 = Path.Combine(tempDir, "fileB.txt");
            var file3 = Path.Combine(tempDir, "fileC.txt");

            File.WriteAllText(file1, "A");
            File.WriteAllText(file2, "B");
            File.WriteAllText(file3, "C");

            var tab = new ExplorerTabViewModel(tempDir);
            await tab.RefreshAsync();

            Assert.Equal(3, tab.FilteredItems.Count);

            // Select fileA and fileC, with focus on fileC
            var itemA = tab.FilteredItems.First(i => i.Name == "fileA.txt");
            var itemC = tab.FilteredItems.First(i => i.Name == "fileC.txt");

            tab.SelectedItems.Add(itemA);
            tab.SelectedItems.Add(itemC);
            itemA.IsThumbnailSelected = true;
            itemC.IsThumbnailSelected = true;
            tab.SelectedItem = itemC;

            // Modify metadata of fileB (e.g. file watcher / background update)
            File.WriteAllText(file2, "B modified content");

            // Perform refresh
            await tab.RefreshAsync();

            Assert.Equal(3, tab.FilteredItems.Count);

            // Verify fileA and fileC remain selected
            var newSelected = tab.SelectedItems.Select(s => s.FullPath).ToList();
            Assert.Equal(2, newSelected.Count);
            Assert.Contains(file1, newSelected);
            Assert.Contains(file3, newSelected);

            // Verify focus remains on fileC
            Assert.NotNull(tab.SelectedItem);
            Assert.Equal(file3, tab.SelectedItem.FullPath);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task Refresh_DoesNotPreserveDeletedItems()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ClankerTest_RefreshDelete_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var file1 = Path.Combine(tempDir, "keep.txt");
            var file2 = Path.Combine(tempDir, "deleted.txt");

            File.WriteAllText(file1, "Keep");
            File.WriteAllText(file2, "Delete me");

            var tab = new ExplorerTabViewModel(tempDir);
            await tab.RefreshAsync();

            var keepItem = tab.FilteredItems.First(i => i.Name == "keep.txt");
            var deleteItem = tab.FilteredItems.First(i => i.Name == "deleted.txt");

            tab.SelectedItems.Add(keepItem);
            tab.SelectedItems.Add(deleteItem);
            keepItem.IsThumbnailSelected = true;
            deleteItem.IsThumbnailSelected = true;
            tab.SelectedItem = deleteItem;

            // Delete file2 on disk
            File.Delete(file2);

            // Refresh
            await tab.RefreshAsync();

            Assert.Single(tab.FilteredItems);
            Assert.Single(tab.SelectedItems);
            Assert.Equal(file1, tab.SelectedItems[0].FullPath);
            Assert.Equal(file1, tab.SelectedItem?.FullPath);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void EmptySpace_ClearingSelection_UpdatesContextMenuState()
    {
        var pane = new ExplorerPaneViewModel("pane1", @"C:\FakePath");
        var tab = pane.SelectedTab!;

        var file1 = new FileItem { Name = "test.txt", FullPath = @"C:\FakePath\test.txt", Extension = ".txt", IsDirectory = false };
        tab.FilteredItems.Add(file1);

        // Select file1
        tab.SelectedItems.Add(file1);
        tab.SelectedItem = file1;
        file1.IsThumbnailSelected = true;

        pane.NotifyContextMenuProperties();
        Assert.True(pane.IsItemSelected);
        Assert.True(pane.IsNormalFileSelected);

        // Simulate empty space click: clear selection
        tab.ClearThumbnailSelection();
        tab.SelectedItems.Clear();
        tab.SelectedItem = null;

        pane.NotifyContextMenuProperties();

        // Verify context menu state transitions to folder/background mode
        Assert.False(pane.IsItemSelected);
        Assert.False(pane.IsNormalFileSelected);
        Assert.False(pane.IsFolderSelected);
        Assert.False(file1.IsThumbnailSelected);
    }

    [Fact]
    public void OpenSelected_Folder_NavigatesIntoDirectory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "clanker_enter_test_" + Guid.NewGuid().ToString("N"));
        var subDir = Path.Combine(tempRoot, "SubFolder");
        Directory.CreateDirectory(subDir);

        try
        {
            var pane = new ExplorerPaneViewModel("pane1", tempRoot);
            var tab = pane.SelectedTab!;

            var folderItem = new FileItem { Name = "SubFolder", FullPath = subDir, IsDirectory = true };
            tab.FilteredItems.Add(folderItem);
            tab.SelectedItems.Add(folderItem);
            tab.SelectedItem = folderItem;

            pane.OpenSelected();

            Assert.Equal(subDir, tab.CurrentPath);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    [Fact]
    public async Task WatcherReconcile_CreatingOrRenaming_PreservesSingleInstanceAndSelection()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "clanker_watcher_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var fileA = Path.Combine(tempDir, "FileA.txt");
        File.WriteAllText(fileA, "Content A");

        try
        {
            using var tab = new ExplorerTabViewModel(tempDir);
            await tab.RefreshAsync();

            Assert.Single(tab.FilteredItems);
            Assert.Equal("FileA.txt", tab.FilteredItems[0].Name);

            // Select FileA
            tab.SelectThumbnailItem(tab.FilteredItems[0], control: false, shift: false);
            Assert.True(tab.FilteredItems[0].IsThumbnailSelected);

            // 1. Rapid successive create / watcher event for existing file
            tab.ReconcileItemCreatedOrChanged(fileA);
            tab.ReconcileItemCreatedOrChanged(fileA);
            Assert.Single(tab.FilteredItems);
            Assert.True(tab.FilteredItems[0].IsThumbnailSelected);

            // 2. Create a new file and reconcile
            var fileB = Path.Combine(tempDir, "FileB.txt");
            File.WriteAllText(fileB, "Content B");
            tab.ReconcileItemCreatedOrChanged(fileB);
            tab.ReconcileItemCreatedOrChanged(fileB); // Rapid duplicate event

            Assert.Equal(2, tab.FilteredItems.Count);

            // 3. Rename FileA to FileA_Renamed.txt
            var fileARenamed = Path.Combine(tempDir, "FileA_Renamed.txt");
            File.Move(fileA, fileARenamed);
            tab.ReconcileItemRenamed(fileA, fileARenamed);

            Assert.Equal(2, tab.FilteredItems.Count);
            var renamedItem = tab.FilteredItems.FirstOrDefault(i => i.Name == "FileA_Renamed.txt");
            Assert.NotNull(renamedItem);
            Assert.True(renamedItem.IsThumbnailSelected);
            Assert.Equal(renamedItem, tab.SelectedItem);
            Assert.DoesNotContain(tab.FilteredItems, i => i.Name == "FileA.txt");

            // 4. Delete FileB
            File.Delete(fileB);
            tab.ReconcileItemDeleted(fileB);
            Assert.Single(tab.FilteredItems);
            Assert.Equal("FileA_Renamed.txt", tab.FilteredItems[0].Name);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task StartInlineRename_SetsIsRenamingAndEditingNameToItemName()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "clanker_inline_rename_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var file1 = Path.Combine(tempDir, "plenum.step");
        var file2 = Path.Combine(tempDir, "document.pdf");
        File.WriteAllText(file1, "1");
        File.WriteAllText(file2, "2");

        try
        {
            var pane = new ExplorerPaneViewModel("pane1", tempDir);
            var tab = pane.SelectedTab!;
            await tab.RefreshAsync();

            var item1 = tab.FilteredItems.First(i => i.Name == "plenum.step");
            var item2 = tab.FilteredItems.First(i => i.Name == "document.pdf");

            tab.SelectedItem = item1;
            pane.TriggerRename();

            Assert.True(item1.IsRenaming);
            Assert.Equal("plenum.step", item1.EditingName);
            Assert.False(item2.IsRenaming);

            // Starting rename on item2 cancels rename on item1
            pane.StartInlineRename(item2);
            Assert.False(item1.IsRenaming);
            Assert.True(item2.IsRenaming);
            Assert.Equal("document.pdf", item2.EditingName);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void FileDragDropService_DeterminesCorrectEffect_SameVsDifferentVolume()
    {
        var sameVolSources = new[] { @"C:\Folder1\file.txt", @"C:\Folder1\image.png" };
        var sameVolDest = @"C:\Folder2";

        // Same volume default = Move
        var moveEffect = FileDragDropService.ResolveEffect(sameVolSources, sameVolDest, Avalonia.Input.KeyModifiers.None);
        Assert.Equal(Avalonia.Input.DragDropEffects.Move, moveEffect);

        // Same volume with Ctrl = Copy
        var ctrlEffect = FileDragDropService.ResolveEffect(sameVolSources, sameVolDest, Avalonia.Input.KeyModifiers.Control);
        Assert.Equal(Avalonia.Input.DragDropEffects.Copy, ctrlEffect);

        // Different volume default = Copy
        var diffVolSources = new[] { @"C:\Folder1\file.txt" };
        var diffVolDest = @"D:\Folder2";
        var diffVolEffect = FileDragDropService.ResolveEffect(diffVolSources, diffVolDest, Avalonia.Input.KeyModifiers.None);
        Assert.Equal(Avalonia.Input.DragDropEffects.Copy, diffVolEffect);

        // Different volume with Shift = Move
        var shiftEffect = FileDragDropService.ResolveEffect(diffVolSources, diffVolDest, Avalonia.Input.KeyModifiers.Shift);
        Assert.Equal(Avalonia.Input.DragDropEffects.Move, shiftEffect);
    }

    [Fact]
    public void FileDragDropService_PreventsRecursiveFolderDrop()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "clanker_drag_rec_" + Guid.NewGuid().ToString("N"));
        var parentDir = Path.Combine(tempRoot, "Parent");
        var childDir = Path.Combine(parentDir, "Child");
        Directory.CreateDirectory(childDir);

        try
        {
            // Moving Parent into Child -> None (illegal)
            var effect = FileDragDropService.ResolveEffect(new[] { parentDir }, childDir, Avalonia.Input.KeyModifiers.None);
            Assert.Equal(Avalonia.Input.DragDropEffects.None, effect);

            // Dropping directory onto itself -> None (illegal)
            var selfEffect = FileDragDropService.ResolveEffect(new[] { parentDir }, parentDir, Avalonia.Input.KeyModifiers.None);
            Assert.Equal(Avalonia.Input.DragDropEffects.None, selfEffect);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    [AvaloniaFact]
    public void FileIconService_ExtractsJumboIconForExtensions()
    {
        // Large (Jumbo 256x256) folder icon
        var folderLarge = FileIconService.Instance.GetFolderIcon(isLarge: true) as Avalonia.Media.Imaging.Bitmap;
        Assert.NotNull(folderLarge);
        Assert.True(folderLarge.PixelSize.Width >= 128, $"Folder large icon width: {folderLarge.PixelSize.Width}");

        // Small (16x16) folder icon
        var folderSmall = FileIconService.Instance.GetFolderIcon(isLarge: false) as Avalonia.Media.Imaging.Bitmap;
        Assert.NotNull(folderSmall);
        Assert.True(folderSmall.PixelSize.Width <= 32, $"Folder small icon width: {folderSmall.PixelSize.Width}");

        // Large extension icons
        foreach (var ext in new[] { ".pdf", ".zip", ".txt", ".cpp", ".exe" })
        {
            var large = FileIconService.Instance.GetExtensionIcon(ext, isLarge: true) as Avalonia.Media.Imaging.Bitmap;
            Assert.NotNull(large);
            Assert.True(large.PixelSize.Width >= 48, $"{ext} large icon width: {large.PixelSize.Width}");
        }
    }

    [AvaloniaFact]
    public async Task ThumbnailService_ExtractsRealVideoThumbnailAsync()
    {
        string videoPath = @"E:\FFDL\BadMommyPOV.25.05.02.Kelly.Caprice.Bad.Stepmommy.Seduction.XXX.1080p.MP4-WRB.mp4";
        if (!File.Exists(videoPath))
        {
            videoPath = @"C:\Users\5900x\Videos\Captures\297.mp4";
        }

        var storageFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(videoPath);
        var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(storageFile);
        var composition = new Windows.Media.Editing.MediaComposition();
        composition.Clips.Add(clip);

        var stream5m = await composition.GetThumbnailAsync(TimeSpan.FromMinutes(5), 256, 0, Windows.Media.Editing.VideoFramePrecision.NearestFrame);
        var streamDepth = await composition.GetThumbnailAsync(TimeSpan.FromTicks((long)(clip.OriginalDuration.Ticks * 0.5)), 256, 0, Windows.Media.Editing.VideoFramePrecision.NearestFrame);

        using var mem5 = new MemoryStream();
        await stream5m.AsStreamForRead().CopyToAsync(mem5);

        Assert.True(clip.OriginalDuration > TimeSpan.Zero, $"Duration was {clip.OriginalDuration}");
        Assert.True(mem5.Length > 1000, $"mem5 length was {mem5.Length}");
    }

    [Fact]
    public async Task NativeVideoPlayer_PlaysVideoAsync()
    {
        string videoPath = @"E:\FFDL\BadMommyPOV.25.05.02.Kelly.Caprice.Bad.Stepmommy.Seduction.XXX.1080p.MP4-WRB.mp4";
        if (!File.Exists(videoPath))
        {
            videoPath = @"C:\Users\5900x\Videos\Captures\297.mp4";
        }
        if (!File.Exists(videoPath)) return;

        var player = new ClankerExplorer.Services.Preview.NativeVideoPlayer();
        bool ok = await player.OpenAsync(videoPath);
        Assert.True(ok);
        Assert.True(player.Duration > TimeSpan.Zero);

        player.Play();
        await Task.Delay(300);
        Assert.True(player.GetPosition() >= TimeSpan.Zero);

        player.Pause();
        player.Dispose();
    }

    [Fact]
    public async Task ZipPreviewService_LoadsFullPreviewHierarchyAsync()
    {
        string tempZip = Path.Combine(Path.GetTempPath(), $"test_zip_hier_{Guid.NewGuid():N}.zip");
        try
        {
            using (var zipStream = new FileStream(tempZip, FileMode.Create))
            using (var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry1 = archive.CreateEntry("documents/subfolder/notes.txt");
                using (var writer = new StreamWriter(entry1.Open()))
                {
                    writer.Write("Detailed report contents");
                }
                var entry2 = archive.CreateEntry("images/photo.jpg");
                using (var writer = new StreamWriter(entry2.Open()))
                {
                    writer.Write("Mock JPG Image Data");
                }
            }

            var result = await ClankerExplorer.Services.Preview.ZipPreviewService.Instance.LoadArchivePreviewAsync(tempZip);
            Assert.True(result.Success);
            Assert.Equal(2, result.TotalFileCount);
            Assert.True(result.TotalFolderCount >= 2);
            Assert.True(result.TotalUncompressedBytes > 0);
            Assert.NotNull(result.Entries);
            Assert.Contains(result.Entries, e => e.Name == "notes.txt" && !e.IsDirectory);
            Assert.Contains(result.Entries, e => e.IsDirectory);
        }
        finally
        {
            if (File.Exists(tempZip)) File.Delete(tempZip);
        }
    }

    [Fact]
    public void PdfPreviewService_RecognizesPdfFiles()
    {
        Assert.True(ClankerExplorer.Services.Preview.PdfPreviewService.Instance.IsPdfFile("document.pdf"));
        Assert.True(ClankerExplorer.Services.Preview.PdfPreviewService.Instance.IsPdfFile(@"C:\Folder\REPORT.PDF"));
        Assert.False(ClankerExplorer.Services.Preview.PdfPreviewService.Instance.IsPdfFile("image.png"));
        Assert.False(ClankerExplorer.Services.Preview.PdfPreviewService.Instance.IsPdfFile(""));
    }

    [Fact]
    public async Task InspectorViewModel_SwitchesProvidersAndCleansUpStateAsync()
    {
        var vm = new InspectorViewModel();

        string tempZip = Path.Combine(Path.GetTempPath(), $"test_zip_vm_{Guid.NewGuid():N}.zip");
        string tempTxt = Path.Combine(Path.GetTempPath(), $"test_txt_vm_{Guid.NewGuid():N}.txt");
        try
        {
            using (var zipStream = new FileStream(tempZip, FileMode.Create))
            using (var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("test.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("Hello inside zip");
            }
            File.WriteAllText(tempTxt, "Plain text document");

            // 1. Select ZIP
            await vm.LoadPreviewAsync(tempZip);
            Assert.True(vm.IsZipPreview);
            Assert.False(vm.IsTextPreview);
            Assert.False(vm.IsImagePreview);
            Assert.False(vm.IsVideoPreview);
            Assert.True(vm.ZipEntries.Count > 0);

            // 2. Select Text
            await vm.LoadPreviewAsync(tempTxt);
            Assert.True(vm.IsTextPreview);
            Assert.False(vm.IsZipPreview);
            Assert.Empty(vm.ZipEntries);

            // 3. Clear Selection
            await vm.LoadPreviewAsync(null);
            Assert.False(vm.IsTextPreview);
            Assert.False(vm.IsZipPreview);
            Assert.Null(vm.PreviewData);
        }
        finally
        {
            if (File.Exists(tempZip)) File.Delete(tempZip);
            if (File.Exists(tempTxt)) File.Delete(tempTxt);
            vm.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetUInt32Delegate(IntPtr thisPtr, [In] ref PROPERTYKEY key, [Out] out uint pull);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetUInt64Delegate(IntPtr thisPtr, [In] ref PROPERTYKEY key, [Out] out ulong pull);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        ref Guid riid,
        out IntPtr ppv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseDelegate(IntPtr thisPtr);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPresentationAttributeDelegate(
        IntPtr thisPtr,
        uint dwStreamIndex,
        [In] ref Guid guidAttribute,
        [Out] out PROPVARIANT pvarAttribute);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFGetAttributeSize([In] IntPtr pAttributes, [In] ref Guid guidKey, [Out] out uint punWidth, [Out] out uint punHeight);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(uint Version, uint dwFlags);
    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MFCreateSourceReaderFromURL([In, MarshalAs(UnmanagedType.LPWStr)] string pwszURL, [In] IntPtr pAttributes, [Out] out IntPtr ppSourceReader);

    [ComImport, Guid("70ae66f2-c809-4e4f-aa91-40c211047fde"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSourceReader
    {
        void GetStreamSelection([In] uint dwStreamIndex, [Out, MarshalAs(UnmanagedType.Bool)] out bool pfSelected);
        void SetStreamSelection([In] uint dwStreamIndex, [In, MarshalAs(UnmanagedType.Bool)] bool fSelected);
        void GetNativeMediaType([In] uint dwStreamIndex, [In] uint dwMediaTypeIndex, [Out] out IntPtr ppMediaType);
        int GetCurrentMediaType([In] uint dwStreamIndex, [Out] out IntPtr ppMediaType);
        void SetCurrentMediaType([In] uint dwStreamIndex, [In] IntPtr pdwReserved, [In] IntPtr pMediaType);
        void SetCurrentPosition([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidTimeFormat, [In] ref PROPVARIANT varPosition);
        int ReadSample([In] uint dwStreamIndex, [In] uint dwControlFlags, [Out] out uint pdwActualStreamIndex, [Out] out uint pdwStreamFlags, [Out] out long pllTimestamp, [Out] out IntPtr ppSample);
        void Flush([In] uint dwStreamIndex);
        void GetServiceForStream([In] uint dwStreamIndex, [In, MarshalAs(UnmanagedType.LPStruct)] Guid guidService, [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid, [Out] out IntPtr ppvObject);
        int GetPresentationAttribute([In] uint dwStreamIndex, [In, MarshalAs(UnmanagedType.LPStruct)] Guid guidAttribute, [Out] out PROPVARIANT pvarAttribute);
    }

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4ad570c600a3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        void GetItem();
        void GetItemType();
        void CompareItem();
        void Compare();
        void GetUINT32();
        void GetUINT64();
        void GetDouble();
        void GetGUID();
        void GetStringLength();
        void GetString();
        void GetAllocatedString();
        void GetBlobSize();
        void GetBlob();
        void GetAllocatedBlob();
        void GetUnknown();
        void SetItem();
        void DeleteItem();
        void DeleteAllItems();
        void SetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] uint unValue);
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(2)] public ushort wReserved1;
        [FieldOffset(4)] public ushort wReserved2;
        [FieldOffset(6)] public ushort wReserved3;
        [FieldOffset(8)] public long hVal;
        [FieldOffset(8)] public ulong uhVal;
        [FieldOffset(8)] public IntPtr ptrVal;
    }
}
