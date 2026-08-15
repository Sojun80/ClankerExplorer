using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClankerExplorer.Tests.TestInfrastructure;
using ClankerExplorer.Models;
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
            Assert.Contains(
                view.GetVisualDescendants().OfType<TextBlock>(),
                text => text.IsVisible && text.Text == "visible-in-thumbnails.txt");
        }
        finally
        {
            window.Close();
        }
    }
}
