using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace SshEnv.Core;

public sealed record WintunAdapter(string InstanceId, string Description, string Status);

public sealed class WintunRepairService
{
    public async Task<IReadOnlyList<WintunAdapter>> FindTun2SocksAdaptersAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunProcessAsync("pnputil", ["/enum-devices", "/class", "Net", "/format", "csv"], cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"pnputil enum failed with code {result.ExitCode}: {result.CombinedOutput.Trim()}");
        }

        return ParseAdapters(result.CombinedOutput)
            .Where(adapter =>
                adapter.InstanceId.StartsWith(@"SWD\Wintun\", StringComparison.OrdinalIgnoreCase) &&
                adapter.Description.Equals("tun2socks Tunnel", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public async Task<int> RemoveTun2SocksAdaptersAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!IsAdministrator())
        {
            throw new InvalidOperationException("Repair requires running OpenPath as Administrator.");
        }

        var adapters = await FindTun2SocksAdaptersAsync(cancellationToken);
        if (adapters.Count == 0)
        {
            progress?.Report("No tun2socks Wintun adapters found.");
            return 0;
        }

        var removed = 0;
        for (var index = 0; index < adapters.Count; index++)
        {
            var adapter = adapters[index];
            progress?.Report($"Removing tun2socks Wintun adapter {index + 1} of {adapters.Count}.");
            var result = await RunProcessAsync("pnputil", ["/remove-device", adapter.InstanceId, "/force"], cancellationToken);
            if (result.ExitCode == 0)
            {
                removed++;
                continue;
            }

            progress?.Report($"Error: failed to remove Wintun adapter {adapter.InstanceId}: {result.CombinedOutput.Trim()}");
        }

        progress?.Report($"Removed {removed} of {adapters.Count} tun2socks Wintun adapters.");
        return removed;
    }

    private static IReadOnlyList<WintunAdapter> ParseAdapters(string output)
    {
        var adapters = new List<WintunAdapter>();
        var lines = output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
        {
            return adapters;
        }

        var header = ParseCsvLine(lines[0]);
        var instanceIdIndex = header.FindIndex(value => value.Equals("InstanceId", StringComparison.OrdinalIgnoreCase));
        var descriptionIndex = header.FindIndex(value => value.Equals("DeviceDescription", StringComparison.OrdinalIgnoreCase));
        var statusIndex = header.FindIndex(value => value.Equals("Status", StringComparison.OrdinalIgnoreCase));

        if (instanceIdIndex < 0 || descriptionIndex < 0 || statusIndex < 0)
        {
            return adapters;
        }

        foreach (var line in lines.Skip(1))
        {
            var fields = ParseCsvLine(line);
            if (fields.Count <= Math.Max(instanceIdIndex, Math.Max(descriptionIndex, statusIndex)))
            {
                continue;
            }

            adapters.Add(new WintunAdapter(fields[instanceIdIndex], fields[descriptionIndex], fields[statusIndex]));
        }

        return adapters;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (inQuotes && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (character == ',' && !inQuotes)
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        values.Add(current.ToString());
        return values;
    }

    private static async Task<CommandResult> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new CommandResult(process.ExitCode, output, error);
    }

    private static bool IsAdministrator()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error)
    {
        public string CombinedOutput => string.IsNullOrWhiteSpace(Error) ? Output : Error;
    }
}
