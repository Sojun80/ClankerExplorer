using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using ClankerExplorer.Models;
using ClankerExplorer.Services;
using ClankerExplorer.ViewModels;

namespace ClankerExplorer.Views;

public partial class SettingsWindow : Window
{
    private bool _isSaved = false;

    public SettingsWindow()
    {
        InitializeComponent();

        var vm = new SettingsViewModel();
        DataContext = vm;

        // Live Preview: Update theme across entire application in real-time
        vm.RequestPreview += previewSettings =>
        {
            ThemeManager.ApplyTheme(previewSettings);
        };

        vm.RequestClose += () =>
        {
            _isSaved = true;
            Close(true);
        };

        Closing += (s, e) =>
        {
            if (!_isSaved)
            {
                // Revert to original theme if closed without saving
                ThemeManager.ApplyTheme(vm.GetOriginalState());
            }
        };

        vm.RequestExport += async () =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel != null)
            {
                var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Export C-Explorer Settings",
                    DefaultExtension = "json",
                    SuggestedFileName = "c-explorer-settings.json",
                    FileTypeChoices = new[]
                    {
                        new FilePickerFileType("JSON File (*.json)") { Patterns = new[] { "*.json" } }
                    }
                });

                if (file != null)
                {
                    var localPath = file.Path.LocalPath;
                    SettingsService.Instance.ExportSettings(localPath);
                    vm.StatusMessage = $"Exported settings to {Path.GetFileName(localPath)}";
                }
            }
        };

        vm.RequestImport += async () =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel != null)
            {
                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Import C-Explorer Settings",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("JSON File (*.json)") { Patterns = new[] { "*.json" } }
                    }
                });

                if (files.Count > 0)
                {
                    var localPath = files[0].Path.LocalPath;
                    SettingsService.Instance.ImportSettings(localPath);
                    ThemeManager.ApplyTheme(SettingsService.Instance.CurrentSettings);
                    DataContext = new SettingsViewModel { StatusMessage = $"Imported settings from {Path.GetFileName(localPath)}" };
                }
            }
        };
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
