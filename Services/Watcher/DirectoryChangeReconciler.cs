using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClankerExplorer.Models;
using ClankerExplorer.ViewModels;

namespace ClankerExplorer.Services.Watcher;

public sealed class DirectoryChangeReconciler
{
    private const int FallbackThreshold = 150;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly ExplorerTabViewModel _tab;

    public DirectoryChangeReconciler(ExplorerTabViewModel tab)
    {
        _tab = tab ?? throw new ArgumentNullException(nameof(tab));
    }

    public void HandleBatch(DirectoryChangeBatch batch)
    {
        if (batch == null || string.IsNullOrWhiteSpace(batch.DirectoryPath)) return;

        // Ignore events from other directories (e.g. previous directory before navigation)
        if (!PathComparer.Equals(batch.DirectoryPath, _tab.CurrentPath)) return;

        // Fallback to state-preserving full refresh if overflow or large burst
        if (batch.IsOverflow || batch.Changes.Count >= FallbackThreshold)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!PathComparer.Equals(batch.DirectoryPath, _tab.CurrentPath)) return;
                _ = _tab.RefreshAsync();
            }, DispatcherPriority.Background);
            return;
        }

        // Pre-resolve metadata off UI thread to avoid blocking UI with disk I/O
        Task.Run(() =>
        {
            var resolved = ResolveMetadata(batch.Changes);
            Dispatcher.UIThread.Post(() =>
            {
                ApplyResolvedBatch(batch.DirectoryPath, resolved);
            }, DispatcherPriority.Background);
        });
    }

    public void ReconcileCreatedOrChangedSync(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return;
        var change = new FileChangeEvent(DirectoryChangeKind.Created, fullPath);
        var resolved = ResolveMetadata(new[] { change });
        ApplyResolvedBatch(_tab.CurrentPath, resolved);
    }

    public void ReconcileDeletedSync(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return;
        if (ApplyDeleted(fullPath))
        {
            _tab.NotifyFilteredItemsChanged();
        }
    }

    public void ReconcileRenamedSync(string oldFullPath, string newFullPath)
    {
        if (string.IsNullOrWhiteSpace(newFullPath)) return;
        var change = new FileChangeEvent(DirectoryChangeKind.Renamed, newFullPath, oldFullPath);
        var resolved = ResolveMetadata(new[] { change });
        ApplyResolvedBatch(_tab.CurrentPath, resolved);
    }

    private sealed record ResolvedChange(
        FileChangeEvent Event,
        bool Exists,
        bool IsDirectory,
        string Name,
        string Extension,
        long SizeBytes,
        DateTime ModifiedTime,
        DateTime CreatedTime,
        DateTime AccessedTime,
        string AttributesString);

    private List<ResolvedChange> ResolveMetadata(IReadOnlyList<FileChangeEvent> changes)
    {
        var list = new List<ResolvedChange>(changes.Count);
        foreach (var change in changes)
        {
            if (change.Kind == DirectoryChangeKind.Deleted)
            {
                list.Add(new ResolvedChange(
                    change,
                    Exists: false,
                    IsDirectory: false,
                    Name: Path.GetFileName(change.FullPath),
                    Extension: Path.GetExtension(change.FullPath),
                    SizeBytes: 0,
                    ModifiedTime: DateTime.MinValue,
                    CreatedTime: DateTime.MinValue,
                    AccessedTime: DateTime.MinValue,
                    AttributesString: string.Empty));
                continue;
            }

            bool isDir = Directory.Exists(change.FullPath);
            bool isFile = !isDir && File.Exists(change.FullPath);

            if (!isDir && !isFile)
            {
                // File may have been deleted immediately after creation
                list.Add(new ResolvedChange(
                    change,
                    Exists: false,
                    IsDirectory: false,
                    Name: Path.GetFileName(change.FullPath),
                    Extension: Path.GetExtension(change.FullPath),
                    SizeBytes: 0,
                    ModifiedTime: DateTime.MinValue,
                    CreatedTime: DateTime.MinValue,
                    AccessedTime: DateTime.MinValue,
                    AttributesString: string.Empty));
                continue;
            }

            try
            {
                if (isDir)
                {
                    var di = new DirectoryInfo(change.FullPath);
                    list.Add(new ResolvedChange(
                        change,
                        Exists: true,
                        IsDirectory: true,
                        Name: di.Name,
                        Extension: string.Empty,
                        SizeBytes: 0,
                        ModifiedTime: di.LastWriteTime,
                        CreatedTime: di.CreationTime,
                        AccessedTime: di.LastAccessTime,
                        AttributesString: di.Attributes.ToString()));
                }
                else
                {
                    var fi = new FileInfo(change.FullPath);
                    list.Add(new ResolvedChange(
                        change,
                        Exists: true,
                        IsDirectory: false,
                        Name: fi.Name,
                        Extension: fi.Extension,
                        SizeBytes: fi.Length,
                        ModifiedTime: fi.LastWriteTime,
                        CreatedTime: fi.CreationTime,
                        AccessedTime: fi.LastAccessTime,
                        AttributesString: fi.Attributes.ToString()));
                }
            }
            catch
            {
                list.Add(new ResolvedChange(
                    change,
                    Exists: false,
                    IsDirectory: isDir,
                    Name: Path.GetFileName(change.FullPath),
                    Extension: Path.GetExtension(change.FullPath),
                    SizeBytes: 0,
                    ModifiedTime: DateTime.MinValue,
                    CreatedTime: DateTime.MinValue,
                    AccessedTime: DateTime.MinValue,
                    AttributesString: string.Empty));
            }
        }
        return list;
    }

    private void ApplyResolvedBatch(string directoryPath, List<ResolvedChange> resolvedChanges)
    {
        if (!PathComparer.Equals(directoryPath, _tab.CurrentPath) || _tab.Items == null) return;

        bool filteredChanged = false;
        var comparer = CreateItemComparer(_tab.SortColumn, _tab.SortAscending);

        foreach (var change in resolvedChanges)
        {
            switch (change.Event.Kind)
            {
                case DirectoryChangeKind.Deleted:
                    if (ApplyDeleted(change.Event.FullPath))
                    {
                        filteredChanged = true;
                    }
                    break;

                case DirectoryChangeKind.Created:
                    if (change.Exists)
                    {
                        if (ApplyCreatedOrChanged(change, comparer))
                        {
                            filteredChanged = true;
                        }
                    }
                    else
                    {
                        if (ApplyDeleted(change.Event.FullPath))
                        {
                            filteredChanged = true;
                        }
                    }
                    break;

                case DirectoryChangeKind.Changed:
                    if (change.Exists)
                    {
                        if (ApplyCreatedOrChanged(change, comparer))
                        {
                            filteredChanged = true;
                        }
                    }
                    else
                    {
                        if (ApplyDeleted(change.Event.FullPath))
                        {
                            filteredChanged = true;
                        }
                    }
                    break;

                case DirectoryChangeKind.Renamed:
                    if (ApplyRenamed(change, comparer))
                    {
                        filteredChanged = true;
                    }
                    break;
            }
        }

        if (filteredChanged)
        {
            _tab.NotifyFilteredItemsChanged();
        }
    }

    public bool ApplyDeleted(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || _tab.Items == null) return false;

        var existing = _tab.Items.FirstOrDefault(i => PathComparer.Equals(i.FullPath, fullPath));
        if (existing == null) return false;

        _tab.Items.Remove(existing);

        bool removedFromFiltered = false;
        if (_tab.FilteredItems.Contains(existing))
        {
            _tab.FilteredItems.Remove(existing);
            removedFromFiltered = true;
        }

        if (_tab.SelectedItems.Contains(existing))
        {
            _tab.SelectedItems.Remove(existing);
        }

        if (_tab.SelectedItem == existing)
        {
            _tab.SelectedItem = _tab.SelectedItems.LastOrDefault() ?? _tab.FilteredItems.LastOrDefault();
        }

        return removedFromFiltered;
    }

    private bool ApplyCreatedOrChanged(ResolvedChange change, IComparer<FileItem> comparer)
    {
        if (_tab.Items == null) return false;

        var existing = _tab.Items.FirstOrDefault(i => PathComparer.Equals(i.FullPath, change.Event.FullPath));
        if (existing != null)
        {
            // Existing item changed
            existing.SizeBytes = change.SizeBytes;
            existing.FormattedSize = change.IsDirectory ? "<DIR>" : FileSystemService.FormatBytes(change.SizeBytes);
            existing.ModifiedTime = change.ModifiedTime;
            existing.AttributesString = change.AttributesString;
            existing.ThumbnailImage = null; // Invalidate thumbnail for re-fetch

            // If sort affects this field, reposition in FilteredItems if needed
            if (_tab.FilteredItems.Contains(existing))
            {
                if (_tab.SortColumn is "Size" or "Modified" or "Date Modified")
                {
                    _tab.FilteredItems.Remove(existing);
                    int insertIdx = FindSortedIndex(_tab.FilteredItems, existing, comparer);
                    _tab.FilteredItems.Insert(insertIdx, existing);
                    return true;
                }
            }
            return false;
        }

        // New item created
        var newItem = new FileItem
        {
            Name = change.Name,
            FullPath = change.Event.FullPath,
            ParentPath = _tab.CurrentPath,
            IsDirectory = change.IsDirectory,
            Extension = change.Extension,
            SizeBytes = change.SizeBytes,
            FormattedSize = change.IsDirectory ? "<DIR>" : FileSystemService.FormatBytes(change.SizeBytes),
            ModifiedTime = change.ModifiedTime,
            CreatedTime = change.CreatedTime,
            AccessedTime = change.AccessedTime,
            AttributesString = change.AttributesString,
            IsCut = ClipboardFileService.IsPathCut(change.Event.FullPath)
        };

        _tab.Items.Add(newItem);

        if (MatchesFilter(newItem, _tab.FilterText, _tab.IsFilterRegex))
        {
            int insertIdx = FindSortedIndex(_tab.FilteredItems, newItem, comparer);
            _tab.FilteredItems.Insert(insertIdx, newItem);
            return true;
        }

        return false;
    }

    private bool ApplyRenamed(ResolvedChange change, IComparer<FileItem> comparer)
    {
        if (_tab.Items == null) return false;

        string? oldPath = change.Event.OldFullPath;
        FileItem? targetItem = null;

        if (!string.IsNullOrEmpty(oldPath))
        {
            targetItem = _tab.Items.FirstOrDefault(i => PathComparer.Equals(i.FullPath, oldPath));
        }

        if (targetItem == null)
        {
            // If old item was not found, check if already updated
            targetItem = _tab.Items.FirstOrDefault(i => PathComparer.Equals(i.FullPath, change.Event.FullPath));
            if (targetItem != null)
            {
                return false;
            }

            // Otherwise treat as new creation
            if (change.Exists)
            {
                return ApplyCreatedOrChanged(change, comparer);
            }
            return false;
        }

        bool wasSelected = _tab.SelectedItems.Contains(targetItem) || targetItem.IsThumbnailSelected;
        bool wasFocused = _tab.SelectedItem == targetItem;

        targetItem.FullPath = change.Event.FullPath;
        targetItem.Name = change.Name;
        targetItem.Extension = change.Extension;
        targetItem.SizeBytes = change.SizeBytes;
        targetItem.FormattedSize = change.IsDirectory ? "<DIR>" : FileSystemService.FormatBytes(change.SizeBytes);
        targetItem.ModifiedTime = change.ModifiedTime;
        targetItem.ThumbnailImage = null; // Invalidate thumbnail

        bool wasInFiltered = _tab.FilteredItems.Contains(targetItem);
        bool nowMatches = MatchesFilter(targetItem, _tab.FilterText, _tab.IsFilterRegex);

        if (wasInFiltered)
        {
            _tab.FilteredItems.Remove(targetItem);
        }

        if (nowMatches)
        {
            int insertIdx = FindSortedIndex(_tab.FilteredItems, targetItem, comparer);
            _tab.FilteredItems.Insert(insertIdx, targetItem);
        }

        if (wasSelected)
        {
            targetItem.IsThumbnailSelected = true;
            if (!_tab.SelectedItems.Contains(targetItem))
            {
                _tab.SelectedItems.Add(targetItem);
            }
            if (wasFocused)
            {
                _tab.SelectedItem = targetItem;
            }
        }

        return true;
    }

    public static bool MatchesFilter(FileItem item, string filterText, bool isFilterRegex)
    {
        if (string.IsNullOrWhiteSpace(filterText)) return true;

        if (isFilterRegex)
        {
            try
            {
                var regex = new Regex(filterText, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
                return regex.IsMatch(item.Name) || regex.IsMatch(item.Extension);
            }
            catch
            {
                return item.Name.Contains(filterText, StringComparison.OrdinalIgnoreCase);
            }
        }

        if (filterText.Contains('*') || filterText.Contains('?'))
        {
            var glob = "^" + Regex.Escape(filterText).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            try
            {
                var regex = new Regex(glob, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
                return regex.IsMatch(item.Name);
            }
            catch
            {
                return item.Name.Contains(filterText, StringComparison.OrdinalIgnoreCase);
            }
        }

        return item.Name.Contains(filterText, StringComparison.OrdinalIgnoreCase) ||
               item.Extension.Contains(filterText, StringComparison.OrdinalIgnoreCase);
    }

    public static int FindSortedIndex(IList<FileItem> list, FileItem item, IComparer<FileItem> comparer)
    {
        int low = 0;
        int high = list.Count - 1;

        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            int cmp = comparer.Compare(list[mid], item);

            if (cmp < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low;
    }

    public static IComparer<FileItem> CreateItemComparer(string sortColumn, bool sortAscending)
    {
        return new FileItemComparer(sortColumn, sortAscending);
    }

    private sealed class FileItemComparer : IComparer<FileItem>
    {
        private readonly string _sortColumn;
        private readonly bool _sortAscending;

        public FileItemComparer(string sortColumn, bool sortAscending)
        {
            _sortColumn = sortColumn ?? "Name";
            _sortAscending = sortAscending;
        }

        public int Compare(FileItem? x, FileItem? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            int primary = _sortColumn switch
            {
                "Extension" or "Type" => string.Compare(x.Extension, y.Extension, StringComparison.OrdinalIgnoreCase),
                "Size" => x.SizeBytes.CompareTo(y.SizeBytes),
                "Modified" or "Date Modified" => x.ModifiedTime.CompareTo(y.ModifiedTime),
                "Created" or "Date Created" => x.CreatedTime.CompareTo(y.CreatedTime),
                "Accessed" or "Date Accessed" => x.AccessedTime.CompareTo(y.AccessedTime),
                "Attributes" => string.Compare(x.AttributesString, y.AttributesString, StringComparison.OrdinalIgnoreCase),
                "Permissions" => string.Compare(x.PermissionsString, y.PermissionsString, StringComparison.OrdinalIgnoreCase),
                "OwnerGroup" => string.Compare(x.OwnerGroupString, y.OwnerGroupString, StringComparison.OrdinalIgnoreCase),
                _ => NaturalStringComparer.OrdinalIgnoreCase.Compare(x.Name, y.Name)
            };

            int result = primary != 0
                ? (_sortAscending ? primary : -primary)
                : NaturalStringComparer.OrdinalIgnoreCase.Compare(x.Name, y.Name);

            return result;
        }
    }
}
