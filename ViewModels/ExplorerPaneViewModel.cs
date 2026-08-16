using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClankerExplorer.Models;
using ClankerExplorer.Services;

namespace ClankerExplorer.ViewModels;

public partial class ExplorerPaneViewModel : ObservableObject, IDisposable
{
    private readonly Dictionary<ExplorerTabViewModel, PropertyChangedEventHandler> _tabPropertyHandlers = new();
    private readonly Dictionary<ExplorerTabViewModel, Action<FileItem>> _tabScrollHandlers = new();
    private readonly Action _clipboardChangedHandler;
    private readonly Action _quickAccessChangedHandler;
    private readonly Action<AppSettings> _settingsChangedHandler;
    private readonly FolderViewStateService _folderViewStateService;
    private AppSettings _lastObservedSettings;
    private bool _isDisposed;
    private bool _applyingFolderViewState;
    private string? _activeFolderStatePath;
    private static readonly string[] DefaultColumnOrder =
    {
        "Name", "Ext", "Size", "Date Modified", "Date Created", "Date Accessed",
        "Type", "Attributes", "Permissions", "Owner:Group"
    };

    public string PaneId { get; }
    public string PaneLabel { get; }

    [ObservableProperty]
    private ObservableCollection<ExplorerTabViewModel> _tabs = new();

    [ObservableProperty]
    private ExplorerTabViewModel? _selectedTab;

    [ObservableProperty]
    private string _rawAddressInput = string.Empty;

