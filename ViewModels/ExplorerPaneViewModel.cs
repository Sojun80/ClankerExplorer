using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClankerExplorer.Models;
using ClankerExplorer.Services;

namespace ClankerExplorer.ViewModels;

public partial class ExplorerPaneViewModel : ObservableObject
{
    public string PaneId { get; }
    public string PaneLabel { get; }

    [ObservableProperty]
    private ObservableCollection<ExplorerTabViewModel> _tabs = new();

    [ObservableProperty]
    private ExplorerTabViewModel? _selectedTab;

    [ObservableProperty]
    private string _rawAddressInput = string.Empty;

    [ObservableProperty]
    private bool _isActive;

    // Reactive Context Menu State
    public bool IsItemSelected => SelectedTab?.SelectedItem != null;
    public bool IsFolderSelected => SelectedTab?.SelectedItem?.IsDirectory == true;
    public bool IsArchiveSelected => SelectedTab?.SelectedItem != null && !SelectedTab.SelectedItem.IsDirectory && ArchiveService.Instance.IsArchive(SelectedTab.SelectedItem.FullPath);
    public bool IsNormalFileSelected => SelectedTab?.SelectedItem != null && !SelectedTab.SelectedItem.IsDirectory && !ArchiveService.Instance.IsArchive(SelectedTab.SelectedItem.FullPath);
    public bool IsTextFileSelected => SelectedTab?.SelectedItem != null && !SelectedTab.SelectedItem.IsDirectory && FileSystemService.Instance.IsTextLikeFile(SelectedTab.SelectedItem.FullPath);

    public bool IsSelectedFolderPinned => SelectedTab?.SelectedItem?.IsDirectory == true && QuickAccessService.Instance.IsPinned(SelectedTab.SelectedItem.FullPath);
    public string PinFolderLabel => IsSelectedFolderPinned ? "Unpin from Quick Access" : "📌 Pin to Quick Access";

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
    public event Action<string>? RequestPinFolder;
    public event Action<FileItem>? RequestRename;
    public event Action<FileItem?>? RequestProperties;
    public event Action<string>? RequestSetClipboardText;

    public Avalonia.Controls.DataGridLength NameColumnWidthDisplay => SmartColumnSizing
        ? new Avalonia.Controls.DataGridLength(3, Avalonia.Controls.DataGridLengthUnitType.Star)
        : new Avalonia.Controls.DataGridLength(ColumnWidthName, Avalonia.Controls.DataGridLengthUnitType.Pixel);

    public Avalonia.Controls.DataGridLength ExtColumnWidthDisplay => SmartColumnSizing
        ? new Avalonia.Controls.DataGridLength(65, Avalonia.Controls.DataGridLengthUnitType.Pixel)
        : new Avalonia.Controls.DataGridLength(ColumnWidthExt, Avalonia.Controls.DataGridLengthUnitType.Pixel);

    public Avalonia.Controls.DataGridLength SizeColumnWidthDisplay => SmartColumnSizing
        ? new Avalonia.Controls.DataGridLength(95, Avalonia.Controls.DataGridLengthUnitType.Pixel)
        : new Avalonia.Controls.DataGridLength(ColumnWidthSize, Avalonia.Controls.DataGridLengthUnitType.Pixel);

    public Avalonia.Controls.DataGridLength ModifiedColumnWidthDisplay => SmartColumnSizing
        ? new Avalonia.Controls.DataGridLength(150, Avalonia.Controls.DataGridLengthUnitType.Pixel)
        : new Avalonia.Controls.DataGridLength(ColumnWidthDateModified, Avalonia.Controls.DataGridLengthUnitType.Pixel);

    public Avalonia.Controls.DataGridLength CreatedColumnWidthDisplay => SmartColumnSizing
        ? new Avalonia.Controls.DataGridLength(150, Avalonia.Controls.DataGridLengthUnitType.Pixel)
        : new Avalonia.Controls.DataGridLength(ColumnWidthDateCreated, Avalonia.Controls.DataGridLengthUnitType.Pixel);

    public Avalonia.Controls.DataGridLength AccessedColumnWidthDisplay => SmartColumnSizing
        ? new Avalonia.Controls.DataGridLength(150, Avalonia.Controls.DataGridLengthUnitType.Pixel)
        : new Avalonia.Controls.DataGridLength(ColumnWidthDateAccessed, Avalonia.Controls.DataGridLengthUnitType.Pixel);

    public Avalonia.Controls.DataGridLength TypeColumnWidthDisplay => SmartColumnSizing
        ? new Avalonia.Controls.DataGridLength(110, Avalonia.Controls.DataGridLengthUnitType.Pixel)
        : new Avalonia.Controls.DataGridLength(ColumnWidthItemType, Avalonia.Controls.DataGridLengthUnitType.Pixel);

    public Avalonia.Controls.DataGridLength AttributesColumnWidthDisplay => SmartColumnSizing
        ? new Avalonia.Controls.DataGridLength(90, Avalonia.Controls.DataGridLengthUnitType.Pixel)
        : new Avalonia.Controls.DataGridLength(ColumnWidthAttributes, Avalonia.Controls.DataGridLengthUnitType.Pixel);

