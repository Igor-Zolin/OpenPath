namespace SshEnv.Core;

internal sealed class DependencyDownloader(HttpClient client, TimeSpan? timeout = null)
{
    public async Task DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(90));
        var complete = false;
        try
        {
            // HttpClient.Timeout only covers headers with ResponseHeadersRead. Keep the deadline
            // active through the body copy as well, including a server that stops sending bytes.
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
            await using var output = File.Create(destination);
            await input.CopyToAsync(output, deadline.Token);
            await output.FlushAsync(deadline.Token);
            if (output.Length == 0 ||
                (response.Content.Headers.ContentLength is long length && output.Length != length))
            {
                throw new InvalidDataException("Download is empty or incomplete.");
            }

            complete = true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Download timed out: {url}. Retry or use the EXE built by publish-winui.ps1, which includes offline dependencies.");
        }
        catch (HttpRequestException exception)
        {
            throw new HttpRequestException($"Could not download {url}: {exception.GetBaseException().Message}", exception);
        }
        finally
        {
            if (!complete)
            {
                try { File.Delete(destination); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
