using System.Globalization;

namespace Apparatus.Core.Calc;

/// <summary>
/// Small recursive-descent evaluator. Numbers accept both "," and "." as decimal separator (pt-BR and en),
/// so function arguments are separated by ";". Supports + - * / ^ ( ), unary minus, implicit multiplication
/// ("2(3+4)", "2pi"), constants and common functions.
/// </summary>
public static class ExpressionEvaluator
{
    private static readonly Dictionary<string, double> Constants = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pi"] = Math.PI, ["π"] = Math.PI, ["e"] = Math.E, ["tau"] = Math.Tau,
    };

    private static readonly Dictionary<string, Func<double[], double>> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sqrt"] = a => Math.Sqrt(a[0]), ["raiz"] = a => Math.Sqrt(a[0]), ["cbrt"] = a => Math.Cbrt(a[0]),
        ["abs"] = a => Math.Abs(a[0]),
        ["sin"] = a => Math.Sin(a[0]), ["cos"] = a => Math.Cos(a[0]), ["tan"] = a => Math.Tan(a[0]),
        ["asin"] = a => Math.Asin(a[0]), ["acos"] = a => Math.Acos(a[0]), ["atan"] = a => Math.Atan(a[0]),
        ["ln"] = a => Math.Log(a[0]), ["log"] = a => Math.Log10(a[0]), ["log2"] = a => Math.Log2(a[0]),
        ["exp"] = a => Math.Exp(a[0]),
        ["floor"] = a => Math.Floor(a[0]), ["ceil"] = a => Math.Ceiling(a[0]), ["round"] = a => Math.Round(a[0], MidpointRounding.AwayFromZero),
        ["min"] = a => a.Min(), ["max"] = a => a.Max(),
    };

    /// <summary>
    /// Evaluates <paramref name="text"/> when it is a genuine calculation (has an operator, parentheses or a
    /// function call). A bare number or word is not offered as a calculation.
    /// </summary>
    public static bool TryEvaluate(string text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 200) return false;
        string s = text.Replace('×', '*').Replace('÷', '/').Replace('−', '-').Replace("**", "^").Trim().TrimEnd('=').Trim();
        if (!LooksLikeMath(s)) return false;
        try
        {
            var p = new Parser(s);
            value = p.ParseAll();
            return double.IsFinite(value);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool LooksLikeMath(string s)
    {
        bool hasDigitOrConstant = false, hasOperator = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsDigit(c)) hasDigitOrConstant = true;
            else if (c is '+' or '*' or '/' or '^' or '(' or ')') hasOperator = true;
            else if (c == '-' && i > 0) hasOperator = true; // leading '-' alone is just a negative number
        }
        if (!hasDigitOrConstant)
        {
            // "pi*2" has no digit but is still math.
            hasDigitOrConstant = Constants.Keys.Any(k => s.Contains(k, StringComparison.OrdinalIgnoreCase));
        }
        return hasDigitOrConstant && hasOperator;
    }

    public static string Format(double value)
    {
        if (value == Math.Round(value) && Math.Abs(value) < 1e15) return value.ToString("0", CultureInfo.GetCultureInfo("pt-BR"));
        string s = value.ToString("G12", CultureInfo.GetCultureInfo("pt-BR"));
        return s;
    }

    private sealed class Parser
    {
        private readonly string _s;
        private int _i;

        public Parser(string s) => _s = s;

        public double ParseAll()
        {
            double v = ParseExpression();
            SkipSpaces();
            if (_i != _s.Length) throw new FormatException("Unexpected input");
            return v;
        }

        private void SkipSpaces() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }

        private bool Peek(char c)
        {
            SkipSpaces();
            return _i < _s.Length && _s[_i] == c;
        }

        // expression := term (('+' | '-') term)*
        private double ParseExpression()
        {
            double v = ParseTerm();
            while (true)
            {
                if (Peek('+')) { _i++; v += ParseTerm(); }
                else if (Peek('-')) { _i++; v -= ParseTerm(); }
                else return v;
            }
        }

        // term := power (('*' | '/') power | implicit-multiplication power)*
        private double ParseTerm()
        {
            double v = ParsePower();
            while (true)
            {
                if (Peek('*')) { _i++; v *= ParsePower(); }
                else if (Peek('/'))
                {
                    _i++;
                    double d = ParsePower();
                    if (d == 0) throw new FormatException("Division by zero");
                    v /= d;
                }
                else if (StartsOperand()) v *= ParsePower();
                else return v;
            }
        }

        private bool StartsOperand()
        {
            SkipSpaces();
            if (_i >= _s.Length) return false;
            char c = _s[_i];
            return char.IsLetter(c) || c == '(';
        }

        // power := ('-' | '+')* primary ('^' power)?   (right associative; unary minus binds looser than ^: -2^2 = -4)
        private double ParsePower()
        {
            if (Peek('-')) { _i++; return -ParsePower(); }
            if (Peek('+')) { _i++; return ParsePower(); }
            double b = ParsePrimary();
            if (Peek('^')) { _i++; return Math.Pow(b, ParsePower()); }
            return b;
        }

        private double ParsePrimary()
        {
            SkipSpaces();
            if (_i >= _s.Length) throw new FormatException("Unexpected end");
            char c = _s[_i];

            if (c == '(')
            {
                _i++;
                double v = ParseExpression();
                if (!Peek(')')) throw new FormatException("Missing )");
                _i++;
                return v;
            }
            if (char.IsDigit(c) || c is '.' or ',') return ParseNumber();
            if (char.IsLetter(c)) return ParseIdentifier();
            throw new FormatException($"Unexpected '{c}'");
        }

        private double ParseNumber()
        {
            int start = _i;
            bool seenSeparator = false;
            while (_i < _s.Length && (char.IsDigit(_s[_i]) || (_s[_i] is '.' or ',' && !seenSeparator)))
            {
                if (_s[_i] is '.' or ',') seenSeparator = true;
                _i++;
            }
            // Scientific notation: 1e3, 2.5E-4
            if (_i < _s.Length && _s[_i] is 'e' or 'E' && _i + 1 < _s.Length &&
                (char.IsDigit(_s[_i + 1]) || (_s[_i + 1] is '+' or '-' && _i + 2 < _s.Length && char.IsDigit(_s[_i + 2]))))
            {
                _i += 2;
                while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
            }
            var text = _s[start.._i].Replace(',', '.');
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                throw new FormatException("Bad number");
            return v;
        }

        private double ParseIdentifier()
        {
            int start = _i;
            while (_i < _s.Length && (char.IsLetter(_s[_i]) || char.IsDigit(_s[_i]) && _i > start)) _i++;
            string name = _s[start.._i];

            if (Peek('('))
            {
                if (!Functions.TryGetValue(name, out var fn)) throw new FormatException($"Unknown function {name}");
                _i++;
                var args = new List<double> { ParseExpression() };
                while (Peek(';')) { _i++; args.Add(ParseExpression()); }
                if (!Peek(')')) throw new FormatException("Missing )");
                _i++;
                return fn(args.ToArray());
            }
            if (Constants.TryGetValue(name, out double c)) return c;
            throw new FormatException($"Unknown identifier {name}");
        }
    }
}
