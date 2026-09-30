using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using ClankerExplorer.Services;
using ClankerExplorer.ViewModels;

namespace ClankerExplorer.Views;

// Details column widths, ordering, and sorting.
public partial class ExplorerPaneView
{
    private void SaveCurrentColumnLayout()
    {
        if (FileDataGrid == null || DataContext is not ExplorerPaneViewModel vm) return;

        var s = SettingsService.Instance.CurrentSettings;
        bool changed = false;

        foreach (var col in FileDataGrid.Columns)
        {
            var header = BaseColumnHeader(col.Header?.ToString());
            double actualWidth = col.ActualWidth;
            if (actualWidth > 20)
            {
                switch (header)
                {
                    case "Name":
                        s.ColumnWidthName = actualWidth;
                        vm.ColumnWidthName = actualWidth;
                        changed = true;
                        break;
                    case "Ext":
                        s.ColumnWidthExt = actualWidth;
                        vm.ColumnWidthExt = actualWidth;
                        changed = true;
                        break;
                    case "Size":
                        s.ColumnWidthSize = actualWidth;
                        vm.ColumnWidthSize = actualWidth;
                        changed = true;
                        break;
                    case "Date Modified":
                        s.ColumnWidthDateModified = actualWidth;
                        vm.ColumnWidthDateModified = actualWidth;
                        changed = true;
                        break;
                    case "Date Created":
                        s.ColumnWidthDateCreated = actualWidth;
                        vm.ColumnWidthDateCreated = actualWidth;
                        changed = true;
                        break;
                    case "Date Accessed":
                        s.ColumnWidthDateAccessed = actualWidth;
                        vm.ColumnWidthDateAccessed = actualWidth;
                        changed = true;
                        break;
                    case "Type":
                        s.ColumnWidthItemType = actualWidth;
                        vm.ColumnWidthItemType = actualWidth;
                        changed = true;
                        break;
                    case "Attributes":
                        s.ColumnWidthAttributes = actualWidth;
                        vm.ColumnWidthAttributes = actualWidth;
                        changed = true;
                        break;
                    case "Permissions":
                        s.ColumnWidthPermissions = actualWidth;
                        vm.ColumnWidthPermissions = actualWidth;
                        changed = true;
                        break;
                    case "Owner:Group":
                        s.ColumnWidthOwnerGroup = actualWidth;
                        vm.ColumnWidthOwnerGroup = actualWidth;
                        changed = true;
                        break;
                }
            }
        }

        if (changed)
        {
            SettingsService.Instance.SaveSettings(s);
        }
        vm.SetCurrentColumnOrder(FileDataGrid.Columns
            .OrderBy(column => column.DisplayIndex)
            .Select(column => BaseColumnHeader(column.Header?.ToString())));
    }

    private void RestoreColumnOrder(IReadOnlyList<string> savedOrder)
    {
        if (FileDataGrid == null || savedOrder.Count == 0) return;
        var byHeader = FileDataGrid.Columns
            .Where(column => column.Header != null)
            .ToDictionary(column => BaseColumnHeader(column.Header!.ToString()), StringComparer.Ordinal);
        int displayIndex = 0;
        foreach (string header in savedOrder)
        {
            if (byHeader.Remove(header, out var column)) column.DisplayIndex = displayIndex++;
        }
        foreach (var column in byHeader.Values.OrderBy(column => column.DisplayIndex))
            column.DisplayIndex = displayIndex++;
    }

    private void OnDataGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        if (DataContext is not ExplorerPaneViewModel vm || vm.SelectedTab == null) return;
        string sortColumn = HeaderToSortColumn(e.Column.Header?.ToString());
        vm.SelectedTab.SortBy(sortColumn);
        vm.PersistCurrentFolderViewState();
        e.Handled = true;
        vm.NotifySortHeadersChanged();
    }

    private static string BaseColumnHeader(string? header) =>
        (header ?? string.Empty).TrimEnd(' ', '↑', '↓');

    private static string HeaderToSortColumn(string? header) => BaseColumnHeader(header) switch
    {
        "Ext" => "Extension",
        "Size" => "Size",
        "Date Modified" => "Modified",
        "Date Created" => "Created",
        "Date Accessed" => "Accessed",
        "Type" => "Type",
        "Attributes" => "Attributes",
        "Permissions" => "Permissions",
        "Owner:Group" => "OwnerGroup",
        _ => "Name"
    };
}