    public Avalonia.Controls.DataGridLength PermissionsColumnWidthDisplay => SmartColumnSizing
        ? new Avalonia.Controls.DataGridLength(110, Avalonia.Controls.DataGridLengthUnitType.Pixel)
        : new Avalonia.Controls.DataGridLength(ColumnWidthPermissions, Avalonia.Controls.DataGridLengthUnitType.Pixel);

    public Avalonia.Controls.DataGridLength OwnerGroupColumnWidthDisplay => SmartColumnSizing
        ? new Avalonia.Controls.DataGridLength(110, Avalonia.Controls.DataGridLengthUnitType.Pixel)
        : new Avalonia.Controls.DataGridLength(ColumnWidthOwnerGroup, Avalonia.Controls.DataGridLengthUnitType.Pixel);

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

    public ExplorerPaneViewModel(string paneId, string? initialPath = null, string label = "")
    {
        PaneId = paneId;
        PaneLabel = label;
        var startPath = initialPath ?? FileSystemService.DefaultRootPath;

        LoadColumnSettings();

        var tab = new ExplorerTabViewModel(startPath);
        Tabs.Add(tab);
        SelectedTab = tab;
        RawAddressInput = startPath;

        WireTabEvents(tab);

        ClipboardFileService.ClipboardChanged += () => OnPropertyChanged(nameof(CanPaste));
        QuickAccessService.Instance.QuickAccessChanged += () => NotifyContextMenuProperties();

        SettingsService.Instance.SettingsChanged += s =>
        {
            LoadColumnSettings();
            NotifyColumnWidthsChanged();
        };
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
        ColumnWidthExt = s.ColumnWidthExt > 0 ? s.ColumnWidthExt : 65;
        ColumnWidthSize = s.ColumnWidthSize > 0 ? s.ColumnWidthSize : 95;
        ColumnWidthDateModified = s.ColumnWidthDateModified > 0 ? s.ColumnWidthDateModified : 150;
        ColumnWidthDateCreated = s.ColumnWidthDateCreated > 0 ? s.ColumnWidthDateCreated : 150;
        ColumnWidthDateAccessed = s.ColumnWidthDateAccessed > 0 ? s.ColumnWidthDateAccessed : 150;
        ColumnWidthItemType = s.ColumnWidthItemType > 0 ? s.ColumnWidthItemType : 110;
        ColumnWidthAttributes = s.ColumnWidthAttributes > 0 ? s.ColumnWidthAttributes : 90;
        ColumnWidthPermissions = s.ColumnWidthPermissions > 0 ? s.ColumnWidthPermissions : 110;
        ColumnWidthOwnerGroup = s.ColumnWidthOwnerGroup > 0 ? s.ColumnWidthOwnerGroup : 110;
    }

    [RelayCommand]
    public void ToggleSmartSizing()
    {
        SmartColumnSizing = !SmartColumnSizing;
        var s = SettingsService.Instance.CurrentSettings;
        s.SmartColumnSizing = SmartColumnSizing;
        SettingsService.Instance.SaveSettings(s);
        NotifyColumnWidthsChanged();
    }

    [RelayCommand]
    public void ResetColumnWidths()
    {
        SmartColumnSizing = true;
        ColumnWidthName = 280;
        ColumnWidthExt = 65;
        ColumnWidthSize = 95;
        ColumnWidthDateModified = 150;
        ColumnWidthDateCreated = 150;
        ColumnWidthDateAccessed = 150;
        ColumnWidthItemType = 110;
        ColumnWidthAttributes = 90;
        ColumnWidthPermissions = 110;
        ColumnWidthOwnerGroup = 110;

        var s = SettingsService.Instance.CurrentSettings;
        s.SmartColumnSizing = true;
        s.ColumnWidthName = 280;
        s.ColumnWidthExt = 65;
        s.ColumnWidthSize = 95;
        s.ColumnWidthDateModified = 150;
        s.ColumnWidthDateCreated = 150;
        s.ColumnWidthDateAccessed = 150;
        s.ColumnWidthItemType = 110;
        s.ColumnWidthAttributes = 90;
        s.ColumnWidthPermissions = 110;
        s.ColumnWidthOwnerGroup = 110;
        SettingsService.Instance.SaveSettings(s);
        NotifyColumnWidthsChanged();
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
        tab.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ExplorerTabViewModel.CurrentPath) && tab == SelectedTab)
            {
                RawAddressInput = tab.CurrentPath;
            }
            else if (e.PropertyName == nameof(ExplorerTabViewModel.SelectedItem) && tab == SelectedTab)
            {
                NotifyContextMenuProperties();
                if (!IsSuppressingPreview)
                {
                    FileSelectedForPreview?.Invoke(tab.SelectedItem);
                }
            }
        };
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
            value.IsSelected = true;
            value.LastActiveTime = DateTime.Now;
            RawAddressInput = value.CurrentPath;
            NotifyContextMenuProperties();
            FileSelectedForPreview?.Invoke(value.SelectedItem);
        }
    }

    [RelayCommand]
    public void AddNewTab(string? path = null)
    {
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
        if (target != null)
        {
            AddNewTab(target.CurrentPath);
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
    public void OpenWith()
    {
        var target = SelectedTab?.SelectedItem;
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
            await ClipboardFileService.PasteAsync(SelectedTab.CurrentPath);
            await SelectedTab.RefreshAsync();
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
}
