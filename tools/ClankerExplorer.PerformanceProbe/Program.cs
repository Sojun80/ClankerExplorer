using System.Diagnostics;
using System.Text.Json;
using ClankerExplorer.Services;

if (args.Length != 1 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: ClankerExplorer.PerformanceProbe <existing-directory>");
    return 2;
}

string directory = Path.GetFullPath(args[0]);
var service = new FileSystemService();

// Warm runtime/JIT and filesystem metadata caches before the measured pass.
await service.ReadDirectoryAsync(directory);
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

long allocationStart = GC.GetTotalAllocatedBytes(true);
var enumerationTimer = Stopwatch.StartNew();
var (items, error) = await service.ReadDirectoryAsync(directory);
enumerationTimer.Stop();
long enumerationBytes = GC.GetTotalAllocatedBytes(true) - allocationStart;
if (error != null)
{
    Console.Error.WriteLine(error);
    return 1;
}

allocationStart = GC.GetTotalAllocatedBytes(true);
var sortTimer = Stopwatch.StartNew();
var sorted = items
    .OrderByDescending(item => item.IsDirectory)
    .ThenBy(item => item.Name, NaturalStringComparer.OrdinalIgnoreCase)
    .ToList();
sortTimer.Stop();
long sortBytes = GC.GetTotalAllocatedBytes(true) - allocationStart;

Console.WriteLine(JsonSerializer.Serialize(new
{
    itemCount = items.Count,
    enumerationMilliseconds = enumerationTimer.Elapsed.TotalMilliseconds,
    enumerationAllocatedBytes = enumerationBytes,
    sortMilliseconds = sortTimer.Elapsed.TotalMilliseconds,
    sortAllocatedBytes = sortBytes
}));
return 0;
