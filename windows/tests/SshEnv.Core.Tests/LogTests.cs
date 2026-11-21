using SshEnv.Core;

internal static class LogTests
{
    public static void Run()
    {
        var runs = AnsiLogText.Parse("[10:29:56] \u001b[31mERROR\u001b[0m [\u001b[38;5;100m1744000596\u001b[0m]").ToArray();
        Require(string.Concat(runs.Select(run => run.Text)) == "[10:29:56] ERROR [1744000596]",
            "Color escapes appeared in visible or copied log text.");
        Require(runs[1].Foreground is { Red: > 200, Green: < 150 } &&
                runs[3].Foreground == new LogColor(135, 135, 0) &&
                runs[2].Foreground is null && runs[4].Foreground is null,
            "Sing-box severity or connection ID colors were lost, or a reset leaked color.");

        var buffer = new BoundedLogBuffer(maxLines: 2);
        buffer.Enqueue("Visible");
        Require(buffer.TryDrain(out _), "Log did not drain while enabled.");
        buffer.IsEnabled = false;
        buffer.Enqueue("While disabled");
        Require(!buffer.TryDrain(out _), "Disabled log accepted a new entry.");
        buffer.IsEnabled = true;
        buffer.Enqueue("After re-enabling");
        Require(buffer.TryDrain(out var text) &&
                text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                    .SequenceEqual(["Visible", "After re-enabling"]),
            "Disabling logging changed history or queued a disabled entry.");

        buffer.Enqueue("First pending");
        buffer.Enqueue("Second pending");
        buffer.Enqueue("Third pending");
        buffer.Clear();
        Require(!buffer.TryDrain(out _), "Cleared pending entries or drop notices reappeared.");
        buffer.Enqueue("After clearing");
        Require(buffer.TryDrain(out text) && text == "After clearing" + Environment.NewLine,
            "Cleared history reappeared or clearing stopped log capture.");

        var shortBuffer = new BoundedLogBuffer(maxLineLength: 16);
        shortBuffer.Enqueue("\u001b[31mERROR \u001b[38;5;100m1744000596\u001b[0m");
        shortBuffer.Enqueue("Next");
        Require(shortBuffer.TryDrain(out text), "Truncated log did not drain.");
        var truncatedRuns = AnsiLogText.Parse(text).ToArray();
        Require(truncatedRuns[0].Text == "ERROR " &&
                truncatedRuns[1].Text == $" ... [truncated]{Environment.NewLine}Next{Environment.NewLine}" &&
                truncatedRuns[1].Foreground is null,
            "A truncated color sequence leaked control text or color into subsequent log entries.");
        Console.WriteLine("PASS: ANSI log colors, truncation, capture toggle, and clearing");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
