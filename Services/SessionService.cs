using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClankerExplorer.Models;
using ClankerExplorer.ViewModels;

namespace ClankerExplorer.Services;

public class TabSessionItem
{
    public string Path { get; set; } = string.Empty;
    public bool IsPinned { get; set; }
    public DateTime LastActiveTime { get; set; } = DateTime.Now;
}

public class PaneSessionState
{
    public List<TabSessionItem> Tabs { get; set; } = new();
    public int ActiveTabIndex { get; set; }
    public string? ActiveTabPath { get; set; }
}

public class AppSessionState
{
    public bool IsDualPane { get; set; }
    public string ActivePaneId { get; set; } = "left";
    public PaneSessionState LeftPane { get; set; } = new();
    public PaneSessionState RightPane { get; set; } = new();
}

public class SessionService
{
    public static SessionService Instance { get; } = new();

    private readonly string _sessionFilePath;
    private readonly string _portableSessionFilePath;

    public SessionService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var sessionDir = Path.Combine(appData, "C-Explorer");
        Directory.CreateDirectory(sessionDir);
        _sessionFilePath = Path.Combine(sessionDir, "session.json");

        var appBase = AppDomain.CurrentDomain.BaseDirectory;
        _portableSessionFilePath = Path.Combine(appBase, "session.json");
    }

    public void SaveSession(MainViewModel vm)
    {
        try
        {
            var state = new AppSessionState
            {
                IsDualPane = vm.IsDualPane,
                ActivePaneId = vm.ActivePane == vm.RightPane ? "right" : "left",
                LeftPane = BuildPaneSession(vm.LeftPane),
                RightPane = BuildPaneSession(vm.RightPane)
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(state, options);

            string targetPath = File.Exists(_portableSessionFilePath) ? _portableSessionFilePath : _sessionFilePath;
            File.WriteAllText(targetPath, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save session: {ex.Message}");
        }
    }

    private static PaneSessionState BuildPaneSession(ExplorerPaneViewModel pane)
    {
        var paneState = new PaneSessionState();
        if (pane == null) return paneState;

        int activeIdx = 0;
        for (int i = 0; i < pane.Tabs.Count; i++)
        {
            var tab = pane.Tabs[i];
            if (tab == pane.SelectedTab) activeIdx = i;

            paneState.Tabs.Add(new TabSessionItem
            {
                Path = tab.CurrentPath,
                IsPinned = tab.IsPinned,
                LastActiveTime = tab.LastActiveTime
            });
        }

        paneState.ActiveTabIndex = activeIdx;
        paneState.ActiveTabPath = pane.SelectedTab?.CurrentPath;
        return paneState;
    }

    public AppSessionState? LoadSession()
    {
        try
        {
            string targetPath = File.Exists(_portableSessionFilePath) ? _portableSessionFilePath : _sessionFilePath;
            if (File.Exists(targetPath))
            {
                var json = File.ReadAllText(targetPath);
                return JsonSerializer.Deserialize<AppSessionState>(json);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load session: {ex.Message}");
        }
        return null;
    }

    public List<TabSessionItem> FilterTabsToRestore(List<TabSessionItem> savedTabs, int maxRestoreCap, string? activePath)
    {
        if (savedTabs == null || savedTabs.Count == 0) return new List<TabSessionItem>();

        // Valid existing directories only (or fallback root)
        var validTabs = savedTabs.Where(t => Directory.Exists(t.Path) || t.Path.StartsWith(@"\\")).ToList();
        if (validTabs.Count == 0) return new List<TabSessionItem>();

        // Pinned tabs always survive regardless of cap
        var pinned = validTabs.Where(t => t.IsPinned).ToList();
        var unpinned = validTabs.Where(t => !t.IsPinned).ToList();

        // Available slots for unpinned tabs
        int availableSlots = Math.Max(1, maxRestoreCap - pinned.Count);

        // Sort unpinned by most recently active, ensuring the active tab is prioritized
        var stringComp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prioritizedUnpinned = unpinned
            .OrderByDescending(t => !string.IsNullOrEmpty(activePath) && string.Equals(t.Path, activePath, stringComp))
            .ThenByDescending(t => t.LastActiveTime)
            .Take(availableSlots)
            .ToHashSet();

        // Reconstruct the final list maintaining original relative order
        return validTabs
            .Where(t => t.IsPinned || prioritizedUnpinned.Contains(t))
            .ToList();
    }
}
