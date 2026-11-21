using System.Diagnostics;
using System.Text.Json;

namespace SshEnv.Core;

internal sealed class SessionRegistry
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private readonly string stateDirectory;
    private readonly string statePath;

    public SessionRegistry(string? stateDirectory = null)
    {
        this.stateDirectory = stateDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SshEnv", "runtime");
        statePath = Path.Combine(this.stateDirectory, "session.json");
    }

    public FileStream Acquire()
    {
        Directory.CreateDirectory(stateDirectory);
        try
        {
            return File.Open(Path.Combine(stateDirectory, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception) when ((exception.HResult & 0xffff) is ErrorSharingViolation or ErrorLockViolation)
        {
            throw new InvalidOperationException("Another OpenPath instance is already using the tunnel. Disconnect it before starting a new session.", exception);
        }
    }

    public void Save(IReadOnlyCollection<int> processIds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        var state = new SessionState(processIds.ToArray());
        File.WriteAllText(statePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void CleanupPreviousSession(Action<string>? log)
    {
        if (!File.Exists(statePath))
        {
            return;
        }

        try
        {
            var state = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(statePath));
            foreach (var processId in state?.ProcessIds ?? [])
            {
                TryKillProcess(processId, log);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            log?.Invoke($"Could not read previous session state: {exception.Message}");
        }
        finally
        {
            Clear();
        }
    }

    private static void TryKillProcess(int processId, Action<string>? log)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            if (process.ProcessName is not ("ssh" or "tun2socks" or "sing-box"))
            {
                return;
            }

            log?.Invoke($"Stopping leftover process {process.ProcessName} ({processId}) from previous OpenPath session.");
            process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            log?.Invoke($"Could not stop leftover process {processId}: {exception.Message}");
        }
    }

    private sealed record SessionState(int[] ProcessIds);
}
