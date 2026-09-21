using AmwalECR.Example.Core.Ui;
using Xunit;

namespace AmwalECR.Example.Tests;

public class AmountLimitsTests
{
    [Fact]
    public void Bounds_match_the_pos_purchase_form()
    {
        Assert.Equal(0.010m, AmountLimits.Min);
        Assert.Equal(100000.000m, AmountLimits.Max);
        Assert.Equal(3, AmountLimits.FractionDigits);
    }

    [Fact]
    public void MaxDigits_is_enough_to_key_the_maximum_and_no_more()
    {
        // 100000.000 is 100000000 mills.
        Assert.Equal(9, AmountLimits.MaxDigits);
    }

    [Theory]
    [InlineData("0.01", "0.010")]
    [InlineData("100000", "100000.000")]
    [InlineData("1.2345", "1.235")]
    [InlineData("0", "0.000")]
    public void Formatted_always_has_three_decimals(string amount, string expected) =>
        Assert.Equal(expected, AmountLimits.Formatted(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("000000001234", "1.234")]
    [InlineData("000000000001", "0.001")]
    [InlineData("000000000000", "0.000")]
    [InlineData("000010000000", "10000.000")]
    [InlineData("", "")]
    [InlineData("abc", "abc")]
    public void FromMinorUnits_reads_the_wire_amount(string minor, string expected) =>
        Assert.Equal(expected, AmountLimits.FromMinorUnits(minor));
}

public class AmountEntryTests
{
    [Theory]
    [InlineData("", "0.000")]
    [InlineData("1", "0.001")]
    [InlineData("12", "0.012")]
    [InlineData("123", "0.123")]
    [InlineData("1234", "1.234")]
    [InlineData("100000000", "100000.000")]
    public void Format_shifts_digits_in_from_the_right(string digits, string shown) =>
        Assert.Equal(shown, AmountEntry.Format(digits));

    [Theory]
    [InlineData("", 0.000)]
    [InlineData("1", 0.001)]
    [InlineData("12", 0.012)]
    [InlineData("1234", 1.234)]
    [InlineData("100000000", 100000.000)]
    public void AmountOf_reads_keyed_digits_as_mills(string digits, double expected) =>
        Assert.Equal((decimal)expected, AmountEntry.AmountOf(digits));

    [Fact]
    public void Keys_1_2_3_4_then_backspace_follow_the_readme_table()
    {
        var digits = "";
        var shown = new List<string>();
        foreach (var key in "1234")
        {
            digits = AmountEntry.Append(digits, key);
            shown.Add(AmountEntry.Format(digits));
        }
        digits = AmountEntry.Backspace(digits);
        shown.Add(AmountEntry.Format(digits));

        Assert.Equal(new[] { "0.001", "0.012", "0.123", "1.234", "0.123" }, shown);
    }

    [Fact]
    public void A_zero_on_an_empty_field_changes_nothing()
    {
        Assert.Equal("", AmountEntry.Append("", '0'));
        Assert.Equal("10", AmountEntry.Append("1", '0'));
    }

    [Fact]
    public void Backspace_on_an_empty_field_is_harmless() => Assert.Equal("", AmountEntry.Backspace(""));

    [Fact]
    public void Keying_stops_at_the_maximum_number_of_digits()
    {
        var digits = "";
        foreach (var key in "12345678901234") digits = AmountEntry.Append(digits, key);

        Assert.Equal("123456789", digits);
        Assert.Equal("123456.789", AmountEntry.Format(digits));
    }

    [Theory]
    [InlineData("1a2b3", "123")]
    [InlineData("000123", "123")]
    [InlineData("0.000", "")]
    [InlineData("١٢", "")] // Arabic-Indic digits are not keyed, only ASCII.
    public void KeyedDigits_keeps_ascii_digits_and_drops_leading_zeros(string text, string expected) =>
        Assert.Equal(expected, AmountEntry.KeyedDigits(text));
}
