using System.Globalization;
using System.Text;

namespace AgentStudio.Domain;

/// <summary>Small spreadsheet-like formula language for <see cref="ExpressionNode"/> — arithmetic,
/// comparisons, boolean logic, parens, and a handful of functions (IF/CONCAT/LEN/UPPER/LOWER/
/// TRIM/ROUND/ABS). Not a general scripting language: no variable assignment, no loops, no
/// user-defined functions — one value in, one value out, same "smallest thing that solves the
/// use case" choice as <see cref="JsonPathExtractor"/> not being full JSONPath.
///
/// <c>{input}</c>/<c>{variables.x}</c> placeholders are resolved to typed values (number if the
/// stored text parses as one, bool if it's exactly "true"/"false", string otherwise) as whole
/// tokens during scanning — never text-substituted into the formula source — so a variable's
/// value can never itself get re-parsed as formula syntax.</summary>
public static class FormulaEvaluator
{
    public static string Evaluate(string formula, IReadOnlyDictionary<string, string> variables)
    {
        var tokens = Tokenize(formula, variables);
        var parser = new Parser(tokens, formula);
        var result = parser.ParseExpression();
        parser.ExpectEnd();
        return result.ToDisplayString();
    }

    // ---------- values ----------

    private enum ValueKind { Number, String, Bool }

