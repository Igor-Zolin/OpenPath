using System.ComponentModel;
using System.Diagnostics;
using SshEnv.Core;

internal static class ApplicationCatalogTests
{
    public static async Task RunAsync()
    {
        using var current = Process.GetCurrentProcess();
        var expectedName = Path.GetFileName(Environment.ProcessPath)!;
        foreach (var pathReader in new Func<int, string?>[]
        {
            _ => null,
            _ => throw new Win32Exception(5),
            _ => throw new InvalidOperationException("Process exited."),
            _ => throw new UnauthorizedAccessException()
        })
        {
            var fallback = InstalledApplicationCatalog.ReadRunningApplication(current, pathReader);
            Assert(fallback?.ProcessName == expectedName && fallback.ExecutablePath == string.Empty,
                "A process without an accessible executable path disappeared or had the wrong executable name.");
            Assert(SingBoxConfigDirectRules.ParseProcessNames(fallback!.ProcessName).Single().Value == expectedName,
                "A process without a path could not be added as a portable exclusion.");
        }

        using var unavailable = new Process();
        Assert(InstalledApplicationCatalog.ReadRunningApplication(unavailable, _ => null) is null,
            "An unavailable process prevented the catalog from being read.");
        Console.WriteLine("PASS: inaccessible and exited processes do not break application discovery");

        var merged = InstalledApplicationCatalog.MergeApplications([
            new("Discord.exe", "Discord.exe", string.Empty),
            new("Discord", "discord.EXE", @"C:\Apps\Discord\Discord.exe"),
            new("Portable game", "Game-Win64-Shipping.exe", @"D:\Games\Game-Win64-Shipping.exe"),
            new("Game-Win64-Shipping.exe", "game-win64-shipping.EXE", string.Empty)
        ]);
        Assert(merged.Count == 2, "Installed and running copies were not deduplicated by executable name.");
        Assert(merged[0].DisplayName == "Discord" && merged[0].ExecutablePath.Length > 0,
            "Merging discarded a friendly registered application name or its executable path.");
        Assert(merged[1].ProcessName == "Game-Win64-Shipping.exe", "Merging discarded the game executable.");
        Assert(merged[1].ToString().Contains("Game-Win64-Shipping.exe"), "The picker hid the game's executable name.");
        Console.WriteLine("PASS: installed and running applications merge without duplicate exclusion targets");

        await VerifyPortableProcessDiscoveryAsync();

        // Check the actual Windows process enumeration as well as synthetic failure cases.
        var stopwatch = Stopwatch.StartNew();
        var detected = InstalledApplicationCatalog.FindInstalledApplications();
        Assert(detected.Any(application => application.ProcessName.Equals(expectedName, StringComparison.OrdinalIgnoreCase)),
            "The running test application was absent from the real Windows catalog.");
        var discordProcesses = Process.GetProcessesByName("Discord");
        try
        {
            if (discordProcesses.Length > 0)
            {
                Assert(detected.Count(application => application.ProcessName.Equals("Discord.exe", StringComparison.OrdinalIgnoreCase)) == 1,
                    "The running Discord instance was missing or appeared more than once.");
                Console.WriteLine("PASS: running Discord is discoverable as Discord.exe");
            }
        }
        finally
        {
            foreach (var process in discordProcesses)
            {
                process.Dispose();
            }
        }

        Console.WriteLine($"PASS: real Windows discovery found {detected.Count} unique applications in {stopwatch.ElapsedMilliseconds} ms");
    }

    private static async Task VerifyPortableProcessDiscoveryAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ssh-env-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // A copied apphost has no registry entry, just like a portable game.
            foreach (var file in Directory.GetFiles(AppContext.BaseDirectory)
                         .Where(file => Path.GetExtension(file) is ".dll" or ".json"))
            {
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            }

            var name = "PortableGame-Win64-Shipping-" + Guid.NewGuid().ToString("N") + ".exe";
            var executablePath = Path.Combine(directory, name);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "SshEnv.Core.Tests.exe"), executablePath);
            Assert(!InstalledApplicationCatalog.FindInstalledApplications().Any(application => application.ProcessName == name),
                "The test game was already registered before it started.");

            var children = new List<Process>();
            try
            {
                children.Add(StartChild(executablePath));
                children.Add(StartChild(executablePath));
                foreach (var process in children)
                {
                    Assert(await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "READY",
                        "The portable test process did not start.");
                }

                var applications = InstalledApplicationCatalog.FindInstalledApplications();
                var application = applications.SingleOrDefault(application => application.ProcessName == name);
                Assert(application?.ExecutablePath == executablePath,
                    "Refreshing did not discover an unregistered game executable at its real path.");
                var rule = SingBoxConfigDirectRules.ParseProcessNames(application!.ProcessName).Single();
                Assert(rule.Type == "process_name" && rule.Value == name,
                    "Selecting a portable game did not produce a process-name exclusion.");
                Console.WriteLine("PASS: refresh discovers a newly started portable game and deduplicates its instances");
            }
            finally
            {
                foreach (var process in children)
                {
                    try
                    {
                        process.StandardInput.Close();
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    finally
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                            await process.WaitForExitAsync();
                        }

                        process.Dispose();
                    }
                }
            }
        }
        finally
        {
            // Windows can briefly keep the executable mapped after process exit.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                    break;
                }
                catch (Exception exception) when (attempt < 20 && exception is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(100);
                }
            }
        }
    }

    private static Process StartChild(string executablePath)
    {
        return Process.Start(new ProcessStartInfo(executablePath, "--catalog-child")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
