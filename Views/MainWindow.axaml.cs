using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using ClankerExplorer.Models;
using ClankerExplorer.Services;
using ClankerExplorer.ViewModels;

namespace ClankerExplorer.Views;

public partial class MainWindow : Window
{
    private ColumnDefinition? PreviewColumnDefinition => MainContentGrid != null && MainContentGrid.ColumnDefinitions.Count > 3 ? MainContentGrid.ColumnDefinitions[3] : null;

    public MainWindow()
    {
        InitializeComponent();

        Closing += (s, e) =>
        {
            if (DataContext is MainViewModel vm)
            {
                SessionService.Instance.SaveSession(vm);
            }
        };

        Loaded += (s, e) =>
        {
            if (DataContext is MainViewModel vm)
            {
                SyncPreviewColumn(vm);
                vm.RequestCreateItem += async (type, parent) =>
                {
                    try
                    {
                        var dlg = new NewItemWindow(type, parent);
                        var res = await dlg.ShowDialog<bool>(this);
                        if (res)
                        {
                            if (type == "folder")
                            {
                                FileSystemService.Instance.CreateFolder(parent, dlg.ItemName);
                            }
                            else
                            {
                                FileSystemService.Instance.CreateFile(parent, dlg.ItemName);
                            }
                            await vm.ActivePane.RefreshAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        await ShowErrorDialogAsync("Create Failed", ex.Message);
                    }
                };

                vm.RequestOpenNetworkShare += async () =>
                {
                    var dlg = new NetworkShareWindow();
                    var res = await dlg.ShowDialog<bool>(this);
                    if (res && !string.IsNullOrWhiteSpace(dlg.NetworkPath))
                    {
                        var path = dlg.NetworkPath.Trim();
                        var serverName = path.TrimStart('\\').Split('\\')[0];
                        if (!string.IsNullOrEmpty(serverName))
                        {
                            vm.AddDiscoveredServer(serverName);
                        }
                        vm.NavigateSidebar(path);
                    }
                };

                vm.RequestOpenSettings += async () =>
                {
                    var dlg = new SettingsWindow();
                    await dlg.ShowDialog(this);
                    vm.RefreshAll();
                };

                vm.RequestProperties += async item =>
                {
                    var target = item ?? new FileItem
                    {
                        FullPath = vm.ActivePane.SelectedTab?.CurrentPath ?? @"C:\",
                        Name = System.IO.Path.GetFileName(vm.ActivePane.SelectedTab?.CurrentPath ?? @"C:\"),
                        IsDirectory = true
                    };
                    var dlg = new PropertiesWindow(target);
                    await dlg.ShowDialog(this);
                };

                vm.RequestVideoThumbnailAtTime += async item =>
                {
                    if (item == null || string.IsNullOrEmpty(item.FullPath)) return;
                    try
                    {
                        var dlg = new VideoThumbnailTimeWindow(item.FullPath);
                        var res = await dlg.ShowDialog<bool>(this);
                        if (res)
                        {
                            int targetSize = (int)vm.ActivePane.ThumbnailSize;
                            var newBmp = await VideoThumbnailService.Instance.ExtractFrameAtTimeAsync(item.FullPath, dlg.TargetTimeSpan, targetSize);
                            if (newBmp != null)
                            {
                                ThumbnailService.Instance.SetCustomThumbnail(item.FullPath, item.ModifiedTime, newBmp, targetSize);
                                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                {
                                    item.ThumbnailImage = newBmp;
                                });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        await ShowErrorDialogAsync("Thumbnail Extraction Failed", ex.Message);
                    }
                };

                vm.RequestDeleteWithConfirmation += async (item, perm) =>
                {
                    if (item == null) return;
                    try
                    {
                        var settings = SettingsService.Instance.CurrentSettings;
                        if (settings.ConfirmBeforeDelete)
                        {
                            var dlg = new ConfirmDeleteWindow(item.Name, item.FullPath, perm);
                            var res = await dlg.ShowDialog<bool>(this);
                            if (!res) return;
                        }

                        await FileSystemService.Instance.DeleteAsync(new[] { item.FullPath }, perm);
                        await vm.ActivePane.RefreshAsync();
                    }
                    catch (Exception ex)
                    {
                        await ShowErrorDialogAsync("Delete Failed", ex.Message);
                    }
                };

                vm.RequestDeleteMultipleWithConfirmation += async (items, perm) =>
                {
                    if (items == null || items.Count == 0) return;
                    try
                    {
                        var settings = SettingsService.Instance.CurrentSettings;
                        if (settings.ConfirmBeforeDelete)
                        {
                            var dlg = new ConfirmDeleteWindow($"{items.Count} items", $"{items.Count} selected files and folders", perm);
                            var res = await dlg.ShowDialog<bool>(this);
                            if (!res) return;
                        }

                        await FileSystemService.Instance.DeleteAsync(items.Select(i => i.FullPath).ToArray(), perm);
                        await vm.ActivePane.RefreshAsync();
                    }
                    catch (Exception ex)
                    {
                        await ShowErrorDialogAsync("Delete Failed", ex.Message);
                    }
                };
            }
        };

        // Hotkey bindings & Mouse navigation
        KeyDown += (s, e) =>
        {
            if (DataContext is not MainViewModel vm) return;

            // Delete / Shift+Delete Key: Delete Selected
            if (e.Key == Key.Delete)
            {
                if (e.Source is not TextBox)
                {
                    bool isPermanent = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                    vm.ActivePane.DeleteSelected(isPermanent);
                    e.Handled = true;
                    return;
                }
            }

            // F3: Toggle Inspector
            if (e.Key == Key.F3)
            {
                vm.ToggleInspector();
                e.Handled = true;
            }
            // Ctrl+Shift+D: Toggle Dual Pane
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.D)
            {
                vm.ToggleDualPane();
                e.Handled = true;
            }
            // Ctrl+T: New Tab
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.T)
            {
                vm.ActivePane.AddNewTab();
                e.Handled = true;
            }
            // Ctrl+W: Close Tab
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.W)
            {
                vm.ActivePane.CloseTab(null);
                e.Handled = true;
            }
            // Alt+Left / Backspace: Go Back
            else if ((e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key == Key.Left) || e.Key == Key.Back)
            {
                if (e.Source is not TextBox)
                {
                    vm.ActivePane.GoBack();
                    e.Handled = true;
                }
            }
            // Alt+Right: Go Forward
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key == Key.Right)
            {
                if (e.Source is not TextBox)
                {
                    vm.ActivePane.GoForward();
                    e.Handled = true;
                }
            }
            // F5: Refresh
            else if (e.Key == Key.F5)
            {
                vm.RefreshAll();
                e.Handled = true;
            }
        };

