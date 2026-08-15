using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClankerExplorer.Models;
using ClankerExplorer.Services;

namespace ClankerExplorer.ViewModels;

public partial class BatchRenameViewModel : ObservableObject
{
    private readonly List<string> _targetPaths;

    [ObservableProperty]
    private string _mode = "replace"; // replace, prefix_suffix, numbering, change_case

    [ObservableProperty]
    private string _findText = string.Empty;

    [ObservableProperty]
    private string _replaceText = string.Empty;

    [ObservableProperty]
    private bool _isRegex;

    [ObservableProperty]
    private bool _caseSensitive;

    [ObservableProperty]
    private string _prefix = string.Empty;

    [ObservableProperty]
    private string _suffix = string.Empty;

    [ObservableProperty]
    private int _startNumber = 1;

    [ObservableProperty]
    private int _padding = 3;

    [ObservableProperty]
    private string _caseMode = "lower";

    [ObservableProperty]
    private ObservableCollection<BatchRenameItem> _previewItems = new();

    [ObservableProperty]
    private int _changedCount;

    [ObservableProperty]
    private int _conflictCount;

    [ObservableProperty]
    private string? _errorMessage;

    public event Action? RequestClose;

    public BatchRenameViewModel(IEnumerable<string> targetPaths)
    {
        _targetPaths = targetPaths.ToList();
        UpdatePreview();
    }

    partial void OnModeChanged(string value) => UpdatePreview();
    partial void OnFindTextChanged(string value) => UpdatePreview();
    partial void OnReplaceTextChanged(string value) => UpdatePreview();
    partial void OnIsRegexChanged(bool value) => UpdatePreview();
    partial void OnCaseSensitiveChanged(bool value) => UpdatePreview();
    partial void OnPrefixChanged(string value) => UpdatePreview();
    partial void OnSuffixChanged(string value) => UpdatePreview();
    partial void OnStartNumberChanged(int value) => UpdatePreview();
    partial void OnPaddingChanged(int value) => UpdatePreview();
    partial void OnCaseModeChanged(string value) => UpdatePreview();

    public void UpdatePreview()
    {
        var rule = new BatchRenameRule
        {
            Mode = Mode,
            FindText = FindText,
            ReplaceText = ReplaceText,
            IsRegex = IsRegex,
            CaseSensitive = CaseSensitive,
            Prefix = Prefix,
            Suffix = Suffix,
            StartNumber = StartNumber,
            Padding = Padding,
            CaseMode = CaseMode
        };

        var items = FileSystemService.Instance.PreviewBatchRename(_targetPaths, rule);
        PreviewItems = new ObservableCollection<BatchRenameItem>(items);
        ChangedCount = items.Count(i => i.WillChange);
        ConflictCount = items.Count(i => i.HasConflict);
    }

    [RelayCommand]
    public void Execute()
    {
        ErrorMessage = null;
        try
        {
            FileSystemService.Instance.ExecuteBatchRename(PreviewItems);
            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }
}
