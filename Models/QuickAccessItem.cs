using CommunityToolkit.Mvvm.ComponentModel;

namespace ClankerExplorer.Models;

public partial class QuickAccessItem : ObservableObject
{
    [ObservableProperty]
    private string _path = string.Empty;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _iconSymbol = "📁";

    [ObservableProperty]
    private bool _isDropTargetTop;

    [ObservableProperty]
    private bool _isDropTargetBottom;

    [ObservableProperty]
    private bool _isBeingDragged;

    public QuickAccessItem() { }

    public QuickAccessItem(string path, string displayName, string iconSymbol = "📁")
    {
        Path = path;
        DisplayName = displayName;
        IconSymbol = iconSymbol;
    }
}