        void SyncPreviewColumn(MainViewModel? vm)
        {
            if (vm == null || PreviewColumnDefinition == null) return;
            if (!vm.ShowInspector)
            {
                PreviewColumnDefinition.MinWidth = 0;
                PreviewColumnDefinition.MaxWidth = 0;
                PreviewColumnDefinition.Width = new GridLength(0);
            }
            else
            {
                double maxAllowed = Math.Max(240, Bounds.Width - 660);
                double safeWidth = Math.Clamp(vm.InspectorWidth, 240, maxAllowed);
                PreviewColumnDefinition.MinWidth = 240;
                PreviewColumnDefinition.MaxWidth = maxAllowed;
                PreviewColumnDefinition.Width = new GridLength(safeWidth);
            }
        }

        DataContextChanged += (s, e) =>
        {
            if (DataContext is MainViewModel vm)
            {
                SyncPreviewColumn(vm);
                vm.PropertyChanged += (sender, args) =>
                {
                    if (args.PropertyName == nameof(MainViewModel.ShowInspector))
                    {
                        SyncPreviewColumn(vm);
                    }
                    else if (args.PropertyName == nameof(MainViewModel.InspectorWidth) && vm.ShowInspector)
                    {
                        SyncPreviewColumn(vm);
                    }
                };
            }
        };

        TabDragCoordinator.Instance.TabDragMoved += OnTabDragMoved;
        TabDragCoordinator.Instance.TabDragEnded += OnTabDragEnded;

