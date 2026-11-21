using System.Diagnostics;
using System.Net.Sockets;

namespace SshEnv.WinUI;

internal static class SshLatencyTester
{
    public static async Task<TimeSpan> MeasureAsync(string host, int port, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var socket = new TcpClient();
        var start = Stopwatch.GetTimestamp();
        try
        {
            await socket.ConnectAsync(host, port, timeout.Token);
            return Stopwatch.GetElapsedTime(start);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException($"TCP connection to {host}:{port} timed out.");
        }
    }
}
