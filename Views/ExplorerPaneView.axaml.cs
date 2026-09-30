using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClankerExplorer.Services;
using ClankerExplorer.ViewModels;

namespace ClankerExplorer.Views;

// Construction and lifecycle wiring live here. Each ExplorerPaneView.<Behavior>.cs
// companion contains the handlers and state for that interaction.
public partial class ExplorerPaneView : UserControl
{
    public ExplorerPaneView()
    {
        InitializeComponent();

        _autoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _autoScrollTimer.Tick += OnAutoScrollTick;
        _middleScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _middleScrollTimer.Tick += OnMiddleScrollTick;
        _thumbnailDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _thumbnailDebounceTimer.Tick += OnThumbnailDebounceTick;
        _folderScrollSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _folderScrollSaveTimer.Tick += (_, _) =>
        {
            _folderScrollSaveTimer.Stop();
            SaveFolderScrollState(persist: true);
        };

        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        Loaded += OnPaneLoaded;
        Unloaded += OnPaneUnloaded;
    }

    private void OnPaneLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ExplorerPaneViewModel vm)
        {
            BindClipboardRequests(vm);
        }

        InitializeTabStrip();
        InitializeFileInputHandlers();
        InitializeThumbnailViewport();
        InitializeFolderViewRestoration();
        InitializeContextMenus();
        Dispatcher.UIThread.Post(UpdateTabScrollButtonsVisibility, DispatcherPriority.Loaded);
    }

    private void OnPaneUnloaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ExplorerPaneViewModel vm)
        {
            vm.BeforeThumbnailLayoutChanging -= OnBeforeThumbnailLayoutChanging;
            vm.AfterThumbnailLayoutChanged -= OnAfterThumbnailLayoutChanged;
        }
        _thumbnailDebounceTimer.Stop();
        _folderScrollSaveTimer.Stop();
        var cts = _thumbnailViewportCts;
        _thumbnailViewportCts = null;
        cts?.Cancel();
        ThumbnailService.Instance.CancelPendingRequests();
        _retainedThumbnailItems.Clear();
    }
}
