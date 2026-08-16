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
}
