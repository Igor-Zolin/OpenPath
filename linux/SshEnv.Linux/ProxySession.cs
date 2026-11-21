using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace SshEnv.Linux;

internal static class ProxySession
{
    public static async Task<int> RunAsync(
        string executable, IEnumerable<string> arguments, int port,
        Action<string> log, Action ready, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckPortAvailable(port);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.OutputDataReceived += (_, args) => ForwardOutput(args.Data, log);
        process.ErrorDataReceived += (_, args) => ForwardOutput(args.Data, log);
        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException($"Could not start {executable}. Install it and make sure it is available on PATH.", exception);
        }

        try
        {
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (await WaitForListenerAsync(process, port, cancellationToken))
            {
                ready();
            }

            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // The child exited between the status check and the kill.
                }
            }

            await process.WaitForExitAsync();
        }
    }

    private static void ForwardOutput(string? line, Action<string> log)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            log(line);
        }
    }

    private static void CheckPortAvailable(int port)
    {
        using var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
        }
        catch (SocketException exception)
        {
            throw new InvalidOperationException($"Could not bind 127.0.0.1:{port}. Check that the port is available.", exception);
        }
    }

    private static async Task<bool> WaitForListenerAsync(Process process, int port, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (!process.HasExited && elapsed.Elapsed < TimeSpan.FromSeconds(15))
        {
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
                return !process.HasExited;
            }
            catch (SocketException)
            {
                await Task.Delay(200, cancellationToken);
            }
        }

        if (process.HasExited)
        {
            return false;
        }

        throw new TimeoutException($"The proxy did not start listening on 127.0.0.1:{port} within 15 seconds.");
    }
}
