using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ClankerExplorer.Models;

namespace ClankerExplorer.Services;

public class NetworkDiscoveryService
{
    public static NetworkDiscoveryService Instance { get; } = new();

    private readonly HashSet<string> _knownServers = new(StringComparer.OrdinalIgnoreCase);

    public NetworkDiscoveryService()
    {
        // Add known default network nodes
        _knownServers.Add(Environment.MachineName); // 5900X
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

    public async Task<List<NetworkNode>> DiscoverComputersAsync()
    {
        return await Task.Run(() =>
        {
            var list = new List<NetworkNode>();
            var set = new HashSet<string>(_knownServers, StringComparer.OrdinalIgnoreCase);

            // Also check mapped network drives for server names
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "net.exe",
                    Arguments = "use",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(1500);
                    var matches = Regex.Matches(output, @"\\\\([^\\]+)\\", RegexOptions.IgnoreCase);
                    foreach (Match m in matches)
                    {
                        var srv = m.Groups[1].Value.Trim();
                        if (!string.IsNullOrEmpty(srv)) set.Add(srv);
                    }
                }
            }
            catch { }

            // Try net view in background (with short timeout)
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "net.exe",
                    Arguments = "view",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(2000);
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
        });
    }

    public async Task<List<NetworkNode>> GetSharesForComputerAsync(string computerNameOrIp)
    {
        return await Task.Run(() =>
        {
            var shares = new List<NetworkNode>();
            computerNameOrIp = computerNameOrIp.TrimStart('\\');

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "net.exe",
                    Arguments = $"view \"\\\\{computerNameOrIp}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(3000);

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
                                // Skip IPC$
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

            // Fallback: If net view returned nothing or restricted, attempt direct directory test for common share names if local
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

            return shares;
        });
    }
}
