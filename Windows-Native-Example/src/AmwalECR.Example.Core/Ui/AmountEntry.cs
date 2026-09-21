using System.Globalization;

namespace AmwalECR.Example.Core.Ui;

/// <summary>
/// POS-style amount entry, matching the merchant app's
/// <c>AmountInputFormatter</c>: the field always shows three decimals and typed
/// digits shift in from the right, so 1 gives 0.001, 12 gives 0.012, 1234 gives
/// 1.234. Backspace shifts them back out.
/// <para>
/// There is no decimal point to type — the cashier just keys the mills. The
/// caller owns the raw keyed digits; everything shown is derived from them.
/// </para>
/// </summary>
public static class AmountEntry
{
    /// <summary>The keyed digits as an amount in major units, e.g. 1.234.</summary>
    public static decimal AmountOf(string digits)
    {
        var value = long.Parse(digits.Length == 0 ? "0" : digits, CultureInfo.InvariantCulture);
        return value / 1000m;
    }

    /// <summary>Digits only, capped so the terminal's limit cannot be over-keyed.</summary>
    public static string KeyedDigits(string text)
    {
        var digits = new string(text.Where(IsAsciiDigit).ToArray()).TrimStart('0');
        return digits.Length <= AmountLimits.MaxDigits ? digits : digits[..AmountLimits.MaxDigits];
    }

    /// <summary>Renders keyed digits as 0.000, shifting them in from the right.</summary>
    public static string Format(string digits)
    {
        var padded = (digits.Length == 0 ? "0" : digits).PadLeft(AmountLimits.FractionDigits + 1, '0');
        return $"{padded[..^AmountLimits.FractionDigits].TrimStart('0').PadLeft(1, '0')}.{padded[^AmountLimits.FractionDigits..]}";
    }

    /// <summary>Digits after one more key: the new digit shifts in from the right.</summary>
    public static string Append(string digits, char digit) => KeyedDigits(digits + digit);

    /// <summary>Digits after backspace: the last one shifts back out.</summary>
    public static string Backspace(string digits) => digits.Length == 0 ? "" : digits[..^1];

    private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';
}
