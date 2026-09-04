using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClankerExplorer.AppLayer.Operations;
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
    private readonly object _syncLock = new();
    private Task _processingChain = Task.CompletedTask;
    private long _currentGeneration = 0;
    private long _stagingToken = 0;
    private readonly List<DirectoryChangeBatch> _stagedBatches = new();
    private bool _isStaging;
    private bool _stagedHasOverflow;

    public DirectoryChangeReconciler(ExplorerTabViewModel tab)
    {
        _tab = tab ?? throw new ArgumentNullException(nameof(tab));
    }

    public void Reset()
    {
        Interlocked.Increment(ref _currentGeneration);
    }

    public long BeginStaging()
    {
        lock (_syncLock)
        {
            _isStaging = true;
            _stagedHasOverflow = false;
            _stagedBatches.Clear();
            return ++_stagingToken;
        }
    }

    public void EndStagingAndReplay(long stagingToken)
    {
        List<DirectoryChangeBatch> toReplay;
        bool hadOverflow;
        lock (_syncLock)
        {
            if (!_isStaging || _stagingToken != stagingToken) return;
            _isStaging = false;
            hadOverflow = _stagedHasOverflow;
            _stagedHasOverflow = false;
            if (_stagedBatches.Count == 0 && !hadOverflow) return;
            toReplay = new List<DirectoryChangeBatch>(_stagedBatches);
            _stagedBatches.Clear();
        }

        if (hadOverflow)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _ = _tab.RefreshAsync();
            }, DispatcherPriority.Background);
            return;
        }

        foreach (var batch in toReplay)
        {
            HandleBatch(batch);
        }
    }

    public void EndStagingAndReplay()
    {
        long token;
        lock (_syncLock) { token = _stagingToken; }
        EndStagingAndReplay(token);
    }

    public void CancelStaging(long stagingToken)
    {
        lock (_syncLock)
        {
            if (_stagingToken == stagingToken)
            {
                _isStaging = false;
                _stagedHasOverflow = false;
                _stagedBatches.Clear();
            }
        }
    }

    public void CancelStaging()
    {
        long token;
        lock (_syncLock) { token = _stagingToken; }
        CancelStaging(token);
    }

    public void HandleBatch(DirectoryChangeBatch batch)
    {
        if (batch == null || string.IsNullOrWhiteSpace(batch.DirectoryPath)) return;

        // Ignore events from other directories (e.g. previous directory before navigation)
        if (!PathComparer.Equals(batch.DirectoryPath, _tab.CurrentPath)) return;

        // Filter active transfer temp files so they never flicker into UI
        if (batch.Changes != null && batch.Changes.Count > 0)
        {
            var filteredChanges = new List<FileChangeEvent>(batch.Changes.Count);
            foreach (var c in batch.Changes)
            {
                bool fullIsTemp = TransferEngine.IsActiveTempFile(c.FullPath);
                bool oldIsTemp = !string.IsNullOrEmpty(c.OldFullPath) && TransferEngine.IsActiveTempFile(c.OldFullPath);

                // Any event whose target is a temp file stays hidden
                if (fullIsTemp)
                {
                    continue;
                }

                // If this is a rename from a temp file to a real destination file,
                // transform it into Created(finalUserFile) so it appears immediately!
                if (c.Kind == DirectoryChangeKind.Renamed && oldIsTemp)
                {
                    filteredChanges.Add(new FileChangeEvent(DirectoryChangeKind.Created, c.FullPath));
                    continue;
                }

                if (oldIsTemp)
                {
                    continue;
                }

                filteredChanges.Add(c);
            }

            if (filteredChanges.Count == 0 && !batch.IsOverflow)
            {
                return;
            }

            batch = new DirectoryChangeBatch(batch.DirectoryPath, filteredChanges, batch.IsOverflow);
        }

        long gen = Volatile.Read(ref _currentGeneration);

        lock (_syncLock)
        {
            if (_isStaging)
            {
                if (batch.IsOverflow || (batch.Changes?.Count ?? 0) >= FallbackThreshold)
                {
                    _stagedHasOverflow = true;
                }
                else
                {
                    _stagedBatches.Add(batch);
                }
                return;
            }
        }

        // Fallback to state-preserving full refresh if overflow or large burst
        if (batch.IsOverflow || (batch.Changes?.Count ?? 0) >= FallbackThreshold)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (gen != Volatile.Read(ref _currentGeneration)) return;
                if (!PathComparer.Equals(batch.DirectoryPath, _tab.CurrentPath)) return;
                _ = _tab.RefreshAsync();
            }, DispatcherPriority.Background);
            return;
        }

        lock (_syncLock)
        {
            _processingChain = ProcessBatchSequentialAsync(_processingChain, batch, gen);
        }
    }

    private async Task ProcessBatchSequentialAsync(Task previousTask, DirectoryChangeBatch batch, long gen)
    {
        try
        {
            await previousTask.ConfigureAwait(false);
        }
        catch
        {
            // Ignore previous task failure so sequential pipeline continues
        }

        if (gen != Volatile.Read(ref _currentGeneration)) return;
        if (!PathComparer.Equals(batch.DirectoryPath, _tab.CurrentPath)) return;

        List<ResolvedChange> resolved;
        try
        {
            resolved = await Task.Run(() => ResolveMetadata(batch.Changes)).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        if (gen != Volatile.Read(ref _currentGeneration)) return;
        if (!PathComparer.Equals(batch.DirectoryPath, _tab.CurrentPath)) return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (gen != Volatile.Read(ref _currentGeneration)) return;
            if (!PathComparer.Equals(batch.DirectoryPath, _tab.CurrentPath)) return;
            ApplyResolvedBatch(batch.DirectoryPath, resolved);
        }, DispatcherPriority.Background);
    }

    public void ReconcileCreatedOrChangedSync(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || TransferEngine.IsActiveTempFile(fullPath)) return;
        var change = new FileChangeEvent(DirectoryChangeKind.Created, fullPath);
        var resolved = ResolveMetadata(new[] { change });
        ApplyResolvedBatch(_tab.CurrentPath, resolved);
    }

    public void ReconcileDeletedSync(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || TransferEngine.IsActiveTempFile(fullPath)) return;
        if (ApplyDeleted(fullPath))
        {
            _tab.NotifyFilteredItemsChanged();
        }
    }

    public void ReconcileRenamedSync(string oldFullPath, string newFullPath)
    {
        if (string.IsNullOrWhiteSpace(newFullPath) || TransferEngine.IsActiveTempFile(newFullPath)) return;
        if (TransferEngine.IsActiveTempFile(oldFullPath))
        {
            ReconcileCreatedOrChangedSync(newFullPath);
            return;
        }
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

        if (_tab.PendingSelectPaths != null && _tab.PendingSelectPaths.Count > 0)
        {
            _tab.SelectPaths(_tab.PendingSelectPaths, scrollIntoView: false);
        }
    }

    public bool ApplyDeleted(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || _tab.Items == null) return false;

        var existing = _tab.Items.FirstOrDefault(i => PathComparer.Equals(i.FullPath, fullPath));
        if (existing == null) return false;

        int deletedFilteredIndex = _tab.FilteredItems.IndexOf(existing);
        bool wasFocused = _tab.SelectedItem == existing;
        bool wasSelected = _tab.SelectedItems.Contains(existing) || existing.IsThumbnailSelected;

        _tab.Items.Remove(existing);

        bool removedFromFiltered = false;
        if (deletedFilteredIndex >= 0)
        {
            _tab.FilteredItems.RemoveAt(deletedFilteredIndex);
            removedFromFiltered = true;
        }

        if (_tab.SelectedItems.Contains(existing))
        {
            _tab.SelectedItems.Remove(existing);
        }
        existing.IsThumbnailSelected = false;

        if (wasFocused || (wasSelected && _tab.SelectedItems.Count == 0))
        {
            FileItem? newSelection = null;
            if (_tab.FilteredItems.Count > 0)
            {
                // Select nearest item: item now occupying deleted index, or previous item if deleted at end
                int targetIndex = Math.Clamp(deletedFilteredIndex, 0, _tab.FilteredItems.Count - 1);
                newSelection = _tab.FilteredItems[targetIndex];
            }

            _tab.SelectedItem = newSelection;
            if (newSelection != null)
            {
                newSelection.IsThumbnailSelected = true;
                if (!_tab.SelectedItems.Contains(newSelection))
                {
                    _tab.SelectedItems.Add(newSelection);
                }
            }
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
            existing.CreatedTime = change.CreatedTime;
            existing.AccessedTime = change.AccessedTime;
            existing.AttributesString = change.AttributesString;
            existing.ThumbnailImage = null; // Invalidate thumbnail for re-fetch

            _tab.TriggerThumbnailViewportUpdate();

            // If sort affects this field, reposition in FilteredItems if needed
            if (_tab.FilteredItems.Contains(existing))
            {
                if (_tab.SortColumn is "Size" or "Modified" or "Date Modified" or "Created" or "Date Created" or "Accessed" or "Date Accessed" or "Attributes")
                {
                    _tab.FilteredItems.Remove(existing);
                    int insertIdx = FindSortedIndex(_tab.FilteredItems, existing, comparer);
                    _tab.FilteredItems.Insert(insertIdx, existing);
                    return true;
                }
            }
            return false;
        }

        // If this was an in-place Changed event and the item is not in the collection, do not recreate it
        if (change.Event.Kind == DirectoryChangeKind.Changed)
        {
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
        targetItem.CreatedTime = change.CreatedTime;
        targetItem.AccessedTime = change.AccessedTime;
        targetItem.AttributesString = change.AttributesString;
        targetItem.ThumbnailImage = null; // Invalidate thumbnail

        _tab.TriggerThumbnailViewportUpdate();

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