    [ObservableProperty]
    private double _tabWidth = 150.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailsView))]
    [NotifyPropertyChangedFor(nameof(IsThumbnailView))]
    private string _viewMode = "Details";

    public bool IsDetailsView => ViewMode == "Details";
    public bool IsThumbnailView => ViewMode == "Thumbnails";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThumbnailCellWidth))]
    [NotifyPropertyChangedFor(nameof(ThumbnailCellHeight))]
    [NotifyPropertyChangedFor(nameof(ThumbnailImageWidth))]
    [NotifyPropertyChangedFor(nameof(ThumbnailImageHeight))]
    private double _thumbnailSize = 144.0;

    public double ThumbnailCellWidth => ThumbnailSize + 28.0;
    public double ThumbnailCellHeight => ThumbnailSize + 54.0;
    public double ThumbnailImageWidth => ThumbnailSize;
    public double ThumbnailImageHeight => ThumbnailSize;

    [ObservableProperty]
    private IReadOnlyList<ThumbnailRow> _thumbnailRows = Array.Empty<ThumbnailRow>();

    [ObservableProperty]
    private int _thumbnailColumnCount = 1;

    private double _thumbnailViewportWidth;

    public double DetailsHorizontalOffset { get; private set; }
    public double DetailsVerticalOffset { get; private set; }
    public double ThumbnailVerticalOffset { get; private set; }
    public string? DetailsTopItemPath { get; private set; }
    public string? ThumbnailTopItemPath { get; private set; }
    public IReadOnlyList<string> CurrentColumnOrder { get; private set; } = Array.Empty<string>();
    public event Action? FolderViewStateRestored;

    partial void OnViewModeChanged(string value)
    {
        if (value == "Thumbnails")
        {
            RebuildThumbnailRows();
        }
        if (!_applyingFolderViewState) PersistCurrentFolderViewState();
    }

    partial void OnThumbnailSizeChanged(double value)
    {
        if (IsThumbnailView)
        {
            RebuildThumbnailRows();
        }

        if (double.IsFinite(value) && value >= 64 && value <= 320)
        {
            var settings = SettingsService.Instance.CurrentSettings;
            if (settings.ThumbnailSize != value)
            {
                settings.ThumbnailSize = value;
                SettingsService.Instance.SaveSettings(settings);
            }
            if (!_applyingFolderViewState) PersistCurrentFolderViewState();
        }
    }

    public void UpdateThumbnailViewportWidth(double width)
    {
        if (!double.IsFinite(width) || width <= 0) return;
        int columns = Math.Max(1, (int)Math.Floor(Math.Max(1, width - 8) / (ThumbnailCellWidth + 8)));
        if (Math.Abs(_thumbnailViewportWidth - width) < 1 && columns == ThumbnailColumnCount) return;
        _thumbnailViewportWidth = width;
        ThumbnailColumnCount = columns;
        RebuildThumbnailRows();
    }

    public void RebuildThumbnailRows()
    {
        var items = SelectedTab?.FilteredItems;
        if (items == null || items.Count == 0)
        {
            ThumbnailRows = Array.Empty<ThumbnailRow>();
            return;
        }

        int columns = Math.Max(1, ThumbnailColumnCount);
        var rows = new List<ThumbnailRow>((items.Count + columns - 1) / columns);
        for (int start = 0; start < items.Count; start += columns)
        {
            int count = Math.Min(columns, items.Count - start);
            var rowItems = new FileItem[count];
            for (int offset = 0; offset < count; offset++)
            {
                rowItems[offset] = items[start + offset];
            }
            rows.Add(new ThumbnailRow(rowItems));
        }
        ThumbnailRows = rows;
    }

    [RelayCommand]
    public void SetDetailsView()
    {
        ViewMode = "Details";
        var s = SettingsService.Instance.CurrentSettings;
        s.ViewMode = ViewMode;
        SettingsService.Instance.SaveSettings(s);
    }

    [RelayCommand]
    public void SetThumbnailView()
    {
        ViewMode = "Thumbnails";
        var s = SettingsService.Instance.CurrentSettings;
        s.ViewMode = ViewMode;
        SettingsService.Instance.SaveSettings(s);
    }

    [RelayCommand]
    public void ToggleViewMode()
    {
        if (IsDetailsView) SetThumbnailView();
        else SetDetailsView();
    }

    [ObservableProperty]
    private bool _isActive;

    // Reactive Context Menu State
    public bool IsItemSelected => SelectedTab?.SelectedItem != null;
    public bool IsFolderSelected => SelectedTab?.SelectedItem?.IsDirectory == true;
    public bool IsArchiveSelected => SelectedTab?.SelectedItem != null && !SelectedTab.SelectedItem.IsDirectory && ArchiveService.Instance.IsArchive(SelectedTab.SelectedItem.FullPath);
    public bool IsNormalFileSelected => SelectedTab?.SelectedItem != null && !SelectedTab.SelectedItem.IsDirectory && !ArchiveService.Instance.IsArchive(SelectedTab.SelectedItem.FullPath);
    public bool IsTextFileSelected => SelectedTab?.SelectedItem != null && !SelectedTab.SelectedItem.IsDirectory && FileSystemService.Instance.IsTextLikeFile(SelectedTab.SelectedItem.FullPath);

    public bool IsSelectedFolderPinned => SelectedTab?.SelectedItem?.IsDirectory == true && QuickAccessService.Instance.IsPinned(SelectedTab.SelectedItem.FullPath);
    public string PinFolderLabel => IsSelectedFolderPinned ? "⭐ Unpin from Quick Access" : "⭐ Add to Quick Access";

    public string OpenArchiveLabel => "7-Zip: Open Archive";
    public string ExtractHereLabel => "7-Zip: Extract Here";
    public string ExtractToLabel => "7-Zip: Extract To...";
    public string AddArchiveDialogLabel => "7-Zip: Add to archive...";

    public string ExtractSubfolderLabel
    {
        get
        {
            var item = SelectedTab?.SelectedItem;
            if (item == null) return "7-Zip: Extract to folder\\";
            var name = Path.GetFileNameWithoutExtension(item.Name);
            if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
            {
                name = Path.GetFileNameWithoutExtension(name);
            }
            return $"7-Zip: Extract to \"{name}\\\"";
        }
    }

    public string AddZipLabel
    {
        get
        {
            var item = SelectedTab?.SelectedItem;
            if (item == null) return "7-Zip: Add to zip";
            var name = item.IsDirectory ? item.Name : Path.GetFileNameWithoutExtension(item.Name);
            return $"7-Zip: Add to \"{name}.zip\"";
        }
    }

    // Configurable Column Visibility
    [ObservableProperty]
    private bool _showColumnExt = true;

    [ObservableProperty]
    private bool _showColumnSize = true;

    [ObservableProperty]
    private bool _showColumnDateModified = true;

    [ObservableProperty]
    private bool _showColumnDateCreated = true;

    [ObservableProperty]
    private bool _showColumnDateAccessed = false;

    [ObservableProperty]
    private bool _showColumnAttributes = true;

    [ObservableProperty]
    private bool _showColumnItemType = false;

    [ObservableProperty]
    private bool _showColumnPermissions = false;

    [ObservableProperty]
    private bool _showColumnOwnerGroup = false;

    // Smart Column Sizing & Custom Widths
    [ObservableProperty]
    private bool _smartColumnSizing = true;

    [ObservableProperty]
    private double _columnWidthName = 280;

    [ObservableProperty]
    private double _columnWidthExt = 65;

    [ObservableProperty]
    private double _columnWidthSize = 95;

    [ObservableProperty]
    private double _columnWidthDateModified = 150;

    [ObservableProperty]
    private double _columnWidthDateCreated = 150;

    [ObservableProperty]
    private double _columnWidthDateAccessed = 150;

    [ObservableProperty]
    private double _columnWidthItemType = 110;

    [ObservableProperty]
    private double _columnWidthAttributes = 90;

    [ObservableProperty]
    private double _columnWidthPermissions = 110;

    [ObservableProperty]
    private double _columnWidthOwnerGroup = 110;

    public string EditActionLabel => FileSystemService.Instance.GetEditMenuLabel();

    public event Action<FileItem?>? FileSelectedForPreview;
    public event Action<string, string>? RequestCreateItem; // "folder" / "file", parentPath
    public event Action<string>? RequestOpenInOtherPane;
    public event Action<FileItem>? RequestRename;
    public event Action<FileItem?>? RequestProperties;
    public event Action<string>? RequestSetClipboardText;
    public event Action<FileItem>? RequestScrollItemIntoView;

    public Avalonia.Controls.DataGridLength NameColumnWidthDisplay =>
        new Avalonia.Controls.DataGridLength(ColumnWidthName > 0 ? ColumnWidthName : 280, Avalonia.Controls.DataGridLengthUnitType.Pixel);

    public Avalonia.Controls.DataGridLength ExtColumnWidthDisplay =>
        new Avalonia.Controls.DataGridLength(ColumnWidthExt > 0 ? ColumnWidthExt : 75, Avalonia.Controls.DataGridLengthUnitType.Pixel);

    public Avalonia.Controls.DataGridLength SizeColumnWidthDisplay =>
        new Avalonia.Controls.DataGridLength(ColumnWidthSize > 0 ? ColumnWidthSize : 110, Avalonia.Controls.DataGridLengthUnitType.Pixel);

    public Avalonia.Controls.DataGridLength ModifiedColumnWidthDisplay =>
        new Avalonia.Controls.DataGridLength(ColumnWidthDateModified > 0 ? ColumnWidthDateModified : 160, Avalonia.Controls.DataGridLengthUnitType.Pixel);

    public Avalonia.Controls.DataGridLength CreatedColumnWidthDisplay =>
        new Avalonia.Controls.DataGridLength(ColumnWidthDateCreated > 0 ? ColumnWidthDateCreated : 160, Avalonia.Controls.DataGridLengthUnitType.Pixel);

    public Avalonia.Controls.DataGridLength AccessedColumnWidthDisplay =>
        new Avalonia.Controls.DataGridLength(ColumnWidthDateAccessed > 0 ? ColumnWidthDateAccessed : 160, Avalonia.Controls.DataGridLengthUnitType.Pixel);

    public Avalonia.Controls.DataGridLength TypeColumnWidthDisplay =>
        new Avalonia.Controls.DataGridLength(ColumnWidthItemType > 0 ? ColumnWidthItemType : 120, Avalonia.Controls.DataGridLengthUnitType.Pixel);

    public Avalonia.Controls.DataGridLength AttributesColumnWidthDisplay =>
        new Avalonia.Controls.DataGridLength(ColumnWidthAttributes > 0 ? ColumnWidthAttributes : 95, Avalonia.Controls.DataGridLengthUnitType.Pixel);

    public Avalonia.Controls.DataGridLength PermissionsColumnWidthDisplay =>
        new Avalonia.Controls.DataGridLength(ColumnWidthPermissions > 0 ? ColumnWidthPermissions : 115, Avalonia.Controls.DataGridLengthUnitType.Pixel);

    public Avalonia.Controls.DataGridLength OwnerGroupColumnWidthDisplay =>
        new Avalonia.Controls.DataGridLength(ColumnWidthOwnerGroup > 0 ? ColumnWidthOwnerGroup : 115, Avalonia.Controls.DataGridLengthUnitType.Pixel);

    public string NameColumnHeader => BuildColumnHeader("Name", "Name");
    public string ExtColumnHeader => BuildColumnHeader("Ext", "Extension");
    public string SizeColumnHeader => BuildColumnHeader("Size", "Size");
    public string ModifiedColumnHeader => BuildColumnHeader("Date Modified", "Modified");
    public string CreatedColumnHeader => BuildColumnHeader("Date Created", "Created");
    public string AccessedColumnHeader => BuildColumnHeader("Date Accessed", "Accessed");
    public string TypeColumnHeader => BuildColumnHeader("Type", "Type");
    public string AttributesColumnHeader => BuildColumnHeader("Attributes", "Attributes");
    public string PermissionsColumnHeader => BuildColumnHeader("Permissions", "Permissions");
    public string OwnerGroupColumnHeader => BuildColumnHeader("Owner:Group", "OwnerGroup");

    private string BuildColumnHeader(string title, string sortColumn) =>
        SelectedTab?.SortColumn == sortColumn ? $"{title} {(SelectedTab.SortAscending ? "↑" : "↓")}" : title;

    public void NotifySortHeadersChanged()
    {
        OnPropertyChanged(nameof(NameColumnHeader));
        OnPropertyChanged(nameof(ExtColumnHeader));
        OnPropertyChanged(nameof(SizeColumnHeader));
        OnPropertyChanged(nameof(ModifiedColumnHeader));
        OnPropertyChanged(nameof(CreatedColumnHeader));
        OnPropertyChanged(nameof(AccessedColumnHeader));
        OnPropertyChanged(nameof(TypeColumnHeader));
        OnPropertyChanged(nameof(AttributesColumnHeader));
        OnPropertyChanged(nameof(PermissionsColumnHeader));
        OnPropertyChanged(nameof(OwnerGroupColumnHeader));
    }

    public void NotifyColumnWidthsChanged()
    {
        OnPropertyChanged(nameof(NameColumnWidthDisplay));
        OnPropertyChanged(nameof(ExtColumnWidthDisplay));
        OnPropertyChanged(nameof(SizeColumnWidthDisplay));
        OnPropertyChanged(nameof(ModifiedColumnWidthDisplay));
        OnPropertyChanged(nameof(CreatedColumnWidthDisplay));
        OnPropertyChanged(nameof(AccessedColumnWidthDisplay));
        OnPropertyChanged(nameof(TypeColumnWidthDisplay));
        OnPropertyChanged(nameof(AttributesColumnWidthDisplay));
        OnPropertyChanged(nameof(PermissionsColumnWidthDisplay));
        OnPropertyChanged(nameof(OwnerGroupColumnWidthDisplay));
    }

    public ExplorerPaneViewModel(
        string paneId,
        string? initialPath = null,
        string label = "",
        FolderViewStateService? folderViewStateService = null)
    {
        PaneId = paneId;
        PaneLabel = label;
        _folderViewStateService = folderViewStateService ?? FolderViewStateService.Instance;
        _lastObservedSettings = SettingsService.Instance.CurrentSettings.Clone();
        var startPath = initialPath ?? FileSystemService.DefaultRootPath;

        LoadColumnSettings();

        var tab = new ExplorerTabViewModel(startPath);
        Tabs.Add(tab);
        SelectedTab = tab;
        RawAddressInput = startPath;

        WireTabEvents(tab);

        _clipboardChangedHandler = () => OnPropertyChanged(nameof(CanPaste));
        _quickAccessChangedHandler = NotifyContextMenuProperties;
        _settingsChangedHandler = ApplyChangedSettingsToActiveFolder;

        ClipboardFileService.ClipboardChanged += _clipboardChangedHandler;
        QuickAccessService.Instance.QuickAccessChanged += _quickAccessChangedHandler;
        SettingsService.Instance.SettingsChanged += _settingsChangedHandler;
    }

    public bool CanPaste => ClipboardFileService.CanPaste;

    public void LoadColumnSettings()
    {
        var s = SettingsService.Instance.CurrentSettings;
        ShowColumnExt = s.ShowColumnExt;
        ShowColumnSize = s.ShowColumnSize;
        ShowColumnDateModified = s.ShowColumnDateModified;
        ShowColumnDateCreated = s.ShowColumnDateCreated;
        ShowColumnDateAccessed = s.ShowColumnDateAccessed;
        ShowColumnAttributes = s.ShowColumnAttributes;
        ShowColumnItemType = s.ShowColumnItemType;
        ShowColumnPermissions = s.ShowColumnPermissions;
        ShowColumnOwnerGroup = s.ShowColumnOwnerGroup;

        SmartColumnSizing = s.SmartColumnSizing;
        ColumnWidthName = s.ColumnWidthName > 0 ? s.ColumnWidthName : 280;
        ColumnWidthExt = s.ColumnWidthExt > 0 ? s.ColumnWidthExt : 75;
        ColumnWidthSize = s.ColumnWidthSize > 0 ? s.ColumnWidthSize : 110;
        ColumnWidthDateModified = s.ColumnWidthDateModified > 0 ? s.ColumnWidthDateModified : 160;
        ColumnWidthDateCreated = s.ColumnWidthDateCreated > 0 ? s.ColumnWidthDateCreated : 160;
        ColumnWidthDateAccessed = s.ColumnWidthDateAccessed > 0 ? s.ColumnWidthDateAccessed : 160;
        ColumnWidthItemType = s.ColumnWidthItemType > 0 ? s.ColumnWidthItemType : 120;
        ColumnWidthAttributes = s.ColumnWidthAttributes > 0 ? s.ColumnWidthAttributes : 95;
        ColumnWidthPermissions = s.ColumnWidthPermissions > 0 ? s.ColumnWidthPermissions : 115;
        ColumnWidthOwnerGroup = s.ColumnWidthOwnerGroup > 0 ? s.ColumnWidthOwnerGroup : 115;
        TabWidth = double.IsFinite(s.TabWidth) && s.TabWidth >= 80 && s.TabWidth <= 280
            ? s.TabWidth
            : 150.0;
        ViewMode = s.ViewMode == "Thumbnails" ? "Thumbnails" : "Details";
        ThumbnailSize = s.ThumbnailSize >= 64 && s.ThumbnailSize <= 320 ? s.ThumbnailSize : 144.0;
    }

    private void ApplyChangedSettingsToActiveFolder(AppSettings settings)
    {
        var old = _lastObservedSettings;
        bool layoutChanged = false;
        _applyingFolderViewState = true;
        try
        {
            if (settings.ViewMode != old.ViewMode) { ViewMode = settings.ViewMode == "Thumbnails" ? "Thumbnails" : "Details"; layoutChanged = true; }
            if (settings.ThumbnailSize != old.ThumbnailSize) { ThumbnailSize = settings.ThumbnailSize; layoutChanged = true; }
            if (settings.SmartColumnSizing != old.SmartColumnSizing) { SmartColumnSizing = settings.SmartColumnSizing; layoutChanged = true; }
            if (settings.ShowColumnExt != old.ShowColumnExt) { ShowColumnExt = settings.ShowColumnExt; layoutChanged = true; }
            if (settings.ShowColumnSize != old.ShowColumnSize) { ShowColumnSize = settings.ShowColumnSize; layoutChanged = true; }
            if (settings.ShowColumnDateModified != old.ShowColumnDateModified) { ShowColumnDateModified = settings.ShowColumnDateModified; layoutChanged = true; }
            if (settings.ShowColumnDateCreated != old.ShowColumnDateCreated) { ShowColumnDateCreated = settings.ShowColumnDateCreated; layoutChanged = true; }
            if (settings.ShowColumnDateAccessed != old.ShowColumnDateAccessed) { ShowColumnDateAccessed = settings.ShowColumnDateAccessed; layoutChanged = true; }
            if (settings.ShowColumnAttributes != old.ShowColumnAttributes) { ShowColumnAttributes = settings.ShowColumnAttributes; layoutChanged = true; }
            if (settings.ShowColumnItemType != old.ShowColumnItemType) { ShowColumnItemType = settings.ShowColumnItemType; layoutChanged = true; }
            if (settings.ShowColumnPermissions != old.ShowColumnPermissions) { ShowColumnPermissions = settings.ShowColumnPermissions; layoutChanged = true; }
            if (settings.ShowColumnOwnerGroup != old.ShowColumnOwnerGroup) { ShowColumnOwnerGroup = settings.ShowColumnOwnerGroup; layoutChanged = true; }
            if (settings.ColumnWidthName != old.ColumnWidthName) { ColumnWidthName = settings.ColumnWidthName; layoutChanged = true; }
            if (settings.ColumnWidthExt != old.ColumnWidthExt) { ColumnWidthExt = settings.ColumnWidthExt; layoutChanged = true; }
            if (settings.ColumnWidthSize != old.ColumnWidthSize) { ColumnWidthSize = settings.ColumnWidthSize; layoutChanged = true; }
            if (settings.ColumnWidthDateModified != old.ColumnWidthDateModified) { ColumnWidthDateModified = settings.ColumnWidthDateModified; layoutChanged = true; }
            if (settings.ColumnWidthDateCreated != old.ColumnWidthDateCreated) { ColumnWidthDateCreated = settings.ColumnWidthDateCreated; layoutChanged = true; }
            if (settings.ColumnWidthDateAccessed != old.ColumnWidthDateAccessed) { ColumnWidthDateAccessed = settings.ColumnWidthDateAccessed; layoutChanged = true; }
            if (settings.ColumnWidthItemType != old.ColumnWidthItemType) { ColumnWidthItemType = settings.ColumnWidthItemType; layoutChanged = true; }
            if (settings.ColumnWidthAttributes != old.ColumnWidthAttributes) { ColumnWidthAttributes = settings.ColumnWidthAttributes; layoutChanged = true; }
            if (settings.ColumnWidthPermissions != old.ColumnWidthPermissions) { ColumnWidthPermissions = settings.ColumnWidthPermissions; layoutChanged = true; }
            if (settings.ColumnWidthOwnerGroup != old.ColumnWidthOwnerGroup) { ColumnWidthOwnerGroup = settings.ColumnWidthOwnerGroup; layoutChanged = true; }
            if (settings.TabWidth != old.TabWidth) TabWidth = settings.TabWidth;
        }
        finally
        {
            _applyingFolderViewState = false;
            _lastObservedSettings = settings.Clone();
        }

        if (layoutChanged)
        {
            NotifyColumnWidthsChanged();
            RefreshMetadataRequirementsIfChanged();
            PersistCurrentFolderViewState();
        }
    }

    public void PersistCurrentFolderViewState() => PersistCurrentFolderViewState(_activeFolderStatePath, SelectedTab);

    private void PersistCurrentFolderViewState(string? path, ExplorerTabViewModel? tab)
    {
        if (_applyingFolderViewState || string.IsNullOrWhiteSpace(path) || tab == null) return;
        _folderViewStateService.Set(path, new FolderViewState
        {
            ViewMode = ViewMode,
            ThumbnailSize = ThumbnailSize,
            SortColumn = tab.SortColumn,
            SortAscending = tab.SortAscending,
            SmartColumnSizing = SmartColumnSizing,
            ShowColumnExt = ShowColumnExt,
            ShowColumnSize = ShowColumnSize,
            ShowColumnDateModified = ShowColumnDateModified,
            ShowColumnDateCreated = ShowColumnDateCreated,
            ShowColumnDateAccessed = ShowColumnDateAccessed,
            ShowColumnAttributes = ShowColumnAttributes,
            ShowColumnItemType = ShowColumnItemType,
            ShowColumnPermissions = ShowColumnPermissions,
            ShowColumnOwnerGroup = ShowColumnOwnerGroup,
            ColumnWidthName = ColumnWidthName,
            ColumnWidthExt = ColumnWidthExt,
            ColumnWidthSize = ColumnWidthSize,
            ColumnWidthDateModified = ColumnWidthDateModified,
            ColumnWidthDateCreated = ColumnWidthDateCreated,
            ColumnWidthDateAccessed = ColumnWidthDateAccessed,
            ColumnWidthItemType = ColumnWidthItemType,
            ColumnWidthAttributes = ColumnWidthAttributes,
            ColumnWidthPermissions = ColumnWidthPermissions,
            ColumnWidthOwnerGroup = ColumnWidthOwnerGroup,
            ColumnOrder = new List<string>(CurrentColumnOrder),
            DetailsHorizontalOffset = DetailsHorizontalOffset,
            DetailsVerticalOffset = DetailsVerticalOffset,
            ThumbnailVerticalOffset = ThumbnailVerticalOffset,
            DetailsTopItemPath = DetailsTopItemPath,
            ThumbnailTopItemPath = ThumbnailTopItemPath
        });
    }

    private void ApplyFolderViewState(ExplorerTabViewModel tab)
    {
        _activeFolderStatePath = tab.CurrentPath;
        FolderViewState state = _folderViewStateService.TryGet(tab.CurrentPath, out var saved)
            ? saved
            : CreateDefaultFolderViewState();

        _applyingFolderViewState = true;
        try
        {
            ViewMode = state.ViewMode == "Thumbnails" ? "Thumbnails" : "Details";
            ThumbnailSize = double.IsFinite(state.ThumbnailSize) && state.ThumbnailSize is >= 64 and <= 320
                ? state.ThumbnailSize : 144;
            tab.SortColumn = NormalizeSortColumn(state.SortColumn);
            tab.SortAscending = state.SortAscending;
            SmartColumnSizing = state.SmartColumnSizing;
            ShowColumnExt = state.ShowColumnExt;
            ShowColumnSize = state.ShowColumnSize;
            ShowColumnDateModified = state.ShowColumnDateModified;
            ShowColumnDateCreated = state.ShowColumnDateCreated;
            ShowColumnDateAccessed = state.ShowColumnDateAccessed;
            ShowColumnAttributes = state.ShowColumnAttributes;
            ShowColumnItemType = state.ShowColumnItemType;
            ShowColumnPermissions = state.ShowColumnPermissions;
            ShowColumnOwnerGroup = state.ShowColumnOwnerGroup;
            ColumnWidthName = PositiveOr(state.ColumnWidthName, 280);
            ColumnWidthExt = PositiveOr(state.ColumnWidthExt, 65);
            ColumnWidthSize = PositiveOr(state.ColumnWidthSize, 95);
            ColumnWidthDateModified = PositiveOr(state.ColumnWidthDateModified, 150);
            ColumnWidthDateCreated = PositiveOr(state.ColumnWidthDateCreated, 150);
            ColumnWidthDateAccessed = PositiveOr(state.ColumnWidthDateAccessed, 150);
            ColumnWidthItemType = PositiveOr(state.ColumnWidthItemType, 110);
            ColumnWidthAttributes = PositiveOr(state.ColumnWidthAttributes, 90);
            ColumnWidthPermissions = PositiveOr(state.ColumnWidthPermissions, 110);
            ColumnWidthOwnerGroup = PositiveOr(state.ColumnWidthOwnerGroup, 110);
            CurrentColumnOrder = state.ColumnOrder?.ToArray() ?? Array.Empty<string>();
            DetailsHorizontalOffset = NonNegative(state.DetailsHorizontalOffset);
            DetailsVerticalOffset = NonNegative(state.DetailsVerticalOffset);
            ThumbnailVerticalOffset = NonNegative(state.ThumbnailVerticalOffset);
            DetailsTopItemPath = state.DetailsTopItemPath;
            ThumbnailTopItemPath = state.ThumbnailTopItemPath;
        }
        finally
        {
            _applyingFolderViewState = false;
        }

        NotifyColumnWidthsChanged();
        NotifySortHeadersChanged();
        RebuildThumbnailRows();
        bool metadataChanged = tab.SetDirectoryReadOptions(new DirectoryReadOptions(
            state.ShowColumnDateCreated,
            state.ShowColumnDateAccessed,
            state.ShowColumnPermissions,
            state.ShowColumnOwnerGroup));
        if (metadataChanged) _ = tab.RefreshAsync();
        else _ = tab.ApplyFilterAsync();
        FolderViewStateRestored?.Invoke();
    }

    private FolderViewState CreateDefaultFolderViewState()
    {
        var s = SettingsService.Instance.CurrentSettings;
        return new FolderViewState
        {
            ViewMode = s.ViewMode,
            ThumbnailSize = s.ThumbnailSize,
            SmartColumnSizing = s.SmartColumnSizing,
            ShowColumnExt = s.ShowColumnExt,
            ShowColumnSize = s.ShowColumnSize,
            ShowColumnDateModified = s.ShowColumnDateModified,
            ShowColumnDateCreated = s.ShowColumnDateCreated,
            ShowColumnDateAccessed = s.ShowColumnDateAccessed,
            ShowColumnAttributes = s.ShowColumnAttributes,
            ShowColumnItemType = s.ShowColumnItemType,
            ShowColumnPermissions = s.ShowColumnPermissions,
            ShowColumnOwnerGroup = s.ShowColumnOwnerGroup,
            ColumnWidthName = s.ColumnWidthName,
            ColumnWidthExt = s.ColumnWidthExt,
            ColumnWidthSize = s.ColumnWidthSize,
            ColumnWidthDateModified = s.ColumnWidthDateModified,
            ColumnWidthDateCreated = s.ColumnWidthDateCreated,
            ColumnWidthDateAccessed = s.ColumnWidthDateAccessed,
            ColumnWidthItemType = s.ColumnWidthItemType,
            ColumnWidthAttributes = s.ColumnWidthAttributes,
            ColumnWidthPermissions = s.ColumnWidthPermissions,
            ColumnWidthOwnerGroup = s.ColumnWidthOwnerGroup
        };
    }

    private static double PositiveOr(double value, double fallback) =>
        double.IsFinite(value) && value > 0 ? value : fallback;

    private static double NonNegative(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;

    private static string NormalizeSortColumn(string? value) => value switch
    {
        "Name" or "Extension" or "Size" or "Modified" or "Created" or "Accessed" or
        "Type" or "Attributes" or "Permissions" or "OwnerGroup" => value,
        _ => "Name"
    };

    private void RefreshMetadataRequirementsIfChanged()
    {
        if (SelectedTab == null) return;
        if (SelectedTab.SetDirectoryReadOptions(new DirectoryReadOptions(
                ShowColumnDateCreated,
                ShowColumnDateAccessed,
                ShowColumnPermissions,
                ShowColumnOwnerGroup)))
        {
            _ = SelectedTab.RefreshAsync();
        }
    }

    public void UpdateFolderScrollState(
        double detailsHorizontal,
        double detailsVertical,
        double thumbnailVertical,
        bool persist = true)
    {
        DetailsHorizontalOffset = Math.Max(0, detailsHorizontal);
        DetailsVerticalOffset = Math.Max(0, detailsVertical);
        ThumbnailVerticalOffset = Math.Max(0, thumbnailVertical);
        if (persist) PersistCurrentFolderViewState();
    }

    public void SetCurrentColumnOrder(IEnumerable<string> headers)
    {
        CurrentColumnOrder = headers.Where(header => !string.IsNullOrWhiteSpace(header)).Distinct().ToArray();
        PersistCurrentFolderViewState();
    }

    public void UpdateFolderViewportAnchors(string? detailsTopItemPath, string? thumbnailTopItemPath)
    {
        DetailsTopItemPath = detailsTopItemPath;
        ThumbnailTopItemPath = thumbnailTopItemPath;
    }

    [RelayCommand]
    public void ToggleSmartSizing()
    {
        SmartColumnSizing = !SmartColumnSizing;
        var s = SettingsService.Instance.CurrentSettings;
        s.SmartColumnSizing = SmartColumnSizing;
        SettingsService.Instance.SaveSettings(s);
        NotifyColumnWidthsChanged();
        PersistCurrentFolderViewState();
    }

    [RelayCommand]
    public void ResetColumnWidths()
    {
        SmartColumnSizing = true;
        ColumnWidthName = 280;
        ColumnWidthExt = 75;
        ColumnWidthSize = 110;
        ColumnWidthDateModified = 160;
        ColumnWidthDateCreated = 160;
        ColumnWidthDateAccessed = 160;
        ColumnWidthItemType = 120;
        ColumnWidthAttributes = 95;
        ColumnWidthPermissions = 115;
        ColumnWidthOwnerGroup = 115;

        var s = SettingsService.Instance.CurrentSettings;
        s.SmartColumnSizing = true;
        s.ColumnWidthName = 280;
        s.ColumnWidthExt = 75;
        s.ColumnWidthSize = 110;
        s.ColumnWidthDateModified = 160;
        s.ColumnWidthDateCreated = 160;
        s.ColumnWidthDateAccessed = 160;
        s.ColumnWidthItemType = 120;
        s.ColumnWidthAttributes = 95;
        s.ColumnWidthPermissions = 115;
        s.ColumnWidthOwnerGroup = 115;
        SettingsService.Instance.SaveSettings(s);
        NotifyColumnWidthsChanged();
        CurrentColumnOrder = DefaultColumnOrder;
        PersistCurrentFolderViewState();
        FolderViewStateRestored?.Invoke();
    }

    [RelayCommand]
    public void ToggleColumn(string col)
    {
        var s = SettingsService.Instance.CurrentSettings;
        switch (col.ToLowerInvariant())
        {
            case "ext":
                ShowColumnExt = !ShowColumnExt;
                s.ShowColumnExt = ShowColumnExt;
                break;
            case "size":
                ShowColumnSize = !ShowColumnSize;
                s.ShowColumnSize = ShowColumnSize;
                break;
            case "datemodified":
            case "modified":
                ShowColumnDateModified = !ShowColumnDateModified;
                s.ShowColumnDateModified = ShowColumnDateModified;
                break;
            case "datecreated":
            case "created":
                ShowColumnDateCreated = !ShowColumnDateCreated;
                s.ShowColumnDateCreated = ShowColumnDateCreated;
                break;
            case "dateaccessed":
            case "accessed":
                ShowColumnDateAccessed = !ShowColumnDateAccessed;
                s.ShowColumnDateAccessed = ShowColumnDateAccessed;
                break;
            case "attributes":
            case "attr":
                ShowColumnAttributes = !ShowColumnAttributes;
                s.ShowColumnAttributes = ShowColumnAttributes;
                break;
            case "itemtype":
            case "type":
                ShowColumnItemType = !ShowColumnItemType;
                s.ShowColumnItemType = ShowColumnItemType;
                break;
            case "permissions":
            case "perm":
                ShowColumnPermissions = !ShowColumnPermissions;
                s.ShowColumnPermissions = ShowColumnPermissions;
                break;
            case "ownergroup":
            case "owner":
                ShowColumnOwnerGroup = !ShowColumnOwnerGroup;
                s.ShowColumnOwnerGroup = ShowColumnOwnerGroup;
                break;
        }
        SettingsService.Instance.SaveSettings(s);
        PersistCurrentFolderViewState();
        RefreshMetadataRequirementsIfChanged();
    }

    public bool IsSuppressingPreview { get; set; }

    public void TriggerPreviewForSelectedItem()
    {
        if (SelectedTab != null)
        {
            FileSelectedForPreview?.Invoke(SelectedTab.SelectedItem);
        }
    }

    public void WireTabEvents(ExplorerTabViewModel tab)
    {
        if (_tabPropertyHandlers.ContainsKey(tab)) return;

        PropertyChangedEventHandler handler = (s, e) =>
        {
            if (e.PropertyName == nameof(ExplorerTabViewModel.CurrentPath) && tab == SelectedTab)
            {
                PersistCurrentFolderViewState(_activeFolderStatePath, tab);
                RawAddressInput = tab.CurrentPath;
                ApplyFolderViewState(tab);
            }
            else if (e.PropertyName == nameof(ExplorerTabViewModel.SelectedItem) && tab == SelectedTab)
            {
                NotifyContextMenuProperties();
                if (!IsSuppressingPreview)
                {
                    FileSelectedForPreview?.Invoke(tab.SelectedItem);
                }
            }
            else if (e.PropertyName == nameof(ExplorerTabViewModel.FilteredItems) && tab == SelectedTab)
            {
                RebuildThumbnailRows();
                FolderViewStateRestored?.Invoke();
            }
        };

        _tabPropertyHandlers[tab] = handler;
        tab.PropertyChanged += handler;

        Action<FileItem> scrollHandler = item =>
        {
            if (tab == SelectedTab)
                RequestScrollItemIntoView?.Invoke(item);
        };
        _tabScrollHandlers[tab] = scrollHandler;
        tab.ScrollIntoViewRequested += scrollHandler;
    }

    public void UnwireTabEvents(ExplorerTabViewModel tab)
    {
        if (_tabPropertyHandlers.Remove(tab, out var handler))
        {
            tab.PropertyChanged -= handler;
        }
        if (_tabScrollHandlers.Remove(tab, out var scrollHandler))
        {
            tab.ScrollIntoViewRequested -= scrollHandler;
        }
    }

    public void NotifyContextMenuProperties()
    {
        OnPropertyChanged(nameof(IsItemSelected));
        OnPropertyChanged(nameof(IsFolderSelected));
        OnPropertyChanged(nameof(IsArchiveSelected));
        OnPropertyChanged(nameof(IsNormalFileSelected));
        OnPropertyChanged(nameof(IsTextFileSelected));
        OnPropertyChanged(nameof(IsSelectedFolderPinned));
        OnPropertyChanged(nameof(PinFolderLabel));
        OnPropertyChanged(nameof(ExtractSubfolderLabel));
        OnPropertyChanged(nameof(AddZipLabel));
        OnPropertyChanged(nameof(EditActionLabel));
    }

    partial void OnSelectedTabChanged(ExplorerTabViewModel? value)
    {
        foreach (var t in Tabs)
        {
            t.IsSelected = (t == value);
        }

        if (value != null)
        {
            ApplyFolderViewState(value);
            value.IsSelected = true;
            value.LastActiveTime = DateTime.Now;
            RawAddressInput = value.CurrentPath;
            NotifyContextMenuProperties();
            FileSelectedForPreview?.Invoke(value.SelectedItem);
        }
    }

    partial void OnSelectedTabChanging(ExplorerTabViewModel? oldValue, ExplorerTabViewModel? newValue)
    {
        PersistCurrentFolderViewState(_activeFolderStatePath, oldValue);
    }

    [RelayCommand]
    public void AddNewTab(string? path = null)
    {
        var settings = SettingsService.Instance.CurrentSettings;
        if (settings.MaxTabsAllowed > 0 && Tabs.Count >= settings.MaxTabsAllowed)
        {
            return;
        }

        var targetPath = path ?? SelectedTab?.CurrentPath ?? FileSystemService.DefaultRootPath;
        var newTab = new ExplorerTabViewModel(targetPath);
        Tabs.Add(newTab);
        SelectedTab = newTab;
        WireTabEvents(newTab);
    }

    [RelayCommand]
    public void CloseTab(ExplorerTabViewModel? tab)
    {
        var target = tab ?? SelectedTab;
        if (target == null || Tabs.Count <= 1 || target.IsPinned) return;

        int idx = Tabs.IndexOf(target);
        Tabs.Remove(target);
        UnwireTabEvents(target);
        target.Dispose();

        if (SelectedTab == target)
        {
            int nextIdx = Math.Min(idx, Tabs.Count - 1);
            SelectedTab = Tabs[nextIdx];
        }
    }

    [RelayCommand]
    public void TogglePinTab(ExplorerTabViewModel? tab)
    {
        var target = tab ?? SelectedTab;
        if (target != null)
        {
            target.IsPinned = !target.IsPinned;
        }
    }

    [RelayCommand]
    public void DuplicateTab(ExplorerTabViewModel? tab)
    {
        var target = tab ?? SelectedTab;
        var settings = SettingsService.Instance.CurrentSettings;
        if (target != null &&
            (settings.MaxTabsAllowed <= 0 || Tabs.Count < settings.MaxTabsAllowed))
        {
            var clone = target.CloneTab();
            Tabs.Add(clone);
            SelectedTab = clone;
            WireTabEvents(clone);
        }
    }

    [RelayCommand]
    public void CloseOtherTabs(ExplorerTabViewModel? tab)
    {
        var target = tab ?? SelectedTab;
        if (target == null) return;

        var toRemove = Tabs.Where(t => t != target && !t.IsPinned).ToList();
        foreach (var t in toRemove)
        {
            Tabs.Remove(t);
            UnwireTabEvents(t);
            t.Dispose();
        }
        SelectedTab = target;
    }

    [RelayCommand]
    public void GoBack() => SelectedTab?.GoBack();

    [RelayCommand]
    public void GoForward() => SelectedTab?.GoForward();

    [RelayCommand]
    public void GoUp() => SelectedTab?.GoUp();

    [RelayCommand]
    public void Refresh() => _ = RefreshAsync();

    public async Task RefreshAsync()
    {
        if (SelectedTab != null)
        {
            await SelectedTab.RefreshAsync();
        }
    }

    [RelayCommand]
    public void SubmitAddress()
    {
        if (!string.IsNullOrWhiteSpace(RawAddressInput) && SelectedTab != null)
        {
            SelectedTab.NavigateTo(RawAddressInput);
        }
    }

    [RelayCommand]
    public void TriggerNewFolder()
    {
        if (SelectedTab != null)
        {
            RequestCreateItem?.Invoke("folder", SelectedTab.CurrentPath);
        }
    }

    [RelayCommand]
    public void TriggerNewFile()
    {
        if (SelectedTab != null)
        {
            RequestCreateItem?.Invoke("file", SelectedTab.CurrentPath);
        }
    }

    [RelayCommand]
    public void OpenItem(FileItem? item = null)
    {
        var target = item ?? SelectedTab?.SelectedItem;
        if (target == null) return;

        if (target.IsDirectory)
        {
            SelectedTab?.NavigateTo(target.FullPath);
        }
        else if (ArchiveService.Instance.IsArchive(target.FullPath))
        {
            ArchiveService.Instance.OpenArchive(target.FullPath);
        }
        else
        {
            FileSystemService.Instance.OpenItem(target.FullPath);
        }
    }

    [RelayCommand]
    public void EditItem()
    {
        var target = SelectedTab?.SelectedItem;
        if (target != null && !target.IsDirectory)
        {
            FileSystemService.Instance.EditFile(target.FullPath);
        }
    }

    [RelayCommand]
    public void OpenWith(FileItem? item = null)
    {
        var target = item ?? SelectedTab?.SelectedItem;
        if (target != null && !target.IsDirectory)
        {
            FileSystemService.Instance.OpenWith(target.FullPath);
        }
    }

    [RelayCommand]
    public void CopyFiles()
    {
        var target = SelectedTab?.SelectedItem;
        if (target != null)
        {
            ClipboardFileService.Copy(new[] { target.FullPath });
            RequestSetClipboardText?.Invoke(target.FullPath);
        }
    }

    [RelayCommand]
    public void CutFiles()
    {
        var target = SelectedTab?.SelectedItem;
        if (target != null)
        {
            ClipboardFileService.Cut(new[] { target.FullPath });
            RequestSetClipboardText?.Invoke(target.FullPath);
        }
    }

    [RelayCommand]
    public async Task PasteFilesAsync()
    {
        if (SelectedTab != null)
        {
            var (successCount, failedPaths, createdPaths) = await ClipboardFileService.PasteAsync(SelectedTab.CurrentPath);
            if (createdPaths != null && createdPaths.Count > 0)
            {
                SelectedTab.PendingSelectPaths = createdPaths;
            }
            await SelectedTab.RefreshAsync();
            NotifyContextMenuProperties();
        }
    }

    [RelayCommand]
    public void CopyPath()
    {
        var target = SelectedTab?.SelectedItem;
        if (target != null)
        {
            RequestSetClipboardText?.Invoke(target.FullPath);
        }
    }

    [RelayCommand]
    public void CopyCurrentPath()
    {
        if (SelectedTab != null)
        {
            RequestSetClipboardText?.Invoke(SelectedTab.CurrentPath);
        }
    }

    [RelayCommand]
    public void OpenArchive()
    {
        var target = SelectedTab?.SelectedItem;
        if (target != null)
        {
            ArchiveService.Instance.OpenArchive(target.FullPath);
        }
    }

    [RelayCommand]
    public async Task ExtractHereAsync()
    {
        var target = SelectedTab?.SelectedItem;
        if (target != null && SelectedTab != null)
        {
            await ArchiveService.Instance.ExtractHereAsync(target.FullPath);
            await SelectedTab.RefreshAsync();
        }
    }

    [RelayCommand]
    public async Task ExtractToSubfolderAsync()
    {
        var target = SelectedTab?.SelectedItem;
        if (target != null && SelectedTab != null)
        {
            await ArchiveService.Instance.ExtractToSubfolderAsync(target.FullPath);
            await SelectedTab.RefreshAsync();
        }
    }

    [RelayCommand]
    public async Task ExtractToCustomAsync()
    {
        var target = SelectedTab?.SelectedItem;
        if (target != null && SelectedTab != null)
        {
            ArchiveService.Instance.OpenExtractDialog(target.FullPath);
            await SelectedTab.RefreshAsync();
        }
    }

    [RelayCommand]
    public void AddToZip()
    {
        var target = SelectedTab?.SelectedItem;
        if (target != null)
        {
            ArchiveService.Instance.CreateZip(target.FullPath);
            Refresh();
        }
    }

    [RelayCommand]
    public void AddToArchiveDialog()
    {
        var target = SelectedTab?.SelectedItem;
        if (target != null)
        {
            ArchiveService.Instance.OpenAddToArchiveDialog(target.FullPath);
            Refresh();
        }
    }

    [RelayCommand]
    public void OpenFolderInNewTab()
    {
        var target = SelectedTab?.SelectedItem;
        if (target != null && target.IsDirectory)
        {
            AddNewTab(target.FullPath);
        }
    }

    [RelayCommand]
    public void OpenFolderInOtherPane()
    {
        var target = SelectedTab?.SelectedItem;
        if (target != null && target.IsDirectory)
        {
            RequestOpenInOtherPane?.Invoke(target.FullPath);
        }
    }

    [RelayCommand]
    public void PinFolder()
    {
        var target = SelectedTab?.SelectedItem;
        if (target != null && target.IsDirectory)
        {
            if (QuickAccessService.Instance.IsPinned(target.FullPath))
            {
                QuickAccessService.Instance.UnpinFolder(target.FullPath);
            }
            else
            {
                QuickAccessService.Instance.PinFolder(target.FullPath, target.Name);
            }
            NotifyContextMenuProperties();
        }
    }

    [RelayCommand]
    public void TriggerRename()
    {
        var target = SelectedTab?.SelectedItem;
        if (target != null)
        {
            RequestRename?.Invoke(target);
        }
    }

    [RelayCommand]
    public void TriggerProperties()
    {
        var target = SelectedTab?.SelectedItem;
        RequestProperties?.Invoke(target);
    }

    [RelayCommand]
    public void OpenTerminal()
    {
        var path = SelectedTab?.SelectedItem?.FullPath ?? SelectedTab?.CurrentPath ?? @"C:\";
        if (File.Exists(path)) path = Path.GetDirectoryName(path) ?? path;
        FileSystemService.Instance.OpenTerminal(path, false);
    }

    [RelayCommand]
    public void OpenTerminalAdmin()
    {
        var path = SelectedTab?.SelectedItem?.FullPath ?? SelectedTab?.CurrentPath ?? @"C:\";
        if (File.Exists(path)) path = Path.GetDirectoryName(path) ?? path;
        FileSystemService.Instance.OpenTerminal(path, true);
    }

    [RelayCommand]
    public void OpenCmd()
    {
        var path = SelectedTab?.SelectedItem?.FullPath ?? SelectedTab?.CurrentPath ?? @"C:\";
        if (File.Exists(path)) path = Path.GetDirectoryName(path) ?? path;
        FileSystemService.Instance.OpenCmd(path, false);
    }

    [RelayCommand]
    public void OpenCmdAdmin()
    {
        var path = SelectedTab?.SelectedItem?.FullPath ?? SelectedTab?.CurrentPath ?? @"C:\";
        if (File.Exists(path)) path = Path.GetDirectoryName(path) ?? path;
        FileSystemService.Instance.OpenCmd(path, true);
    }

    [RelayCommand]
    public void OpenVSCode()
    {
        var path = SelectedTab?.SelectedItem?.FullPath ?? SelectedTab?.CurrentPath ?? @"C:\";
        FileSystemService.Instance.OpenEditor(path);
    }

    public event Action<FileItem, bool>? RequestDeleteWithConfirmation;

    [RelayCommand]
    public void DeleteSelected(bool permanent = false)
    {
        var item = SelectedTab?.SelectedItem;
        if (item != null)
        {
            RequestDeleteWithConfirmation?.Invoke(item, permanent);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        PersistCurrentFolderViewState();
        _folderViewStateService.Flush();
        _isDisposed = true;

        ClipboardFileService.ClipboardChanged -= _clipboardChangedHandler;
        QuickAccessService.Instance.QuickAccessChanged -= _quickAccessChangedHandler;
        SettingsService.Instance.SettingsChanged -= _settingsChangedHandler;

        foreach (var tab in Tabs.ToList())
        {
            UnwireTabEvents(tab);
            tab.Dispose();
        }
        Tabs.Clear();
        SelectedTab = null;
    }
}
