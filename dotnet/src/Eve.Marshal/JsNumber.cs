using System.Globalization;
using System.Text;

namespace Eve.Marshal;

/// <summary>
/// JavaScript number semantics the marshal reference relies on. The packed-row
/// encoder accepts loosely typed input and coerces it with <c>Number(...)</c>,
/// <c>Math.trunc</c>, <c>&gt;&gt;&gt; 0</c> and <c>BigInt(...)</c>; these helpers
/// reproduce those coercions so the C# codec writes the same bytes.
/// </summary>
internal static class JsNumber
{
    private const double MaxSafeInteger = 9_007_199_254_740_991.0;

    public static bool IsSafeInteger(double value) =>
        double.IsFinite(value) && Math.Truncate(value) == value && Math.Abs(value) <= MaxSafeInteger;

    /// <summary><c>value &gt;&gt;&gt; 0</c> (ToUint32).</summary>
    public static uint ToUint32(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0;
        }
        var remainder = Math.Truncate(value) % 4_294_967_296.0;
        if (remainder < 0)
        {
            remainder += 4_294_967_296.0;
        }
        return (uint)remainder;
    }

    // JavaScript's String.prototype.trim whitespace, by code point (the set differs from .NET's).
    private static bool IsJsWhitespace(char c) =>
        c is (>= (char)0x09 and <= (char)0x0d) or (char)0x20 or (char)0xa0 or (char)0x1680
            or (>= (char)0x2000 and <= (char)0x200a) or (char)0x2028 or (char)0x2029 or (char)0x202f
            or (char)0x205f or (char)0x3000 or (char)0xfeff;

    public static string TrimJs(string text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && IsJsWhitespace(text[start]))
        {
            start++;
        }
        while (end > start && IsJsWhitespace(text[end - 1]))
        {
            end--;
        }
        return text[start..end];
    }

    private static bool TryRadixPrefix(string text, out int radix, out string digits)
    {
        radix = 0;
        digits = "";
        if (text.Length < 2 || text[0] != '0')
        {
            return false;
        }
        radix = text[1] switch
        {
            'x' or 'X' => 16,
            'o' or 'O' => 8,
            'b' or 'B' => 2,
            _ => 0,
        };
        if (radix == 0)
        {
            return false;
        }
        digits = text[2..];
        return true;
    }

    private static int DigitValue(char c, int radix)
    {
        var value = c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'z' => c - 'a' + 10,
            >= 'A' and <= 'Z' => c - 'A' + 10,
            _ => int.MaxValue,
        };
        return value < radix ? value : -1;
    }

    /// <summary><c>Number(string)</c>; NaN when the text is not a numeric literal.</summary>
    public static double NumberFromString(string text)
    {
        text = TrimJs(text);
        if (text.Length == 0)
        {
            return 0.0;
        }
        if (TryRadixPrefix(text, out var radix, out var digits))
        {
            if (digits.Length == 0)
            {
                return double.NaN;
            }
            var value = 0.0;
            foreach (var c in digits)
            {
                var digit = DigitValue(c, radix);
                if (digit < 0)
                {
                    return double.NaN;
                }
                value = (value * radix) + digit;
            }
            return value;
        }
        switch (text)
        {
            case "Infinity" or "+Infinity":
                return double.PositiveInfinity;
            case "-Infinity":
                return double.NegativeInfinity;
        }
        // .NET (like Rust) also parses "NaN", "∞" and friends; JS does not.
        foreach (var c in text)
        {
            if (!(c is >= '0' and <= '9' or '+' or '-' or '.' or 'e' or 'E'))
            {
                return double.NaN;
            }
        }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN;
    }

    /// <summary>
    /// <c>BigInt(string)</c> for values that fit in an Int128; null when JS would throw a
    /// SyntaxError (or the value is out of Int128 range).
    /// </summary>
    public static Int128? BigIntFromString(string text)
    {
        text = TrimJs(text);
        if (text.Length == 0)
        {
            return 0;
        }
        if (TryRadixPrefix(text, out var radix, out var prefixed))
        {
            return ParseRadix(prefixed, radix, negative: false);
        }
        var negative = false;
        var body = text;
        if (text[0] == '-')
        {
            negative = true;
            body = text[1..];
        }
        else if (text[0] == '+')
        {
            body = text[1..];
        }
        if (body.Length == 0 || !body.All(char.IsAsciiDigit))
        {
            return null;
        }
        return ParseRadix(body, 10, negative);
    }

    private static Int128? ParseRadix(string digits, int radix, bool negative)
    {
        if (digits.Length == 0)
        {
            return null;
        }
        try
        {
            Int128 magnitude = 0;
            foreach (var c in digits)
            {
                var digit = DigitValue(c, radix);
                if (digit < 0)
                {
                    return null;
                }
                magnitude = checked((magnitude * radix) + digit);
            }
            return negative ? -magnitude : magnitude;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary><c>Number(value)</c> for a decoded value (what the virtual-column resolvers use).</summary>
    public static double ValueToNumber(PyValue value) => value switch
    {
        PyNone => 0.0,
        PyBool flag => flag.Value ? 1.0 : 0.0,
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyReal number => number.Value,
        PyBigUnsigned big => big.Bytes.Reverse().Aggregate(0.0, (acc, b) => (acc * 256.0) + b),
        PyStr text => NumberFromString(text.Value),
        PyWString text => NumberFromString(text.Value),
        PyToken text => NumberFromString(text.Value),
        PyBytes bytes => NumberFromString(Encoding.UTF8.GetString(bytes.Value.Span)),
        PyBuffer bytes => NumberFromString(Encoding.UTF8.GetString(bytes.Value.Span)),
        _ => double.NaN,
    };
}
