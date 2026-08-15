using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using ClankerExplorer.Models;
using ClankerExplorer.Services;

namespace ClankerExplorer.ViewModels;

public partial class ExplorerTabViewModel : ObservableObject
{
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    private string _title = "C:\\";

    [ObservableProperty]
    private string _currentPath = @"C:\";

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _isFilterRegex;

    [ObservableProperty]
    private bool _isFilterWildcard;

    [ObservableProperty]
    private bool _isFilterBarOpen;

    [ObservableProperty]
    private string _sortColumn = "Name";

    [ObservableProperty]
    private bool _sortAscending = true;

    [ObservableProperty]
    private FileItem? _selectedItem;

    [ObservableProperty]
    private ObservableCollection<FileItem> _items = new();

    [ObservableProperty]
    private ObservableCollection<FileItem> _filteredItems = new();

    [ObservableProperty]
    private ObservableCollection<FileItem> _selectedItems = new();

    public List<string> History { get; } = new();
    public int HistoryIndex { get; private set; } = -1;

    public bool CanGoBack => HistoryIndex > 0;
    public bool CanGoForward => HistoryIndex < History.Count - 1;

    public ExplorerTabViewModel(string initialPath = @"C:\")
    {
        ClipboardFileService.ClipboardChanged += UpdateCutStatus;
        NavigateTo(initialPath);
    }

    public void UpdateCutStatus()
    {
        if (Items == null) return;
        foreach (var item in Items)
        {
            item.IsCut = ClipboardFileService.IsPathCut(item.FullPath);
        }
    }

    public void NavigateTo(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        // Clean path
        path = Path.GetFullPath(path);

        if (HistoryIndex < History.Count - 1)
        {
            History.RemoveRange(HistoryIndex + 1, History.Count - (HistoryIndex + 1));
        }

        History.Add(path);
        HistoryIndex = History.Count - 1;

        CurrentPath = path;
        UpdateTitle(path);
        Refresh();

        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    public void GoBack()
    {
        if (!CanGoBack) return;
        HistoryIndex--;
        CurrentPath = History[HistoryIndex];
        UpdateTitle(CurrentPath);
        Refresh();
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    public void GoForward()
    {
        if (!CanGoForward) return;
        HistoryIndex++;
        CurrentPath = History[HistoryIndex];
        UpdateTitle(CurrentPath);
        Refresh();
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    public void GoUp()
    {
        var parent = Directory.GetParent(CurrentPath);
        if (parent != null)
        {
            NavigateTo(parent.FullName);
        }
    }

    private void UpdateTitle(string path)
    {
        var dirName = Path.GetFileName(path.TrimEnd('\\', '/'));
        Title = string.IsNullOrEmpty(dirName) ? path : dirName;
    }

    public void Refresh()
    {
        var (list, error) = FileSystemService.Instance.ReadDirectory(CurrentPath);
        foreach (var item in list)
        {
            item.IsCut = ClipboardFileService.IsPathCut(item.FullPath);
        }
        Items = new ObservableCollection<FileItem>(list);
        ApplyFilter();
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnIsFilterRegexChanged(bool value) => ApplyFilter();
    partial void OnIsFilterWildcardChanged(bool value) => ApplyFilter();

    public void ApplyFilter()
    {
        IEnumerable<FileItem> query = Items;

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            if (IsFilterRegex)
            {
                try
                {
                    var regex = new Regex(FilterText, RegexOptions.IgnoreCase);
                    query = query.Where(i => regex.IsMatch(i.Name) || regex.IsMatch(i.Extension));
                }
                catch
                {
                    query = query.Where(i => i.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase));
                }
            }
            else if (FilterText.Contains('*') || FilterText.Contains('?'))
            {
                var glob = "^" + Regex.Escape(FilterText).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                try
                {
                    var regex = new Regex(glob, RegexOptions.IgnoreCase);
                    query = query.Where(i => regex.IsMatch(i.Name));
                }
                catch
                {
                    query = query.Where(i => i.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase));
                }
            }
            else
            {
                query = query.Where(i => i.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
                                         i.Extension.Contains(FilterText, StringComparison.OrdinalIgnoreCase));
            }
        }

        // Sort: Folders always on top, then sort column
        IOrderedEnumerable<FileItem> sorted;
        if (SortAscending)
        {
            sorted = SortColumn switch
            {
                "Extension" => query.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.Extension).ThenBy(i => i.Name),
                "Size" => query.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.SizeBytes),
                "Modified" => query.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.ModifiedTime),
                "Attributes" => query.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.AttributesString),
                _ => query.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.Name)
            };
        }
        else
        {
            sorted = SortColumn switch
            {
                "Extension" => query.OrderByDescending(i => i.IsDirectory).ThenByDescending(i => i.Extension).ThenBy(i => i.Name),
                "Size" => query.OrderByDescending(i => i.IsDirectory).ThenByDescending(i => i.SizeBytes),
                "Modified" => query.OrderByDescending(i => i.IsDirectory).ThenByDescending(i => i.ModifiedTime),
                "Attributes" => query.OrderByDescending(i => i.IsDirectory).ThenByDescending(i => i.AttributesString),
                _ => query.OrderByDescending(i => i.IsDirectory).ThenByDescending(i => i.Name)
            };
        }

        FilteredItems = new ObservableCollection<FileItem>(sorted);
    }

    public void SortBy(string column)
    {
        if (SortColumn == column)
        {
            SortAscending = !SortAscending;
        }
        else
        {
            SortColumn = column;
            SortAscending = true;
        }
        ApplyFilter();
    }
}
