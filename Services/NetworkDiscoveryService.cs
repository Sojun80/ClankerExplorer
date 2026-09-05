using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ClankerExplorer.Models;

namespace ClankerExplorer.Services;

public class NetworkDiscoveryService
{
    public static NetworkDiscoveryService Instance { get; } = new();

    private readonly HashSet<string> _knownServers = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public NetworkDiscoveryService()
    {
        // Add known default network nodes
        if (!string.IsNullOrWhiteSpace(Environment.MachineName))
        {
            _knownServers.Add(Environment.MachineName);
        }
        _knownServers.Add("TRUENAS");
        _knownServers.Add("MSI");
    }

    public void AddCustomServer(string nameOrIp)
    {
        if (string.IsNullOrWhiteSpace(nameOrIp)) return;
        nameOrIp = nameOrIp.Trim().TrimStart('\\').TrimEnd('\\');
        if (!string.IsNullOrEmpty(nameOrIp))
        {
            _knownServers.Add(nameOrIp);
        }
    }

    public async Task<List<NetworkNode>> DiscoverComputersAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<NetworkNode>();
        var set = new HashSet<string>(_knownServers, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        if (OperatingSystem.IsWindows())
        {
            // Check mapped network drives for server names
            try
            {
                var (output, _, exitCode) = await FileSystemService.Instance.RunProcessWithTimeoutAsync(
                    "net.exe", new[] { "use" }, 1500, null, cancellationToken);

                if (exitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    var matches = Regex.Matches(output, @"\\\\([^\\]+)\\", RegexOptions.IgnoreCase);
                    foreach (Match m in matches)
                    {
                        var srv = m.Groups[1].Value.Trim();
                        if (!string.IsNullOrEmpty(srv)) set.Add(srv);
                    }
                }
            }
            catch { }

            // Try net view with 2000ms timeout
            try
            {
                var (output, _, exitCode) = await FileSystemService.Instance.RunProcessWithTimeoutAsync(
                    "net.exe", new[] { "view" }, 2000, null, cancellationToken);

                if (exitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith(@"\\"))
                        {
                            var srvName = trimmed.Substring(2).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                            if (!string.IsNullOrEmpty(srvName)) set.Add(srvName);
                        }
                    }
                }
            }
            catch { }
        }

        foreach (var server in set.OrderBy(s => s))
        {
            list.Add(new NetworkNode
            {
                Name = server,
                UncPath = $@"\\{server}",
                Type = "Computer"
            });
        }

        return list;
    }

    public async Task<List<NetworkNode>> GetSharesForComputerAsync(string computerNameOrIp, CancellationToken cancellationToken = default)
    {
        var shares = new List<NetworkNode>();
        computerNameOrIp = computerNameOrIp.TrimStart('\\');

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var (output, _, exitCode) = await FileSystemService.Instance.RunProcessWithTimeoutAsync(
                    "net.exe", new[] { "view", $@"\\{computerNameOrIp}" }, 2500, null, cancellationToken);

                if (exitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    bool headerPassed = false;

                    foreach (var line in lines)
                    {
                        if (line.Contains("---"))
                        {
                            headerPassed = true;
                            continue;
                        }

                        if (headerPassed)
                        {
                            if (line.Contains("The command completed successfully", StringComparison.OrdinalIgnoreCase))
                                break;

                            var parts = Regex.Split(line.Trim(), @"\s{2,}");
                            if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
                            {
                                string shareName = parts[0].Trim();
                                if (shareName.Equals("IPC$", StringComparison.OrdinalIgnoreCase)) continue;

                                shares.Add(new NetworkNode
                                {
                                    Name = shareName,
                                    UncPath = $@"\\{computerNameOrIp}\{shareName}",
                                    Type = "Share"
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error querying shares for {computerNameOrIp}: {ex.Message}");
            }

            // Fallback: If net view returned nothing, test common local share names
            if (shares.Count == 0 && computerNameOrIp.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists($@"\\{computerNameOrIp}\Users"))
                {
                    shares.Add(new NetworkNode
                    {
                        Name = "Users",
                        UncPath = $@"\\{computerNameOrIp}\Users",
                        Type = "Share"
                    });
                }
            }
        }

        return shares;
    }
}
