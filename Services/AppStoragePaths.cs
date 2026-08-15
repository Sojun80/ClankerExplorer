using System;
using System.IO;

namespace ClankerExplorer.Services;

public static class AppStoragePaths
{
    public const string DataDirectoryEnvironmentVariable = "CLANKEREXPLORER_DATA_DIR";

    public static string GetDataDirectory(string? explicitDirectory = null)
    {
        var directory = explicitDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "C-Explorer");
        }

        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static string GetPortableFilePath(string fileName, string? explicitDataDirectory = null)
    {
        // An explicit/test data directory must be completely isolated from files
        // beside the executable, even if the app is normally running portably.
        if (!string.IsNullOrWhiteSpace(explicitDataDirectory) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable)))
        {
            return Path.Combine(GetDataDirectory(explicitDataDirectory), fileName);
        }

        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
    }
}