    private readonly struct Value
    {
        public readonly ValueKind Kind;
        public readonly double Number;
        public readonly string Str;
        public readonly bool Bool;

        private Value(ValueKind kind, double number, string str, bool boolean)
        {
            Kind = kind;
            Number = number;
            Str = str;
            Bool = boolean;
        }

        public static Value Of(double n) => new(ValueKind.Number, n, "", false);
        public static Value Of(string s) => new(ValueKind.String, 0, s, false);
        public static Value Of(bool b) => new(ValueKind.Bool, 0, "", b);

        public double AsNumber(string formula)
        {
            if (Kind == ValueKind.Number) return Number;
            if (Kind == ValueKind.String && double.TryParse(Str, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return n;
            throw new InvalidOperationException($"formula '{formula}': expected a number, got '{ToDisplayString()}'.");
        }

        public bool AsBool(string formula)
        {
            if (Kind == ValueKind.Bool) return Bool;
            throw new InvalidOperationException($"formula '{formula}': expected true/false, got '{ToDisplayString()}'.");
        }

        public string AsString() => Kind switch
        {
            ValueKind.String => Str,
            ValueKind.Bool => Bool ? "true" : "false",
            _ => Number.ToString(CultureInfo.InvariantCulture)
        };

        public string ToDisplayString() => AsString();

        public static bool Equal(Value a, Value b)
        {
            if (a.Kind == ValueKind.Number && b.Kind == ValueKind.Number) return a.Number == b.Number;
            if (a.Kind == ValueKind.Bool && b.Kind == ValueKind.Bool) return a.Bool == b.Bool;
            return a.AsString() == b.AsString();
        }
    }

    // ---------- tokens ----------

    private enum TokKind
    {
        Number, String, Value, Ident,
        Plus, Minus, Star, Slash,
        Eq, Neq, Lt, Gt, Le, Ge, And, Or, Not,
        LParen, RParen, Comma, End
    }

    private sealed class Token
    {
        public TokKind Kind;
        public double Number;
        public string Str = "";
        public Value Value;
    }

    private static List<Token> Tokenize(string formula, IReadOnlyDictionary<string, string> variables)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < formula.Length)
        {
            var c = formula[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '{')
            {
                var close = formula.IndexOf('}', i);
                if (close < 0)
                    throw new InvalidOperationException($"formula '{formula}': unterminated placeholder starting at position {i}.");
                var name = formula[(i + 1)..close].Trim();
                var raw = name == "input"
                    ? variables.TryGetValue("input", out var iv) ? iv : ""
                    : name.StartsWith("variables.", StringComparison.Ordinal)
                        ? variables.TryGetValue(name["variables.".Length..], out var vv) ? vv : ""
                        : throw new InvalidOperationException($"formula '{formula}': unrecognized placeholder '{{{name}}}' — use {{input}} or {{variables.x}}.");
                tokens.Add(new Token { Kind = TokKind.Value, Value = ResolvePlaceholderValue(raw) });
                i = close + 1;
                continue;
            }

            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < formula.Length && formula[i] != '"')
                {
                    if (formula[i] == '\\' && i + 1 < formula.Length) { sb.Append(formula[i + 1]); i += 2; }
                    else { sb.Append(formula[i]); i++; }
                }
                if (i >= formula.Length)
                    throw new InvalidOperationException($"formula '{formula}': unterminated string literal.");
                i++; // closing quote
                tokens.Add(new Token { Kind = TokKind.String, Str = sb.ToString() });
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < formula.Length && char.IsDigit(formula[i + 1])))
            {
                var start = i;
                while (i < formula.Length && (char.IsDigit(formula[i]) || formula[i] == '.')) i++;
                tokens.Add(new Token { Kind = TokKind.Number, Number = double.Parse(formula[start..i], CultureInfo.InvariantCulture) });
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < formula.Length && (char.IsLetterOrDigit(formula[i]) || formula[i] == '_')) i++;
                var word = formula[start..i].ToUpperInvariant();
                tokens.Add(word switch
                {
                    "TRUE" => new Token { Kind = TokKind.Value, Value = Value.Of(true) },
                    "FALSE" => new Token { Kind = TokKind.Value, Value = Value.Of(false) },
                    _ => new Token { Kind = TokKind.Ident, Str = word }
                });
                continue;
            }

            switch (c)
            {
                case '+': tokens.Add(new Token { Kind = TokKind.Plus }); i++; break;
                case '-': tokens.Add(new Token { Kind = TokKind.Minus }); i++; break;
                case '*': tokens.Add(new Token { Kind = TokKind.Star }); i++; break;
                case '/': tokens.Add(new Token { Kind = TokKind.Slash }); i++; break;
                case '(': tokens.Add(new Token { Kind = TokKind.LParen }); i++; break;
                case ')': tokens.Add(new Token { Kind = TokKind.RParen }); i++; break;
                case ',': tokens.Add(new Token { Kind = TokKind.Comma }); i++; break;
                case '=' when i + 1 < formula.Length && formula[i + 1] == '=': tokens.Add(new Token { Kind = TokKind.Eq }); i += 2; break;
                case '!' when i + 1 < formula.Length && formula[i + 1] == '=': tokens.Add(new Token { Kind = TokKind.Neq }); i += 2; break;
                case '!': tokens.Add(new Token { Kind = TokKind.Not }); i++; break;
                case '<' when i + 1 < formula.Length && formula[i + 1] == '=': tokens.Add(new Token { Kind = TokKind.Le }); i += 2; break;
                case '<': tokens.Add(new Token { Kind = TokKind.Lt }); i++; break;
                case '>' when i + 1 < formula.Length && formula[i + 1] == '=': tokens.Add(new Token { Kind = TokKind.Ge }); i += 2; break;
                case '>': tokens.Add(new Token { Kind = TokKind.Gt }); i++; break;
                case '&' when i + 1 < formula.Length && formula[i + 1] == '&': tokens.Add(new Token { Kind = TokKind.And }); i += 2; break;
                case '|' when i + 1 < formula.Length && formula[i + 1] == '|': tokens.Add(new Token { Kind = TokKind.Or }); i += 2; break;
                default:
                    throw new InvalidOperationException($"formula '{formula}': unexpected character '{c}' at position {i}.");
            }
        }
        tokens.Add(new Token { Kind = TokKind.End });
        return tokens;
    }

    private static Value ResolvePlaceholderValue(string raw)
    {
        if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)) return Value.Of(true);
        if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)) return Value.Of(false);
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return Value.Of(n);
        return Value.Of(raw);
    }

    // ---------- recursive-descent parser ----------

    private sealed class Parser
    {
        private readonly List<Token> _tokens;
        private readonly string _formula;
        private int _pos;

        public Parser(List<Token> tokens, string formula)
        {
            _tokens = tokens;
            _formula = formula;
        }

        private Token Current => _tokens[_pos];

        public void ExpectEnd()
        {
            if (Current.Kind != TokKind.End)
                throw new InvalidOperationException($"formula '{_formula}': unexpected trailing input.");
        }

        public Value ParseExpression() => ParseOr();

        private Value ParseOr()
        {
            var left = ParseAnd();
            while (Current.Kind == TokKind.Or)
            {
                _pos++;
                var right = ParseAnd();
                left = Value.Of(left.AsBool(_formula) || right.AsBool(_formula));
            }
            return left;
        }

        private Value ParseAnd()
        {
            var left = ParseNot();
            while (Current.Kind == TokKind.And)
            {
                _pos++;
                var right = ParseNot();
                left = Value.Of(left.AsBool(_formula) && right.AsBool(_formula));
            }
            return left;
        }

        private Value ParseNot()
        {
            if (Current.Kind == TokKind.Not)
            {
                _pos++;
                return Value.Of(!ParseNot().AsBool(_formula));
            }
            return ParseEquality();
        }

        private Value ParseEquality()
        {
            var left = ParseComparison();
            while (Current.Kind is TokKind.Eq or TokKind.Neq)
            {
                var op = Current.Kind;
                _pos++;
                var right = ParseComparison();
                var eq = Value.Equal(left, right);
                left = Value.Of(op == TokKind.Eq ? eq : !eq);
            }
            return left;
        }

        private Value ParseComparison()
        {
            var left = ParseAdditive();
            while (Current.Kind is TokKind.Lt or TokKind.Gt or TokKind.Le or TokKind.Ge)
            {
                var op = Current.Kind;
                _pos++;
                var right = ParseAdditive();
                var a = left.AsNumber(_formula);
                var b = right.AsNumber(_formula);
                left = Value.Of(op switch
                {
                    TokKind.Lt => a < b,
                    TokKind.Gt => a > b,
                    TokKind.Le => a <= b,
                    _ => a >= b
                });
            }
            return left;
        }

        private Value ParseAdditive()
        {
            var left = ParseTerm();
            while (Current.Kind is TokKind.Plus or TokKind.Minus)
            {
                var op = Current.Kind;
                _pos++;
                var right = ParseTerm();
                var a = left.AsNumber(_formula);
                var b = right.AsNumber(_formula);
                left = Value.Of(op == TokKind.Plus ? a + b : a - b);
            }
            return left;
        }

        private Value ParseTerm()
        {
            var left = ParseUnary();
            while (Current.Kind is TokKind.Star or TokKind.Slash)
            {
                var op = Current.Kind;
                _pos++;
                var right = ParseUnary();
                var a = left.AsNumber(_formula);
                var b = right.AsNumber(_formula);
                if (op == TokKind.Slash && b == 0)
                    throw new InvalidOperationException($"formula '{_formula}': division by zero.");
                left = Value.Of(op == TokKind.Star ? a * b : a / b);
            }
            return left;
        }

        private Value ParseUnary()
        {
            if (Current.Kind == TokKind.Minus)
            {
                _pos++;
                return Value.Of(-ParseUnary().AsNumber(_formula));
            }
            return ParsePrimary();
        }

        private Value ParsePrimary()
        {
            switch (Current.Kind)
            {
                case TokKind.Number:
                    { var v = Value.Of(Current.Number); _pos++; return v; }
                case TokKind.String:
                    { var v = Value.Of(Current.Str); _pos++; return v; }
                case TokKind.Value:
                    { var v = Current.Value; _pos++; return v; }
                case TokKind.LParen:
                    {
                        _pos++;
                        var v = ParseExpression();
                        Expect(TokKind.RParen, ")");
                        return v;
                    }
                case TokKind.Ident:
                    return ParseFunctionCall();
                default:
                    throw new InvalidOperationException($"formula '{_formula}': expected a value at position {_pos}.");
            }
        }

        private Value ParseFunctionCall()
        {
            var name = Current.Str;
            _pos++;
            Expect(TokKind.LParen, "(");
            var args = new List<Value>();
            if (Current.Kind != TokKind.RParen)
            {
                args.Add(ParseExpression());
                while (Current.Kind == TokKind.Comma)
                {
                    _pos++;
                    args.Add(ParseExpression());
                }
            }
            Expect(TokKind.RParen, ")");
            return CallFunction(name, args, _formula);
        }

        private void Expect(TokKind kind, string display)
        {
            if (Current.Kind != kind)
                throw new InvalidOperationException($"formula '{_formula}': expected '{display}' at position {_pos}.");
            _pos++;
        }
    }

    private static Value CallFunction(string name, List<Value> args, string formula)
    {
        switch (name)
        {
            case "IF":
                Arity(name, args, formula, 3);
                return args[0].AsBool(formula) ? args[1] : args[2];
            case "CONCAT":
                if (args.Count == 0)
                    throw new InvalidOperationException($"formula '{formula}': CONCAT needs at least one argument.");
                return Value.Of(string.Concat(args.Select(a => a.AsString())));
            case "LEN":
                Arity(name, args, formula, 1);
                return Value.Of(args[0].AsString().Length);
            case "UPPER":
                Arity(name, args, formula, 1);
                return Value.Of(args[0].AsString().ToUpperInvariant());
            case "LOWER":
                Arity(name, args, formula, 1);
                return Value.Of(args[0].AsString().ToLowerInvariant());
            case "TRIM":
                Arity(name, args, formula, 1);
                return Value.Of(args[0].AsString().Trim());
            case "ROUND":
                Arity(name, args, formula, 2);
                return Value.Of(Math.Round(args[0].AsNumber(formula), (int)args[1].AsNumber(formula), MidpointRounding.AwayFromZero));
            case "ABS":
                Arity(name, args, formula, 1);
                return Value.Of(Math.Abs(args[0].AsNumber(formula)));
            default:
                throw new InvalidOperationException($"formula '{formula}': unknown function '{name}'.");
        }
    }

    private static void Arity(string name, List<Value> args, string formula, int expected)
    {
        if (args.Count != expected)
            throw new InvalidOperationException($"formula '{formula}': {name} expects {expected} argument(s), got {args.Count}.");
    }
}
