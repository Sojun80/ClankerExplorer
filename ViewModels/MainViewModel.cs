using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClankerExplorer.Models;
using ClankerExplorer.Services;

namespace ClankerExplorer.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LeftPaneColumnSpan))]
    private bool _isDualPane;

    public int LeftPaneColumnSpan => IsDualPane ? 1 : 3;

    [ObservableProperty]
    private bool _showInspector = true;

    [ObservableProperty]
    private double _inspectorWidth = 320.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NetworkExpandArrow))]
    private bool _isNetworkExpanded = false;

    public string NetworkExpandArrow => IsNetworkExpanded ? "▾" : "▸";

    [ObservableProperty]
    private bool _isScanningNetwork;

    [ObservableProperty]
    private ExplorerPaneViewModel _leftPane;

    [ObservableProperty]
    private ExplorerPaneViewModel _rightPane;

    [ObservableProperty]
    private ExplorerPaneViewModel _activePane;

    [ObservableProperty]
    private InspectorViewModel _inspector = new();

    [ObservableProperty]
    private ObservableCollection<DriveModel> _drives = new();

    [ObservableProperty]
    private ObservableCollection<DriveModel> _localDrives = new();

    [ObservableProperty]
    private ObservableCollection<DriveModel> _networkDrives = new();

    [ObservableProperty]
    private ObservableCollection<NetworkNode> _networkComputers = new();

    [ObservableProperty]
    private ObservableCollection<QuickAccessItem> _quickAccess = new();

    [ObservableProperty]
    private ObservableCollection<WslDistroItem> _wslDistros = new();

    [ObservableProperty]
    private ObservableCollection<FrequentFolderItem> _frequentFolders = new();

    [ObservableProperty]
    private ObservableCollection<FrequentFolderItem> _recentFolders = new();

    [ObservableProperty]
    private bool _showHistoryUndoBanner;

    [ObservableProperty]
    private string _historyUndoMessage = string.Empty;

    [ObservableProperty]
    private DriveModel? _currentDrive;

    public event Action<string, string>? RequestCreateItem;
    public event Action? RequestOpenNetworkShare;
    public event Action? RequestOpenSettings;
    public event Action<FileItem>? RequestRename;
    public event Action<FileItem?>? RequestProperties;
    public event Action<FileItem, bool>? RequestDeleteWithConfirmation;

    public MainViewModel()
    {
        var settings = SettingsService.Instance.CurrentSettings;
        var startPath = string.IsNullOrWhiteSpace(settings.DefaultPath) ? FileSystemService.DefaultRootPath : settings.DefaultPath;
        if (!Directory.Exists(startPath)) startPath = FileSystemService.DefaultRootPath;

        InitializePanesFromStartupSettings(settings, startPath);

        QuickAccessService.Instance.QuickAccessChanged += RefreshQuickAccess;

        LoadSidebarData();
    }

    private void InitializePanesFromStartupSettings(AppSettings settings, string defaultPath)
    {
        IsDualPane = settings.StartInDualPane;
        ShowInspector = settings.ShowInspectorOnStartup;

        var session = SessionService.Instance.LoadSession();
        InspectorWidth = session != null && session.InspectorWidth > 150 ? session.InspectorWidth : (settings.InspectorWidth > 150 ? settings.InspectorWidth : 320.0);

        if (settings.StartupBehavior == "RestoreSession" && session != null)
        {
            IsDualPane = session.IsDualPane;
            var paneLeft = RestorePaneFromSession("left", session.LeftPane, defaultPath, "PANE 1");
            var paneRight = RestorePaneFromSession("right", session.RightPane, defaultPath, "PANE 2");

            if (session.ActivePaneId == "right" && IsDualPane)
            {
                paneLeft.IsActive = false;
                paneRight.IsActive = true;
                ActivePane = paneRight;
            }
            else
            {
                paneLeft.IsActive = true;
                paneRight.IsActive = false;
                ActivePane = paneLeft;
            }

            LeftPane = paneLeft;
            RightPane = paneRight;
        }
        else if (settings.StartupBehavior == "OpenPinned" && session != null)
        {
            var paneLeft = RestorePinnedPane("left", session.LeftPane, defaultPath, "PANE 1");
            var paneRight = RestorePinnedPane("right", session.RightPane, defaultPath, "PANE 2");
            paneLeft.IsActive = true;
            paneRight.IsActive = false;
            LeftPane = paneLeft;
            RightPane = paneRight;
            ActivePane = paneLeft;
        }
        else
        {
            // Open a single default location
            var paneLeft = new ExplorerPaneViewModel("left", defaultPath, "PANE 1") { IsActive = true };
            var paneRight = new ExplorerPaneViewModel("right", defaultPath, "PANE 2") { IsActive = false };
            LeftPane = paneLeft;
            RightPane = paneRight;
            ActivePane = paneLeft;
        }

        WirePaneEvents(LeftPane);
        WirePaneEvents(RightPane);
    }

    private static ExplorerPaneViewModel RestorePaneFromSession(string paneId, PaneSessionState? paneSession, string defaultPath, string label)
    {
        var settings = SettingsService.Instance.CurrentSettings;
        var pane = new ExplorerPaneViewModel(paneId, defaultPath, label);

        if (paneSession != null && paneSession.Tabs.Count > 0)
        {
            var filteredTabs = SessionService.Instance.FilterTabsToRestore(
                paneSession.Tabs,
                settings.MaxTabsRestoredOnStartup,
                paneSession.ActiveTabPath
            );

            if (filteredTabs.Count > 0)
            {
                pane.Tabs.Clear();
                ExplorerTabViewModel? selectedTab = null;

                foreach (var tabInfo in filteredTabs)
                {
                    var tab = new ExplorerTabViewModel(tabInfo.Path)
                    {
                        IsPinned = tabInfo.IsPinned,
                        LastActiveTime = tabInfo.LastActiveTime
                    };
                    pane.Tabs.Add(tab);
                    pane.WireTabEvents(tab);

                    if (selectedTab == null || string.Equals(tab.CurrentPath, paneSession.ActiveTabPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    {
                        selectedTab = tab;
                    }
                }

                pane.SelectedTab = selectedTab ?? pane.Tabs.FirstOrDefault();
                return pane;
            }
        }

        return pane;
    }

    private static ExplorerPaneViewModel RestorePinnedPane(string paneId, PaneSessionState? paneSession, string defaultPath, string label)
    {
        var pane = new ExplorerPaneViewModel(paneId, defaultPath, label);

        if (paneSession != null)
        {
            var pinned = paneSession.Tabs.Where(t => t.IsPinned && Directory.Exists(t.Path)).ToList();
            if (pinned.Count > 0)
            {
                pane.Tabs.Clear();
                foreach (var t in pinned)
                {
                    var tab = new ExplorerTabViewModel(t.Path)
                    {
                        IsPinned = true,
                        LastActiveTime = t.LastActiveTime
                    };
                    pane.Tabs.Add(tab);
                    pane.WireTabEvents(tab);
                }
                pane.SelectedTab = pane.Tabs.FirstOrDefault();
                return pane;
            }
        }

        return pane;
    }

    private void WirePaneEvents(ExplorerPaneViewModel pane)
    {
        pane.FileSelectedForPreview += item =>
        {
            _ = Inspector.LoadPreviewAsync(item?.FullPath);
            UpdateCurrentDrive();
            RefreshHistoryData();
        };

        pane.RequestCreateItem += (type, parent) => RequestCreateItem?.Invoke(type, parent);
        pane.RequestOpenInOtherPane += path =>
        {
            if (!IsDualPane) ToggleDualPane();
            var targetPane = pane == LeftPane ? RightPane : LeftPane;
            targetPane.AddNewTab(path);
        };
        pane.RequestPinFolder += path => QuickAccessService.Instance.PinFolder(path);
        pane.RequestRename += item => RequestRename?.Invoke(item);
        pane.RequestProperties += item => RequestProperties?.Invoke(item);
        pane.RequestDeleteWithConfirmation += (item, perm) => RequestDeleteWithConfirmation?.Invoke(item, perm);
    }

    public void SetActivePane(string paneId)
    {
        if (paneId == "left")
        {
            LeftPane.IsActive = true;
            RightPane.IsActive = false;
            ActivePane = LeftPane;
        }
        else
        {
            LeftPane.IsActive = false;
            RightPane.IsActive = true;
            ActivePane = RightPane;
        }
        UpdateCurrentDrive();
    }

    public void LoadSidebarData()
    {
        var driveList = FileSystemService.Instance.GetDrives();
        Drives = new ObservableCollection<DriveModel>(driveList);
        LocalDrives = new ObservableCollection<DriveModel>(driveList.Where(d => !d.IsNetworkDrive));
        NetworkDrives = new ObservableCollection<DriveModel>(driveList.Where(d => d.IsNetworkDrive));

        RefreshQuickAccess();

        WslDistros = new ObservableCollection<WslDistroItem>();
        _ = LoadWslDistributionsAsync();

        RefreshHistoryData();
        UpdateCurrentDrive();
    }

    public void RefreshQuickAccess()
    {
        QuickAccess = new ObservableCollection<QuickAccessItem>(QuickAccessService.Instance.Items);
    }

    private async Task LoadWslDistributionsAsync()
    {
        var wslList = await FileSystemService.Instance.GetWslDistributionsAsync();
        WslDistros = new ObservableCollection<WslDistroItem>(wslList);
    }

    public void RefreshHistoryData()
    {
        var exclude = QuickAccess.Select(q => q.Path);
        var frequent = HistoryService.Instance.GetFrequentFolders(exclude, 5);
        FrequentFolders = new ObservableCollection<FrequentFolderItem>(frequent);

        var recent = HistoryService.Instance.GetRecentFolders(exclude, 5);
        RecentFolders = new ObservableCollection<FrequentFolderItem>(recent);
    }

    [RelayCommand]
    public void ResetFolderHistory(FrequentFolderItem item)
    {
        if (item == null) return;
        HistoryService.Instance.ResetFolderHistory(item.Path);
        HistoryUndoMessage = $"Reset history for '{item.DisplayName}'";
        ShowHistoryUndoBanner = true;
        RefreshHistoryData();
    }

    [RelayCommand]
    public void UndoResetHistory()
    {
        if (HistoryService.Instance.UndoReset())
        {
            ShowHistoryUndoBanner = false;
            RefreshHistoryData();
        }
    }

    [RelayCommand]
    public void DismissHistoryUndo()
    {
        ShowHistoryUndoBanner = false;
    }

    [RelayCommand]
    public async Task ScanNetwork()
    {
        IsScanningNetwork = true;
        try
        {
            var nodes = await NetworkDiscoveryService.Instance.DiscoverComputersAsync();
            NetworkComputers = new ObservableCollection<NetworkNode>(nodes);
        }
        finally
        {
            IsScanningNetwork = false;
        }
    }

    [RelayCommand]
    public async Task ToggleExpandNode(NetworkNode node)
    {
        node.IsExpanded = !node.IsExpanded;
        if (node.IsExpanded && !node.HasLoadedChildren)
        {
            node.IsLoading = true;
            try
            {
                var shares = await NetworkDiscoveryService.Instance.GetSharesForComputerAsync(node.Name);
                node.Children.Clear();
                foreach (var sh in shares)
                {
                    node.Children.Add(sh);
                }
                node.HasLoadedChildren = true;
            }
            finally
            {
                node.IsLoading = false;
            }
        }
    }

    [RelayCommand]
    public async Task ToggleNetworkSection()
    {
        IsNetworkExpanded = !IsNetworkExpanded;
        if (IsNetworkExpanded && NetworkComputers.Count == 0)
        {
            await ScanNetwork();
        }
    }

    public void UpdateCurrentDrive()
    {
        var currentPath = ActivePane.SelectedTab?.CurrentPath ?? @"C:\";
        CurrentDrive = Drives.FirstOrDefault(d => currentPath.StartsWith(d.Letter, StringComparison.OrdinalIgnoreCase) ||
                                                 (d.IsNetworkDrive && currentPath.StartsWith(d.RootPath, StringComparison.OrdinalIgnoreCase)));
    }

    [RelayCommand]
    public void ToggleDualPane()
    {
        IsDualPane = !IsDualPane;
        if (IsDualPane)
        {
            if (RightPane.SelectedTab == null)
            {
                RightPane.AddNewTab(LeftPane.SelectedTab?.CurrentPath ?? @"C:\");
            }
            else
            {
                var target = string.IsNullOrEmpty(RightPane.SelectedTab.CurrentPath)
                    ? (LeftPane.SelectedTab?.CurrentPath ?? @"C:\")
                    : RightPane.SelectedTab.CurrentPath;
                RightPane.SelectedTab.NavigateTo(target);
            }
        }
    }

    [RelayCommand]
    public void ToggleInspector()
    {
        ShowInspector = !ShowInspector;
    }

    [RelayCommand]
    public void RefreshAll()
    {
        LeftPane.LoadColumnSettings();
        RightPane.LoadColumnSettings();
        LoadSidebarData();
        if (IsNetworkExpanded) _ = ScanNetwork();
        LeftPane.Refresh();
        if (IsDualPane) RightPane.Refresh();
    }

    [RelayCommand]
    public void OpenTerminal()
    {
        ActivePane.OpenTerminal();
    }

    [RelayCommand]
    public void OpenTerminalAdmin()
    {
        ActivePane.OpenTerminalAdmin();
    }

    [RelayCommand]
    public void OpenCmd()
    {
        ActivePane.OpenCmd();
    }

    [RelayCommand]
    public void OpenCmdAdmin()
    {
        ActivePane.OpenCmdAdmin();
    }

    [RelayCommand]
    public void OpenVSCode()
    {
        ActivePane.OpenVSCode();
    }

    [RelayCommand]
    public void NavigateSidebar(string path)
    {
        ActivePane.SelectedTab?.NavigateTo(path);
        UpdateCurrentDrive();
        RefreshHistoryData();
    }

    [RelayCommand]
    public void PromptNetworkShare()
    {
        RequestOpenNetworkShare?.Invoke();
    }

    [RelayCommand]
    public void OpenSettings()
    {
        RequestOpenSettings?.Invoke();
    }

    public void AddDiscoveredServer(string serverName)
    {
        NetworkDiscoveryService.Instance.AddCustomServer(serverName);
        IsNetworkExpanded = true;
        _ = ScanNetwork();
    }

    [RelayCommand]
    public void ResetInspectorWidth()
    {
        InspectorWidth = 320.0;
    }

    [RelayCommand]
    public void PinToQuickAccess(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            QuickAccessService.Instance.PinFolder(path);
        }
    }

    [RelayCommand]
    public void UnpinFromQuickAccess(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            QuickAccessService.Instance.UnpinFolder(path);
        }
    }

    [RelayCommand]
    public void RemoveQuickAccessItem(QuickAccessItem? item)
    {
        if (item != null && !string.IsNullOrWhiteSpace(item.Path))
        {
            QuickAccessService.Instance.UnpinFolder(item.Path);
        }
    }

    public void ReorderQuickAccess(int fromIndex, int toIndex)
    {
        QuickAccessService.Instance.MoveItem(fromIndex, toIndex);
    }
}
