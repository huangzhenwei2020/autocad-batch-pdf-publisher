using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace CadArchSpec.CadTable
{
    public static class TianzhengTextCodec
    {
        private static readonly Regex TianzhengSuperscript = new Regex(@"\^U([^\^]*)\^U",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex UnicodeSuperscript = new Regex("[⁰¹²³⁴⁵⁶⁷⁸⁹⁺⁻⁼⁽⁾ⁿⁱ]+",
            RegexOptions.CultureInvariant);
        private static readonly IDictionary<char, char> ToSuperscript = new Dictionary<char, char>
        {
            ['0'] = '⁰', ['1'] = '¹', ['2'] = '²', ['3'] = '³', ['4'] = '⁴',
            ['5'] = '⁵', ['6'] = '⁶', ['7'] = '⁷', ['8'] = '⁸', ['9'] = '⁹',
            ['+'] = '⁺', ['-'] = '⁻', ['='] = '⁼', ['('] = '⁽', [')'] = '⁾',
            ['n'] = 'ⁿ', ['i'] = 'ⁱ'
        };
        private static readonly IDictionary<char, char> FromSuperscript = Reverse(ToSuperscript);

        public static bool ContainsTianzhengFormatting(string value)
        {
            return !string.IsNullOrEmpty(value) && TianzhengSuperscript.IsMatch(value);
        }

        public static string ToPlainText(string value)
        {
            if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
            return TianzhengSuperscript.Replace(value, match => SuperscriptOrOriginal(match.Groups[1].Value));
        }

        public static string ToAutoCadMText(string value)
        {
            if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
            var converted = TianzhengSuperscript.Replace(value,
                match => "{\\S" + EscapeStackText(match.Groups[1].Value) + "^;}");
            return UnicodeSuperscript.Replace(converted, match =>
                "{\\S" + FromSuperscriptText(match.Value) + "^;}");
        }

        public static string ToSuperscriptPlain(string value)
        {
            return SuperscriptOrOriginal(value ?? string.Empty);
        }

        private static string SuperscriptOrOriginal(string value)
        {
            var result = new StringBuilder(value.Length);
            foreach (var character in value)
            {
                char superscript;
                if (!ToSuperscript.TryGetValue(character, out superscript)) return value;
                result.Append(superscript);
            }
            return result.ToString();
        }

        private static string FromSuperscriptText(string value)
        {
            var result = new StringBuilder(value.Length);
            foreach (var character in value)
            {
                char normal;
                result.Append(FromSuperscript.TryGetValue(character, out normal) ? normal : character);
            }
            return EscapeStackText(result.ToString());
        }

        private static string EscapeStackText(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("{", "\\{")
                .Replace("}", "\\}").Replace(";", "\\;");
        }

        private static IDictionary<char, char> Reverse(IEnumerable<KeyValuePair<char, char>> source)
        {
            var result = new Dictionary<char, char>();
            foreach (var pair in source) result[pair.Value] = pair.Key;
            return result;
        }
    }
}
