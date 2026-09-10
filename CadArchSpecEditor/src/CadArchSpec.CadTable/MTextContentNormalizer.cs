using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CadArchSpec.CadTable
{
    public static class MTextContentNormalizer
    {
        public static string Normalize(string contents, string fallbackText = "")
        {
            if (string.IsNullOrEmpty(contents)) return NormalizeLines(fallbackText);
            var output = new StringBuilder(contents.Length);
            for (var index = 0; index < contents.Length; index++)
            {
                var current = contents[index];
                if (current == '{' || current == '}') continue;
                if (current != '\\')
                {
                    output.Append(current);
                    continue;
                }
                if (++index >= contents.Length) break;
                var command = contents[index];
                // Uppercase \P is a paragraph break. Lowercase \p starts a
                // paragraph-format command (for example \pxqc;) and must not
                // leak its parameters into the visible text.
                if (command == 'P') { output.Append('\n'); continue; }
                if (command == '~') { output.Append(' '); continue; }
                if (command == '\\' || command == '{' || command == '}') { output.Append(command); continue; }
                if ((command == 'U' || command == 'u') && index + 5 < contents.Length && contents[index + 1] == '+')
                {
                    int codePoint;
                    if (int.TryParse(contents.Substring(index + 2, 4), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out codePoint))
                    {
                        output.Append((char)codePoint);
                        index += 5;
                        continue;
                    }
                }
                if (command == 'S' || command == 's')
                {
                    var end = contents.IndexOf(';', index + 1);
                    if (end < 0) end = contents.Length;
                    var stacked = contents.Substring(index + 1, end - index - 1)
                        .Replace('#', '/').Replace('^', '/');
                    output.Append(stacked);
                    index = end;
                    continue;
                }
                if ("FfCcHhWwTtQqAap".IndexOf(command) >= 0)
                {
                    var end = contents.IndexOf(';', index + 1);
                    index = end < 0 ? contents.Length : end;
                    continue;
                }
                if (command == 'L' || command == 'l' || command == 'O' || command == 'o' ||
                    command == 'K' || command == 'k') continue;
                // Preserve the visible character for an unknown escape rather than
                // silently deleting user text from an unrecognised vendor code.
                output.Append(command);
            }
            var normalized = NormalizeLines(output.ToString());
            return normalized.Length == 0 ? NormalizeLines(fallbackText) : normalized;
        }

        private static string NormalizeLines(string value)
        {
            var lines = (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n')
                .Split(new[] { '\n' }, StringSplitOptions.None)
                .Select(line => line.Trim()).ToList();
            while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
            while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return string.Join(Environment.NewLine, lines);
        }
    }
}
