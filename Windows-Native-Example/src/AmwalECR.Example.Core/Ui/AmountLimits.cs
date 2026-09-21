using System.Globalization;
using System.Numerics;

namespace AmwalECR.Example.Core.Ui;

/// <summary>
/// Amount bounds, mirroring the POS purchase form
/// (<c>transation_operation_dialog.dart</c>): minimum 10 mills, maximum the
/// terminal's transaction limit.
/// <para>
/// On POS the maximum comes from the server as
/// <c>transactionPermissionsViewModel.amountlimit</c> in minor units. This sample
/// has no such permission call, so it is a constant here — change
/// <see cref="Max"/> to match the limit configured for your terminals.
/// </para>
/// </summary>
public static class AmountLimits
{
    /// <summary>POS: <c>minAmount = 10 / 1000</c>.</summary>
    public static readonly decimal Min = 0.010m;

    /// <summary>POS: <c>amountLimit = amountlimit / 1000</c>.</summary>
    public static readonly decimal Max = 100000.000m;

    /// <summary>Decimal places the currency has. Matches the amount field's entry.</summary>
    public const int FractionDigits = 3;

    /// <summary>
    /// Digits the field accepts, the equivalent of the POS form's
    /// <c>maxLength</c>: enough to key the maximum and no more.
    /// </summary>
    public static int MaxDigits => ((long)(Max * 1000m)).ToString(CultureInfo.InvariantCulture).Length;

    public static string Formatted(decimal amount) =>
        Math.Round(amount, FractionDigits, MidpointRounding.AwayFromZero)
            .ToString("F" + FractionDigits, CultureInfo.InvariantCulture);

    /// <summary>Minor units off the wire read as an amount: 000000001234 becomes 1.234.</summary>
    public static string FromMinorUnits(string minorUnits)
    {
        if (minorUnits.Length == 0) return "";
        if (!BigInteger.TryParse(minorUnits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            return minorUnits;
        }
        var sign = value.Sign < 0 ? "-" : "";
        var digits = BigInteger.Abs(value).ToString(CultureInfo.InvariantCulture).PadLeft(FractionDigits + 1, '0');
        return $"{sign}{digits[..^FractionDigits]}.{digits[^FractionDigits..]}";
    }
}
