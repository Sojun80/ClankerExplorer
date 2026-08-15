namespace ClankerExplorer.Tests.TestInfrastructure;

public sealed class TemporaryFileSystem : IDisposable
{
    public string Root { get; }
    public string FolderA => Path.Combine(Root, "FolderA");
    public string FolderB => Path.Combine(Root, "FolderB");
    public string FolderC => Path.Combine(Root, "FolderC");
    public string Nested => Path.Combine(FolderC, "Nested");
    public string Config => Path.Combine(Root, ".config");

    public TemporaryFileSystem()
    {
        Root = Path.Combine(Path.GetTempPath(), $"clanker-fs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(FolderA);
        Directory.CreateDirectory(FolderB);
        Directory.CreateDirectory(Nested);
        Directory.CreateDirectory(Config);

        File.WriteAllText(Path.Combine(FolderA, "alpha.txt"), "alpha");
        File.WriteAllText(Path.Combine(FolderA, "beta.txt"), "beta");
        File.WriteAllText(Path.Combine(FolderA, "file2.txt"), "two");
        File.WriteAllText(Path.Combine(FolderA, "file10.txt"), "ten");
        File.WriteAllText(Path.Combine(Nested, "nested.txt"), "nested");
    }

    public string CreateDirectory(string relativePath)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    public string CreateFile(string relativePath, string content = "content")
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // Tests never escape this dedicated OS temporary directory.
        }
    }
}
