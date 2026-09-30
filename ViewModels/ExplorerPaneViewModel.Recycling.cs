using System.IO;
using CommunityToolkit.Mvvm.Input;
using ClankerExplorer.Services;

namespace ClankerExplorer.ViewModels;

public partial class ExplorerPaneViewModel
{
    public bool IsRecycledItemSelected => SelectedTab?.SelectedItem is { FullPath: { Length: > 0 } path } &&
        Path.GetFileName(Path.GetDirectoryName(path)) == RecycleBinService.DirectoryName;

    [RelayCommand]
    public void OpenRecycleBin()
    {
        var tab = SelectedTab;
        if (tab == null) return;
        if (OperatingSystem.IsWindows() && !RecycleBinService.IsNetworkPath(tab.CurrentPath))
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe",
                "shell:RecycleBinFolder") { UseShellExecute = true }); }
            catch (Exception ex) { tab.StatusMessage = $"Could not open Recycle Bin: {ex.Message}"; }
            return;
        }
        string bin = Path.GetFileName(tab.CurrentPath) == RecycleBinService.DirectoryName
            ? tab.CurrentPath : RecycleBinService.GetBinDirectory(tab.CurrentPath);
        if (!Directory.Exists(bin))
        {
            tab.StatusMessage = "No items have been recycled in this folder.";
            return;
        }
        tab.NavigateTo(bin);
    }

    [RelayCommand]
    public void OpenRecycleHistory()
    {
        try
        {
            string path = RecycleBinService.Instance.HistoryPath;
            if (!File.Exists(path)) { SelectedTab!.StatusMessage = "No recycle operations have been recorded yet."; return; }
            var start = new System.Diagnostics.ProcessStartInfo("notepad.exe") { UseShellExecute = true };
            start.ArgumentList.Add(path);
            System.Diagnostics.Process.Start(start);
        }
        catch (Exception ex) { if (SelectedTab != null) SelectedTab.StatusMessage = $"Could not open recycle history: {ex.Message}"; }
    }

    [RelayCommand]
    public async Task RestoreRecycledItemAsync()
    {
        var tab = SelectedTab;
        var item = tab?.SelectedItem;
        if (tab == null || item == null || !IsRecycledItemSelected) return;
        try
        {
            await PrepareForExternalOpenAsync(item.FullPath);
            string originalPath = await Task.Run(() => RecycleBinService.Instance.Restore(item.FullPath));
            tab.PendingSelectPath = originalPath;
            tab.NavigateTo(Path.GetDirectoryName(originalPath)!);
        }
        catch (Exception ex)
        {
            tab.StatusMessage = $"Restore failed: {ex.Message}";
        }
    }
}
