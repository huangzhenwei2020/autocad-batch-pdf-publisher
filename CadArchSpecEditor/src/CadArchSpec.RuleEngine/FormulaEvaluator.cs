using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CadArchSpec.RuleEngine
{
    public sealed class FormulaEvaluator
    {
        public decimal Evaluate(string formula, IReadOnlyDictionary<string, decimal> variables)
        {
            if (string.IsNullOrWhiteSpace(formula)) throw new ArgumentException("公式不能为空。", nameof(formula));
            var parser = new Parser(formula, variables ?? new Dictionary<string, decimal>());
            var result = parser.ParseExpression();
            parser.Expect(TokenKind.End);
            return result;
        }

        private sealed class Parser
        {
            private readonly Lexer _lexer;
            private readonly IReadOnlyDictionary<string, decimal> _variables;
            private Token _current;

            public Parser(string formula, IReadOnlyDictionary<string, decimal> variables)
            {
                _lexer = new Lexer(formula);
                _variables = variables;
                _current = _lexer.Next();
            }

            public decimal ParseExpression()
            {
                return ParseComparison();
            }

            public void Expect(TokenKind kind)
            {
                if (_current.Kind != kind) throw Error("需要 " + kind + "，实际为 " + _current.Text + "。");
                Next();
            }

            private decimal ParseComparison()
            {
                var left = ParseAdditive();
                while (_current.Kind == TokenKind.Equal || _current.Kind == TokenKind.NotEqual ||
                       _current.Kind == TokenKind.Less || _current.Kind == TokenKind.LessOrEqual ||
                       _current.Kind == TokenKind.Greater || _current.Kind == TokenKind.GreaterOrEqual)
                {
                    var operation = _current.Kind;
                    Next();
                    var right = ParseAdditive();
                    switch (operation)
                    {
                        case TokenKind.Equal: left = left == right ? 1m : 0m; break;
                        case TokenKind.NotEqual: left = left != right ? 1m : 0m; break;
                        case TokenKind.Less: left = left < right ? 1m : 0m; break;
                        case TokenKind.LessOrEqual: left = left <= right ? 1m : 0m; break;
                        case TokenKind.Greater: left = left > right ? 1m : 0m; break;
                        case TokenKind.GreaterOrEqual: left = left >= right ? 1m : 0m; break;
                    }
                }
                return left;
            }

            private decimal ParseAdditive()
            {
                var value = ParseMultiplicative();
                while (_current.Kind == TokenKind.Plus || _current.Kind == TokenKind.Minus)
                {
                    var operation = _current.Kind;
                    Next();
                    var right = ParseMultiplicative();
                    value = operation == TokenKind.Plus ? value + right : value - right;
                }
                return value;
            }

            private decimal ParseMultiplicative()
            {
                var value = ParseUnary();
                while (_current.Kind == TokenKind.Multiply || _current.Kind == TokenKind.Divide)
                {
                    var operation = _current.Kind;
                    Next();
                    var right = ParseUnary();
                    if (operation == TokenKind.Divide && right == 0m) throw Error("公式不能除以零。");
                    value = operation == TokenKind.Multiply ? value * right : value / right;
                }
                return value;
            }

            private decimal ParseUnary()
            {
                if (_current.Kind == TokenKind.Plus) { Next(); return ParseUnary(); }
                if (_current.Kind == TokenKind.Minus) { Next(); return -ParseUnary(); }
                return ParsePrimary();
            }

            private decimal ParsePrimary()
            {
                if (_current.Kind == TokenKind.Number)
                {
                    var value = _current.Number;
                    Next();
                    return value;
                }

                if (_current.Kind == TokenKind.Identifier)
                {
                    var name = _current.Text;
                    Next();
                    if (_current.Kind == TokenKind.LeftParenthesis) return ParseFunction(name);
                    decimal value;
                    if (!_variables.TryGetValue(name, out value)) throw Error("公式引用了未知字段：" + name);
                    return value;
                }

                if (_current.Kind == TokenKind.LeftParenthesis)
                {
                    Next();
                    var value = ParseExpression();
                    Expect(TokenKind.RightParenthesis);
                    return value;
                }

                throw Error("无法识别的公式内容：" + _current.Text);
            }

            private decimal ParseFunction(string name)
            {
                Expect(TokenKind.LeftParenthesis);
                var arguments = new List<decimal>();
                if (_current.Kind != TokenKind.RightParenthesis)
                {
                    while (true)
                    {
                        arguments.Add(ParseExpression());
                        if (_current.Kind != TokenKind.Comma) break;
                        Next();
                    }
                }
                Expect(TokenKind.RightParenthesis);
                return ExecuteFunction(name, arguments);
            }

            private decimal ExecuteFunction(string name, IList<decimal> arguments)
            {
                switch (name.ToUpperInvariant())
                {
                    case "SUM":
                        RequireAtLeast(name, arguments, 1);
                        return arguments.Sum();
                    case "MIN":
                        RequireAtLeast(name, arguments, 1);
                        return arguments.Min();
                    case "MAX":
                        RequireAtLeast(name, arguments, 1);
                        return arguments.Max();
                    case "COUNT":
                        return arguments.Count;
                    case "ABS":
                        RequireExactly(name, arguments, 1);
                        return Math.Abs(arguments[0]);
                    case "ROUND":
                        if (arguments.Count != 1 && arguments.Count != 2) throw Error("ROUND 需要 1 或 2 个参数。");
                        var digits = arguments.Count == 2 ? decimal.ToInt32(arguments[1]) : 0;
                        if (digits < 0 || digits > 28) throw Error("ROUND 的小数位必须在 0—28 之间。");
                        return Math.Round(arguments[0], digits, MidpointRounding.AwayFromZero);
                    case "IF":
                        RequireExactly(name, arguments, 3);
                        return arguments[0] != 0m ? arguments[1] : arguments[2];
                    default:
                        throw Error("不允许使用函数：" + name);
                }
            }

            private static void RequireAtLeast(string name, ICollection<decimal> arguments, int count)
            {
                if (arguments.Count < count) throw new FormulaException(name + " 至少需要 " + count + " 个参数。");
            }

            private static void RequireExactly(string name, ICollection<decimal> arguments, int count)
            {
                if (arguments.Count != count) throw new FormulaException(name + " 需要 " + count + " 个参数。");
            }

            private void Next()
            {
                _current = _lexer.Next();
            }

            private FormulaException Error(string message)
            {
                return new FormulaException(message + " 位置：" + _lexer.Position);
            }
        }

        private sealed class Lexer
        {
            private readonly string _source;
            private int _position;

            public Lexer(string source)
            {
                _source = source;
            }

            public int Position { get { return _position; } }

            public Token Next()
            {
                while (_position < _source.Length && char.IsWhiteSpace(_source[_position])) _position++;
                if (_position >= _source.Length) return new Token(TokenKind.End, string.Empty);

                var character = _source[_position];
                if (char.IsDigit(character) || character == '.') return ReadNumber();
                if (char.IsLetter(character) || character == '_' || character > 127) return ReadIdentifier();
                _position++;
                switch (character)
                {
                    case '+': return new Token(TokenKind.Plus, "+");
                    case '-': return new Token(TokenKind.Minus, "-");
                    case '*': return new Token(TokenKind.Multiply, "*");
                    case '/': return new Token(TokenKind.Divide, "/");
                    case '(': return new Token(TokenKind.LeftParenthesis, "(");
                    case ')': return new Token(TokenKind.RightParenthesis, ")");
                    case ',': return new Token(TokenKind.Comma, ",");
                    case '=':
                        if (Take('=')) return new Token(TokenKind.Equal, "==");
                        return new Token(TokenKind.Equal, "=");
                    case '!':
                        if (Take('=')) return new Token(TokenKind.NotEqual, "!=");
                        break;
                    case '<':
                        if (Take('=')) return new Token(TokenKind.LessOrEqual, "<=");
                        return new Token(TokenKind.Less, "<");
                    case '>':
                        if (Take('=')) return new Token(TokenKind.GreaterOrEqual, ">=");
                        return new Token(TokenKind.Greater, ">");
                }
                throw new FormulaException("公式包含不允许的字符：" + character + "，位置：" + (_position - 1));
            }

            private Token ReadNumber()
            {
                var start = _position;
                var hasDot = false;
                while (_position < _source.Length)
                {
                    var character = _source[_position];
                    if (char.IsDigit(character)) { _position++; continue; }
                    if (character == '.' && !hasDot) { hasDot = true; _position++; continue; }
                    break;
                }
                var text = _source.Substring(start, _position - start);
                decimal value;
                if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value))
                    throw new FormulaException("无效数值：" + text);
                return new Token(TokenKind.Number, text, value);
            }

            private Token ReadIdentifier()
            {
                var start = _position;
                while (_position < _source.Length)
                {
                    var character = _source[_position];
                    if (!char.IsLetterOrDigit(character) && character != '_' && character != '.' && character <= 127) break;
                    _position++;
                }
                return new Token(TokenKind.Identifier, _source.Substring(start, _position - start));
            }

            private bool Take(char expected)
            {
                if (_position >= _source.Length || _source[_position] != expected) return false;
                _position++;
                return true;
            }
        }

        private struct Token
        {
            public Token(TokenKind kind, string text, decimal number = 0m)
            {
                Kind = kind;
                Text = text;
                Number = number;
            }

            public TokenKind Kind { get; private set; }
            public string Text { get; private set; }
            public decimal Number { get; private set; }
        }

        private enum TokenKind
        {
            End,
            Number,
            Identifier,
            Plus,
            Minus,
            Multiply,
            Divide,
            LeftParenthesis,
            RightParenthesis,
            Comma,
            Equal,
            NotEqual,
            Less,
            LessOrEqual,
            Greater,
            GreaterOrEqual
        }
    }

    public sealed class FormulaException : Exception
    {
        public FormulaException(string message) : base(message) { }
    }
}
