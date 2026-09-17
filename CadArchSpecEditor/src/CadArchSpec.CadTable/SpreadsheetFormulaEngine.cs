using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CadArchSpec.CadTable
{
    public static class SpreadsheetFormulaEngine
    {
        public static string[,] Calculate(string[,] contents)
        {
            if (contents == null) throw new ArgumentNullException(nameof(contents));
            var rows = contents.GetLength(0);
            var columns = contents.GetLength(1);
            var results = new string[rows, columns];
            var states = new byte[rows, columns];
            var values = new Value[rows, columns];

            Value EvaluateCell(int row, int column)
            {
                if (row < 0 || column < 0 || row >= rows || column >= columns)
                    return Value.Error("#REF!");
                if (states[row, column] == 2) return values[row, column];
                if (states[row, column] == 1) return Value.Error("#CYCLE!");
                states[row, column] = 1;
                var raw = contents[row, column] ?? string.Empty;
                Value value;
                if (!raw.TrimStart().StartsWith("=", StringComparison.Ordinal))
                    value = Value.FromText(raw);
                else
                {
                    try
                    {
                        value = new Parser(raw.TrimStart().Substring(1), EvaluateCell).Parse();
                    }
                    catch (FormulaParseException ex)
                    {
                        value = Value.Error(ex.Message);
                    }
                    catch
                    {
                        value = Value.Error("#VALUE!");
                    }
                }
                states[row, column] = 2;
                values[row, column] = value;
                return value;
            }

            for (var row = 0; row < rows; row++)
                for (var column = 0; column < columns; column++)
                    results[row, column] = EvaluateCell(row, column).DisplayText;
            return results;
        }

        public static string CellAddress(int row, int column)
        {
            if (row < 0 || column < 0) throw new ArgumentOutOfRangeException();
            var letters = string.Empty;
            for (var value = column + 1; value > 0; value /= 26)
                letters = (char)('A' + (value - 1) % 26) + letters;
            return letters + (row + 1).ToString(CultureInfo.InvariantCulture);
        }

        private sealed class Parser
        {
            private readonly string _text;
            private readonly Func<int, int, Value> _cell;
            private int _position;

            public Parser(string text, Func<int, int, Value> cell)
            {
                _text = text ?? string.Empty;
                _cell = cell;
            }

            public Value Parse()
            {
                var result = ParseComparison();
                SkipWhite();
                if (_position != _text.Length) throw new FormulaParseException("#VALUE!");
                return result;
            }

            private Value ParseComparison()
            {
                var left = ParseAddSubtract();
                while (true)
                {
                    SkipWhite();
                    var op = ReadAny("<=", ">=", "<>", "=", "<", ">");
                    if (op == null) return left;
                    var right = ParseAddSubtract();
                    if (left.IsError) return left;
                    if (right.IsError) return right;
                    var comparison = Compare(left, right);
                    left = Value.Boolean(op == "=" ? comparison == 0 : op == "<>" ? comparison != 0 :
                        op == "<" ? comparison < 0 : op == ">" ? comparison > 0 :
                        op == "<=" ? comparison <= 0 : comparison >= 0);
                }
            }

            private Value ParseAddSubtract()
            {
                var left = ParseMultiplyDivide();
                while (true)
                {
                    SkipWhite();
                    if (Take('+')) left = Arithmetic(left, ParseMultiplyDivide(), (a, b) => a + b);
                    else if (Take('-')) left = Arithmetic(left, ParseMultiplyDivide(), (a, b) => a - b);
                    else return left;
                }
            }

            private Value ParseMultiplyDivide()
            {
                var left = ParsePower();
                while (true)
                {
                    SkipWhite();
                    if (Take('*')) left = Arithmetic(left, ParsePower(), (a, b) => a * b);
                    else if (Take('/'))
                    {
                        var right = ParsePower();
                        double divisor;
                        if (!right.TryNumber(out divisor)) left = right.IsError ? right : Value.Error("#VALUE!");
                        else if (Math.Abs(divisor) < 1e-15) left = Value.Error("#DIV/0!");
                        else left = Arithmetic(left, right, (a, b) => a / b);
                    }
                    else return left;
                }
            }

            private Value ParsePower()
            {
                var left = ParseUnary();
                SkipWhite();
                return Take('^') ? Arithmetic(left, ParsePower(), Math.Pow) : left;
            }

            private Value ParseUnary()
            {
                SkipWhite();
                if (Take('+')) return ParseUnary();
                if (Take('-')) return Arithmetic(Value.Number(0), ParseUnary(), (a, b) => a - b);
                return ParsePrimary();
            }

            private Value ParsePrimary()
            {
                SkipWhite();
                if (Take('('))
                {
                    var value = ParseComparison();
                    Require(')');
                    return value;
                }
                if (Peek() == '"') return Value.Text(ReadString());
                if (char.IsDigit(Peek()) || Peek() == '.') return Value.Number(ReadNumber());
                var identifier = ReadIdentifier();
                if (string.IsNullOrEmpty(identifier)) throw new FormulaParseException("#VALUE!");
                SkipWhite();
                if (Take('(')) return ParseFunction(identifier);
                int row;
                int column;
                if (TryCellAddress(identifier, out row, out column))
                {
                    SkipWhite();
                    if (Take(':'))
                    {
                        var end = ReadIdentifier();
                        int endRow;
                        int endColumn;
                        if (!TryCellAddress(end, out endRow, out endColumn)) throw new FormulaParseException("#REF!");
                        var range = new List<Value>();
                        for (var currentRow = Math.Min(row, endRow); currentRow <= Math.Max(row, endRow); currentRow++)
                            for (var currentColumn = Math.Min(column, endColumn); currentColumn <= Math.Max(column, endColumn); currentColumn++)
                                range.Add(_cell(currentRow, currentColumn));
                        return Value.Range(range);
                    }
                    return _cell(row, column);
                }
                if (identifier.Equals("TRUE", StringComparison.OrdinalIgnoreCase)) return Value.Boolean(true);
                if (identifier.Equals("FALSE", StringComparison.OrdinalIgnoreCase)) return Value.Boolean(false);
                throw new FormulaParseException("#NAME?");
            }

            private Value ParseFunction(string name)
            {
                var arguments = new List<Value>();
                SkipWhite();
                if (!Take(')'))
                {
                    while (true)
                    {
                        arguments.Add(ParseComparison());
                        SkipWhite();
                        if (Take(')')) break;
                        Require(',');
                    }
                }
                var values = arguments.SelectMany(value => value.Items ?? new[] { value }).ToList();
                var upper = name.ToUpperInvariant();
                if (upper == "IF")
                {
                    if (arguments.Count < 2 || arguments.Count > 3) return Value.Error("#VALUE!");
                    return arguments[0].IsTrue ? arguments[1] : arguments.Count == 3 ? arguments[2] : Value.Boolean(false);
                }
                if (values.Any(value => value.IsError)) return values.First(value => value.IsError);
                var numbers = values.Select(value => { double number; return value.TryNumber(out number) ? (double?)number : null; })
                    .Where(value => value.HasValue).Select(value => value.Value).ToList();
                if (upper == "SUM") return Value.Number(numbers.Sum());
                if (upper == "COUNT") return Value.Number(numbers.Count);
                if (upper == "AVERAGE") return numbers.Count == 0 ? Value.Error("#DIV/0!") : Value.Number(numbers.Average());
                if (upper == "MIN") return numbers.Count == 0 ? Value.Number(0) : Value.Number(numbers.Min());
                if (upper == "MAX") return numbers.Count == 0 ? Value.Number(0) : Value.Number(numbers.Max());
                if (upper == "ABS") return arguments.Count == 1 ? UnaryNumber(arguments[0], Math.Abs) : Value.Error("#VALUE!");
                if (upper == "ROUND")
                {
                    double number;
                    double digits;
                    return arguments.Count == 2 && arguments[0].TryNumber(out number) && arguments[1].TryNumber(out digits)
                        ? Value.Number(Math.Round(number, Math.Max(0, Math.Min(15, (int)digits)), MidpointRounding.AwayFromZero))
                        : Value.Error("#VALUE!");
                }
                return Value.Error("#NAME?");
            }

            private static Value UnaryNumber(Value value, Func<double, double> operation)
            {
                double number;
                return value.TryNumber(out number) ? Value.Number(operation(number)) : value.IsError ? value : Value.Error("#VALUE!");
            }

            private static Value Arithmetic(Value left, Value right, Func<double, double, double> operation)
            {
                if (left.IsError) return left;
                if (right.IsError) return right;
                double first;
                double second;
                return left.TryNumber(out first) && right.TryNumber(out second)
                    ? Value.Number(operation(first, second)) : Value.Error("#VALUE!");
            }

            private static int Compare(Value left, Value right)
            {
                double first;
                double second;
                if (left.TryNumber(out first) && right.TryNumber(out second)) return first.CompareTo(second);
                return string.Compare(left.DisplayText, right.DisplayText, StringComparison.OrdinalIgnoreCase);
            }

            private static bool TryCellAddress(string value, out int row, out int column)
            {
                row = -1;
                column = -1;
                value = (value ?? string.Empty).Replace("$", string.Empty);
                var split = 0;
                while (split < value.Length && char.IsLetter(value[split])) split++;
                if (split == 0 || split == value.Length) return false;
                int oneBasedRow;
                if (!int.TryParse(value.Substring(split), NumberStyles.None, CultureInfo.InvariantCulture, out oneBasedRow) || oneBasedRow < 1) return false;
                var oneBasedColumn = 0;
                for (var index = 0; index < split; index++)
                {
                    var letter = char.ToUpperInvariant(value[index]);
                    if (letter < 'A' || letter > 'Z') return false;
                    oneBasedColumn = checked(oneBasedColumn * 26 + letter - 'A' + 1);
                }
                row = oneBasedRow - 1;
                column = oneBasedColumn - 1;
                return true;
            }

            private string ReadIdentifier()
            {
                SkipWhite();
                var start = _position;
                while (_position < _text.Length && (char.IsLetterOrDigit(_text[_position]) || _text[_position] == '_' || _text[_position] == '$')) _position++;
                return _text.Substring(start, _position - start);
            }

            private string ReadString()
            {
                Require('"');
                var result = string.Empty;
                while (_position < _text.Length)
                {
                    var character = _text[_position++];
                    if (character == '"')
                    {
                        if (_position < _text.Length && _text[_position] == '"') { result += '"'; _position++; continue; }
                        return result;
                    }
                    result += character;
                }
                throw new FormulaParseException("#VALUE!");
            }

            private double ReadNumber()
            {
                var start = _position;
                while (_position < _text.Length && (char.IsDigit(_text[_position]) || _text[_position] == '.' ||
                    _text[_position] == 'e' || _text[_position] == 'E' || _text[_position] == '+' && _position > start && (_text[_position - 1] == 'e' || _text[_position - 1] == 'E') ||
                    _text[_position] == '-' && _position > start && (_text[_position - 1] == 'e' || _text[_position - 1] == 'E'))) _position++;
                double value;
                if (!double.TryParse(_text.Substring(start, _position - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    throw new FormulaParseException("#VALUE!");
                return value;
            }

            private string ReadAny(params string[] values)
            {
                foreach (var value in values)
                    if (_text.Substring(_position).StartsWith(value, StringComparison.Ordinal)) { _position += value.Length; return value; }
                return null;
            }

            private char Peek() { return _position < _text.Length ? _text[_position] : '\0'; }
            private bool Take(char value) { if (Peek() != value) return false; _position++; return true; }
            private void Require(char value) { SkipWhite(); if (!Take(value)) throw new FormulaParseException("#VALUE!"); }
            private void SkipWhite() { while (_position < _text.Length && char.IsWhiteSpace(_text[_position])) _position++; }
        }

        private sealed class Value
        {
            private Value(string text, double? number, bool? boolean, string error, IReadOnlyList<Value> items)
            {
                TextValue = text;
                NumberValue = number;
                BooleanValue = boolean;
                ErrorValue = error;
                Items = items;
            }

            public string TextValue { get; }
            public double? NumberValue { get; }
            public bool? BooleanValue { get; }
            public string ErrorValue { get; }
            public IReadOnlyList<Value> Items { get; }
            public bool IsError { get { return !string.IsNullOrEmpty(ErrorValue); } }
            public bool IsTrue { get { double number; return BooleanValue == true || TryNumber(out number) && Math.Abs(number) > 1e-15 || !string.IsNullOrEmpty(TextValue); } }
            public string DisplayText
            {
                get
                {
                    if (IsError) return ErrorValue;
                    if (NumberValue.HasValue) return NumberValue.Value.ToString("G15", CultureInfo.InvariantCulture);
                    if (BooleanValue.HasValue) return BooleanValue.Value ? "TRUE" : "FALSE";
                    return TextValue ?? string.Empty;
                }
            }

            public bool TryNumber(out double result)
            {
                if (NumberValue.HasValue) { result = NumberValue.Value; return true; }
                if (BooleanValue.HasValue) { result = BooleanValue.Value ? 1d : 0d; return true; }
                return double.TryParse(TextValue ?? string.Empty, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
            }

            public static Value FromText(string text)
            {
                double number;
                return double.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                    ? Number(number) : Text(text ?? string.Empty);
            }
            public static Value Text(string text) { return new Value(text, null, null, null, null); }
            public static Value Number(double number) { return new Value(null, number, null, null, null); }
            public static Value Boolean(bool value) { return new Value(null, null, value, null, null); }
            public static Value Error(string error) { return new Value(null, null, null, error, null); }
            public static Value Range(IReadOnlyList<Value> values) { return new Value(null, null, null, null, values); }
        }

        private sealed class FormulaParseException : Exception
        {
            public FormulaParseException(string message) : base(message) { }
        }
    }
}
