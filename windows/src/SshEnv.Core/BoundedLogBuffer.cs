namespace SshEnv.Core;

public sealed class BoundedLogBuffer
{
    private const int DefaultMaxLines = 500;
    private const int DefaultMaxLineLength = 2_048;

    private readonly object gate = new();
    private readonly Queue<string> pendingLines = [];
    private readonly List<string> visibleLines = [];
    private readonly int maxLines;
    private readonly int maxLineLength;
    private int droppedPendingLines;
    private bool isEnabled = true;

    public BoundedLogBuffer(int maxLines = DefaultMaxLines, int maxLineLength = DefaultMaxLineLength)
    {
        if (maxLines <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLines), "Log line limit must be positive.");
        }

        if (maxLineLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLineLength), "Log line length limit must be positive.");
        }

        this.maxLines = maxLines;
        this.maxLineLength = maxLineLength;
    }

    public bool IsEnabled
    {
        get
        {
            lock (gate)
            {
                return isEnabled;
            }
        }
        set
        {
            lock (gate)
            {
                isEnabled = value;
            }
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            pendingLines.Clear();
            visibleLines.Clear();
            droppedPendingLines = 0;
        }
    }

    public void Enqueue(string message)
    {
        lock (gate)
        {
            if (!isEnabled)
            {
                return;
            }

            foreach (var line in SplitLines(message))
            {
                if (pendingLines.Count >= maxLines)
                {
                    pendingLines.Dequeue();
                    droppedPendingLines++;
                }

                pendingLines.Enqueue(TrimLine(line));
            }
        }
    }

    public bool TryDrain(out string text)
    {
        lock (gate)
        {
            if (pendingLines.Count == 0 && droppedPendingLines == 0)
            {
                text = string.Empty;
                return false;
            }

            while (pendingLines.Count > 0)
            {
                visibleLines.Add(pendingLines.Dequeue());
            }

            if (droppedPendingLines > 0)
            {
                visibleLines.Add($"[{DateTime.Now:HH:mm:ss}] Log throttled: dropped {droppedPendingLines} older lines because output is too fast.");
                droppedPendingLines = 0;
            }

            if (visibleLines.Count > maxLines)
            {
                visibleLines.RemoveRange(0, visibleLines.Count - maxLines);
            }

            text = string.Join(Environment.NewLine, visibleLines) + Environment.NewLine;
            return true;
        }
    }

    private static string[] SplitLines(string message)
    {
        return message.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
    }

    private string TrimLine(string line)
    {
        if (line.Length <= maxLineLength)
        {
            return line;
        }

        var prefix = line[..maxLineLength];
        var resetColor = prefix.Contains('\x1b') ? "\x1b[0m" : string.Empty;
        return prefix + resetColor + " ... [truncated]";
    }
}
