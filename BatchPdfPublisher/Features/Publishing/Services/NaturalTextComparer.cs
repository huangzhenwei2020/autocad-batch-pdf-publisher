using System;

namespace BatchPdfPublisher.Services
{
    internal sealed class NaturalTextComparer : System.Collections.Generic.IComparer<string>
    {
        public static readonly NaturalTextComparer Instance = new NaturalTextComparer();

        public int Compare(string left, string right)
        {
            left = left ?? string.Empty;
            right = right ?? string.Empty;
            var leftIndex = 0;
            var rightIndex = 0;
            while (leftIndex < left.Length && rightIndex < right.Length)
            {
                if (char.IsDigit(left[leftIndex]) && char.IsDigit(right[rightIndex]))
                {
                    var leftStart = leftIndex;
                    var rightStart = rightIndex;
                    while (leftIndex < left.Length && char.IsDigit(left[leftIndex])) leftIndex++;
                    while (rightIndex < right.Length && char.IsDigit(right[rightIndex])) rightIndex++;
                    var leftDigits = left.Substring(leftStart, leftIndex - leftStart).TrimStart('0');
                    var rightDigits = right.Substring(rightStart, rightIndex - rightStart).TrimStart('0');
                    if (leftDigits.Length == 0) leftDigits = "0";
                    if (rightDigits.Length == 0) rightDigits = "0";
                    var lengthResult = leftDigits.Length.CompareTo(rightDigits.Length);
                    if (lengthResult != 0) return lengthResult;
                    var digitResult = string.CompareOrdinal(leftDigits, rightDigits);
                    if (digitResult != 0) return digitResult;
                    continue;
                }
                var leftCharacter = char.ToUpperInvariant(left[leftIndex++]);
                var rightCharacter = char.ToUpperInvariant(right[rightIndex++]);
                var characterResult = leftCharacter.CompareTo(rightCharacter);
                if (characterResult != 0) return characterResult;
            }
            return left.Length.CompareTo(right.Length);
        }
    }
}