        SizeChanged += (s, e) =>
        {
            if (DataContext is MainViewModel vm && vm.ShowInspector && PreviewColumnDefinition != null)
            {
                double maxAllowed = Math.Max(240, Bounds.Width - 660);
                PreviewColumnDefinition.MaxWidth = maxAllowed;
                if (PreviewColumnDefinition.Width.Value > maxAllowed)
                {
                    PreviewColumnDefinition.Width = new GridLength(maxAllowed);
                    vm.InspectorWidth = maxAllowed;
                }
            }
        };

    }

    private void OnTabDragMoved(ExplorerTabViewModel tab, Point winPos, bool isCtrl)
    {
        if (TabDragGhost != null && TabGhostTitle != null && TabGhostPinIcon != null && TabGhostFolderIcon != null && TabGhostCopyBadge != null)
        {
            TabDragGhost.IsVisible = true;
            TabGhostTitle.Text = tab.Title;
            TabGhostPinIcon.IsVisible = tab.IsPinned;
            TabGhostFolderIcon.IsVisible = !tab.IsPinned;
            TabGhostCopyBadge.IsVisible = isCtrl;

            Canvas.SetLeft(TabDragGhost, Math.Max(0, winPos.X - 15));
            Canvas.SetTop(TabDragGhost, Math.Max(0, winPos.Y - 20));
        }
    }

    private void OnTabDragEnded()
    {
        if (TabDragGhost != null)
        {
            TabDragGhost.IsVisible = false;
        }
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled) return;
        if (DataContext is MainViewModel vm)
        {
            var props = e.GetCurrentPoint(this).Properties;
            if (props.IsXButton1Pressed)
            {
                vm.ActivePane.GoBack();
                e.Handled = true;
            }
            else if (props.IsXButton2Pressed)
            {
                vm.ActivePane.GoForward();
                e.Handled = true;
            }
        }
    }

    private void OnWindowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (TabDragCoordinator.Instance.IsDragging && TabDragCoordinator.Instance.DraggedTab != null)
        {
            var winPos = e.GetPosition(this);
            var visuals = this.GetVisualsAt(winPos).ToList();

            ExplorerPaneViewModel? targetPane = null;
            ExplorerTabViewModel? targetTab = null;
            bool isLeftHalf = false;

            foreach (var v in visuals)
            {
                if (targetTab == null && v.DataContext is ExplorerTabViewModel tvm)
                {
                    targetTab = tvm;
                    var tabPos = e.GetPosition(v);
                    isLeftHalf = tabPos.X < v.Bounds.Width / 2;
                }

                if (targetPane == null && v is ExplorerPaneView paneView && paneView.DataContext is ExplorerPaneViewModel pvm)
                {
                    targetPane = pvm;
                }
                else if (targetPane == null && v.DataContext is ExplorerPaneViewModel pvm2)
                {
                    targetPane = pvm2;
                }
            }

            TabDragCoordinator.Instance.UpdateDrag(targetPane, targetTab, isLeftHalf, e.KeyModifiers.HasFlag(KeyModifiers.Control), winPos);
        }
    }

    private void OnWindowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (TabDragCoordinator.Instance.IsDragging)
        {
            var winPos = e.GetPosition(this);
            var visuals = this.GetVisualsAt(winPos).ToList();

            ExplorerPaneViewModel? targetPane = TabDragCoordinator.Instance.CurrentTargetPane;
            ExplorerTabViewModel? targetTab = TabDragCoordinator.Instance.CurrentHoveredTab;
            bool isLeft = TabDragCoordinator.Instance.IsLeftDropHalf;
            bool isCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

            // Double check targetPane and targetTab from visuals at drop position
            if (targetPane == null || targetTab == null)
            {
                foreach (var v in visuals)
                {
                    if (targetTab == null && v.DataContext is ExplorerTabViewModel tvm)
                    {
                        targetTab = tvm;
                        var tabPos = e.GetPosition(v);
                        isLeft = tabPos.X < v.Bounds.Width / 2;
                    }

                    if (targetPane == null && v is ExplorerPaneView paneView && paneView.DataContext is ExplorerPaneViewModel pvm)
                    {
                        targetPane = pvm;
                    }
                    else if (targetPane == null && v.DataContext is ExplorerPaneViewModel pvm2)
                    {
                        targetPane = pvm2;
                    }
                }
            }

            // Check if dropped into a valid pane / tab area
            bool isOverValidPane = targetPane != null && visuals.Any(v => v is ExplorerPaneView || (v.DataContext is ExplorerPaneViewModel) || (v.DataContext is ExplorerTabViewModel));

            if (isOverValidPane && targetPane != null)
            {
                int targetIndex = -1;
                if (targetTab != null)
                {
                    int idx = targetPane.Tabs.IndexOf(targetTab);
                    if (idx >= 0)
                    {
                        targetIndex = isLeft ? idx : idx + 1;
                    }
                }
                else
                {
                    targetIndex = targetPane.Tabs.Count;
                }

                TabDragCoordinator.Instance.CompleteDrop(targetPane, targetIndex, isCtrl);
            }
            else
            {
                // Dropped outside or over invalid drop zone -> SNAP BACK safely!
                TabDragCoordinator.Instance.CancelDrag();
            }
        }
    }

    private void OnWindowPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (TabDragCoordinator.Instance.IsDragging)
        {
            TabDragCoordinator.Instance.CancelDrag();
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnToggleNetworkPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.ToggleNetworkSectionCommand.Execute(null);
        }
    }

    private void OnDriveClicked(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.DataContext is DriveModel drive && DataContext is MainViewModel vm)
        {
            vm.NavigateSidebar(drive.RootPath);
        }
    }

    private void OnNetworkNodeClicked(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.DataContext is NetworkNode node && DataContext is MainViewModel vm)
        {
            if (!string.IsNullOrWhiteSpace(node.UncPath))
            {
                vm.NavigateSidebar(node.UncPath);
            }
        }
    }

    private QuickAccessItem? _pressedQuickAccessItem;
    private Point _qaPressPoint;
    private bool _isQaDragging;
    private IInputElement? _capturedQaBorder;
    private QuickAccessItem? _hoveredQaTarget;
    private bool _isQaDropTop;

    private void OnQuickAccessPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is IInputElement inputElem && sender is Visual visual && visual.DataContext is QuickAccessItem item && DataContext is MainViewModel vm)
        {
            if (e.GetCurrentPoint(visual).Properties.IsLeftButtonPressed)
            {
                _pressedQuickAccessItem = item;
                _qaPressPoint = e.GetPosition(this);
                _isQaDragging = false;
                _capturedQaBorder = inputElem;
                vm.NavigateSidebar(item.Path);
            }
        }
    }

    private void OnQuickAccessPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedQuickAccessItem != null && DataContext is MainViewModel vm)
        {
            var cur = e.GetPosition(this);
            var delta = cur - _qaPressPoint;

            if (!_isQaDragging && PointerGestureClassifier.ExceedsDragThreshold(delta.X, delta.Y, 6))
            {
                _isQaDragging = true;
                if (_capturedQaBorder != null)
                {
                    e.Pointer.Capture(_capturedQaBorder);
                }
                _pressedQuickAccessItem.IsBeingDragged = true;
            }

            if (_isQaDragging)
            {
                if (QuickAccessDragGhost != null && QaGhostIcon != null && QaGhostTitle != null)
                {
                    QuickAccessDragGhost.IsVisible = true;
                    QaGhostIcon.Text = _pressedQuickAccessItem.IconSymbol;
                    QaGhostTitle.Text = _pressedQuickAccessItem.DisplayName;

                    Canvas.SetLeft(QuickAccessDragGhost, Math.Max(0, cur.X - 15));
                    Canvas.SetTop(QuickAccessDragGhost, Math.Max(0, cur.Y - 15));
                }

                var topLevel = TopLevel.GetTopLevel(this);
                var hitVisual = topLevel?.InputHitTest(cur) as Visual;
                QuickAccessItem? targetItem = null;
                bool isTop = false;

                while (hitVisual != null)
                {
                    if (hitVisual.DataContext is QuickAccessItem qai)
                    {
                        targetItem = qai;
                        var itemPos = e.GetPosition(hitVisual);
                        isTop = itemPos.Y < hitVisual.Bounds.Height / 2;
                        break;
                    }
                    hitVisual = hitVisual.GetVisualParent();
                }

                // Update indicators
                if (_hoveredQaTarget != null && _hoveredQaTarget != targetItem)
                {
                    _hoveredQaTarget.IsDropTargetTop = false;
                    _hoveredQaTarget.IsDropTargetBottom = false;
                }

                _hoveredQaTarget = targetItem;
                _isQaDropTop = isTop;

                if (targetItem != null && targetItem != _pressedQuickAccessItem)
                {
                    targetItem.IsDropTargetTop = isTop;
                    targetItem.IsDropTargetBottom = !isTop;
                }
            }
        }
    }

    private void OnQuickAccessPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isQaDragging && _pressedQuickAccessItem != null && DataContext is MainViewModel vm)
        {
            if (_hoveredQaTarget != null && _hoveredQaTarget != _pressedQuickAccessItem)
            {
                int fromIdx = vm.QuickAccess.IndexOf(_pressedQuickAccessItem);
                int toIdx = vm.QuickAccess.IndexOf(_hoveredQaTarget);

                if (fromIdx >= 0 && toIdx >= 0)
                {
                    if (!_isQaDropTop && fromIdx > toIdx)
                    {
                        toIdx++;
                    }
                    else if (_isQaDropTop && fromIdx < toIdx)
                    {
                        toIdx--;
                    }

                    toIdx = Math.Clamp(toIdx, 0, vm.QuickAccess.Count - 1);
                    vm.ReorderQuickAccess(fromIdx, toIdx);
                }
            }

            if (_capturedQaBorder != null)
            {
                e.Pointer.Capture(null);
            }
        }

        ClearQuickAccessDragState();
    }

    private void OnQuickAccessPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        ClearQuickAccessDragState();
    }

    private void ClearQuickAccessDragState()
    {
        if (QuickAccessDragGhost != null)
        {
            QuickAccessDragGhost.IsVisible = false;
        }

        if (_pressedQuickAccessItem != null)
        {
            _pressedQuickAccessItem.IsBeingDragged = false;
        }

        if (_hoveredQaTarget != null)
        {
            _hoveredQaTarget.IsDropTargetTop = false;
            _hoveredQaTarget.IsDropTargetBottom = false;
        }

        if (DataContext is MainViewModel vm)
        {
            foreach (var item in vm.QuickAccess)
            {
                item.IsBeingDragged = false;
                item.IsDropTargetTop = false;
                item.IsDropTargetBottom = false;
            }
        }

        _pressedQuickAccessItem = null;
        _hoveredQaTarget = null;
        _isQaDragging = false;
        _capturedQaBorder = null;
    }

    private void OnWslRootClicked(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.DataContext is WslDistroItem distro && DataContext is MainViewModel vm)
        {
            vm.NavigateSidebar(distro.RootPath);
        }
    }

    private void OnWslHomeClicked(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.DataContext is WslDistroItem distro && DataContext is MainViewModel vm)
        {
            vm.NavigateSidebar(distro.HomePath);
        }
    }

    private void OnFrequentFolderClicked(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.DataContext is FrequentFolderItem item && DataContext is MainViewModel vm)
        {
            vm.NavigateSidebar(item.Path);
        }
    }

    private void OnLeftPanePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.SetActivePane("left");
        }
    }

    private void OnRightPanePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.SetActivePane("right");
        }
    }

    private async System.Threading.Tasks.Task ShowErrorDialogAsync(string title, string message)
    {
        var win = new Window
        {
            Title = title,
            Width = 440,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = (Avalonia.Media.IBrush)this.FindResource("AppSurfaceBrush")!,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"⚠️ {title}",
                        FontSize = 14,
                        FontWeight = Avalonia.Media.FontWeight.Bold,
                        Foreground = (Avalonia.Media.IBrush)this.FindResource("AppTextBrush")!
                    },
                    new TextBlock
                    {
                        Text = message,
                        FontSize = 12,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Foreground = (Avalonia.Media.IBrush)this.FindResource("AppSubTextBrush")!
                    },
                    new Button
                    {
                        Content = "OK",
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Classes = { "power-btn" }
                    }
                }
            }
        };

        var btn = (Button)((StackPanel)win.Content).Children[2];
        btn.Click += (s, e) => win.Close();

        await win.ShowDialog(this);
    }

    private void OnInspectorSplitterDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.ResetInspectorWidth();
            if (PreviewColumnDefinition != null && vm.ShowInspector)
            {
                PreviewColumnDefinition.MinWidth = 240;
                PreviewColumnDefinition.Width = new GridLength(320);
            }
        }
    }

    private void OnInspectorSplitterDragCompleted(object? sender, Avalonia.Input.VectorEventArgs e)
    {
        if (DataContext is MainViewModel vm &&
            vm.ShowInspector &&
            PreviewColumnDefinition != null &&
            PreviewColumnDefinition.ActualWidth >= 240)
        {
            double maxAllowed = Math.Max(240, Bounds.Width - 660);
            vm.InspectorWidth = Math.Clamp(PreviewColumnDefinition.ActualWidth, 240, maxAllowed);
        }
    }
}
