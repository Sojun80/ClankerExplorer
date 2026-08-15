using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClankerExplorer.Models;

public partial class NetworkNode : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _uncPath = string.Empty;

    [ObservableProperty]
    private string _type = "Computer"; // "Computer", "Share", "Custom"

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandSymbol))]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isLoading;

    public bool HasLoadedChildren { get; set; }

    public string ExpandSymbol => IsExpanded ? "▾" : "▸";

    public string IconSymbol
    {
        get
        {
            if (Type == "Computer") return "💻";
            if (Type == "Share") return "📁";
            return "🌐";
        }
    }

    public ObservableCollection<NetworkNode> Children { get; } = new();
}
