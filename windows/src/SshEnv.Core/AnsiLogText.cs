using System.Text.RegularExpressions;

namespace SshEnv.Core;

public readonly record struct LogColor(byte Red, byte Green, byte Blue);

public readonly record struct LogTextRun(string Text, LogColor? Foreground);

public static partial class AnsiLogText
{
    private static readonly uint[] Palette =
    [
        0x747C89, 0xF07178, 0xA3BE8C, 0xEBCB8B, 0x81A1E9, 0xC792EA, 0x88C0D0, 0xD8DEE9,
        0x939AA5, 0xFF9398, 0xC0DAA2, 0xFFE2A6, 0xA5C3FF, 0xDAB2FF, 0xA8E5ED, 0xFFFFFF
    ];

    public static IEnumerable<LogTextRun> Parse(string text)
    {
        LogColor? foreground = null;
        var position = 0;
        foreach (Match sequence in EscapeSequence().Matches(text))
        {
            if (sequence.Index > position)
            {
                yield return new LogTextRun(text[position..sequence.Index], foreground);
            }

            var end = sequence.Index + sequence.Length;
            if (sequence.Length >= 3 && text[end - 1] == 'm')
            {
                foreground = ReadForeground(text[(sequence.Index + 2)..(end - 1)], foreground);
            }

            position = end;
        }

        if (position < text.Length)
        {
            yield return new LogTextRun(text[position..], foreground);
        }
    }

    private static LogColor? ReadForeground(string sequence, LogColor? foreground)
    {
        var parameters = sequence.Split(';');
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            if (parameter is "" or "0" or "39")
            {
                foreground = null;
            }
            else if (int.TryParse(parameter, out var code))
            {
                switch (code)
                {
                    case >= 30 and <= 37:
                        foreground = IndexedColor(code - 30);
                        break;
                    case >= 90 and <= 97:
                        foreground = IndexedColor(code - 90 + 8);
                        break;
                    case 38 or 48 or 58 when index + 1 < parameters.Length:
                        // Consume the whole color value, even for background and underline.
                        var count = parameters[++index] switch { "5" => 1, "2" => 3, _ => 0 };
                        if (code == 38 && count == 1 && index + 1 < parameters.Length &&
                            byte.TryParse(parameters[index + 1], out var colorIndex))
                        {
                            foreground = IndexedColor(colorIndex);
                        }
                        else if (code == 38 && count == 3 && index + 3 < parameters.Length &&
                                 byte.TryParse(parameters[index + 1], out var red) &&
                                 byte.TryParse(parameters[index + 2], out var green) &&
                                 byte.TryParse(parameters[index + 3], out var blue))
                        {
                            foreground = new LogColor(red, green, blue);
                        }

                        index += count;
                        break;
                }
            }
        }

        return foreground;
    }

    private static LogColor IndexedColor(int index)
    {
        if (index < Palette.Length)
        {
            var rgb = Palette[index];
            return new LogColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        if (index >= 232)
        {
            var gray = (byte)(8 + (index - 232) * 10);
            return new LogColor(gray, gray, gray);
        }

        var cubeIndex = index - 16;
        return new LogColor(CubeLevel(cubeIndex / 36), CubeLevel(cubeIndex / 6 % 6), CubeLevel(cubeIndex % 6));
    }

    private static byte CubeLevel(int level) => level == 0 ? (byte)0 : (byte)(55 + 40 * level);

    // A bounded line may end in the middle of a color sequence.
    [GeneratedRegex(@"\x1B(?:\[[0-?]*[@-~]?)?")]
    private static partial Regex EscapeSequence();
}
