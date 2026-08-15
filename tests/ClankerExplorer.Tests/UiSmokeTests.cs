using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClankerExplorer.Tests.TestInfrastructure;
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
}
