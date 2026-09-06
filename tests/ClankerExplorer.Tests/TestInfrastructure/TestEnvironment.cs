using System.Runtime.CompilerServices;
using System.Text.Json;
using ClankerExplorer.Models;
using ClankerExplorer.Services;

namespace ClankerExplorer.Tests.TestInfrastructure;

public static class TestEnvironment
{
    public static string ProcessRoot { get; private set; } = string.Empty;
    public static string DataDirectory { get; private set; } = string.Empty;
    public static string DefaultFolder { get; private set; } = string.Empty;

    [ModuleInitializer]
    public static void Initialize()
    {
        ProcessRoot = Path.Combine(Path.GetTempPath(), $"clanker-explorer-tests-{Guid.NewGuid():N}");
        DataDirectory = Path.Combine(ProcessRoot, "data");
        DefaultFolder = Path.Combine(ProcessRoot, "default-folder");
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(DefaultFolder);

        Environment.SetEnvironmentVariable(
            AppStoragePaths.DataDirectoryEnvironmentVariable,
            DataDirectory);

        var settings = new AppSettings
        {
            DefaultPath = DefaultFolder,
            StartupBehavior = "OpenDefaultPath",
            StartInDualPane = false,
            ShowInspectorOnStartup = true
        };
        File.WriteAllText(
            Path.Combine(DataDirectory, "settings.json"),
            JsonSerializer.Serialize(settings));

        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDeleteProcessRoot();
    }

    public static void ResetGlobalSettings(string defaultPath)
    {
        FolderViewStateService.Instance.ClearAll();
        SettingsService.Instance.SaveSettings(new AppSettings
        {
            DefaultPath = defaultPath,
            StartupBehavior = "OpenDefaultPath",
            StartInDualPane = false,
            ShowInspectorOnStartup = true,
            AlwaysOnTop = false,
            ShowOperationsWorkspaceOnStartup = false,
            ShowSearchWorkspaceOnStartup = false
        });

        var sessionPath = SessionService.Instance.SessionFilePath;
        if (File.Exists(sessionPath)) File.Delete(sessionPath);
    }

    private static void TryDeleteProcessRoot()
    {
        try
        {
            if (Directory.Exists(ProcessRoot)) Directory.Delete(ProcessRoot, recursive: true);
        }
        catch
        {
            // The OS temporary directory remains the containment boundary if a
            // background file handle is still winding down at process exit.
        }
    }
}
