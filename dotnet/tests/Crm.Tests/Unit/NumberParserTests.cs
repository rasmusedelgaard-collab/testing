using Crm.Web.Services;

namespace Crm.Tests.Unit;

/// <summary>Ports tests/numberParsing.test.ts (parse cases verbatim).</summary>
public class NumberParserTests
{
    private static ParseResult A(string s, string l) => NumberParser.ParseAmount(s, l);

    [Theory]
    [InlineData("1.234,56", "1234.56")]
    [InlineData("1234,56", "1234.56")]
    [InlineData("6,8", "6.8")]
    [InlineData("0,5", "0.5")]
    [InlineData("12500", "12500")]
    [InlineData("12.500", "12500")]
    [InlineData("1.234.567,89", "1234567.89")]
    [InlineData("0", "0")]
    [InlineData("007", "7")]
    [InlineData("100,00", "100")]
    [InlineData("999.999.999,99", "999999999.99")]
    [InlineData("12,5", "12.5")]
    [InlineData("1.000", "1000")]
    public void Da_accepts(string input, string value)
    {
        var r = A(input, "da-DK");
        Assert.True(r.Ok);
        Assert.Equal(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), r.Value);
    }

    [Fact]
    public void Da_rate_looking_value_is_an_amount_error() => Assert.False(A("7,4590", "da-DK").Ok);

    [Theory]
    [InlineData("12.5")]
    [InlineData("1.23")]
    [InlineData("1.2345")]
    [InlineData("12.34.56")]
    [InlineData("1.234.56")]
    [InlineData("1234.567")]
    [InlineData("1,234,56")]
    [InlineData("1,2,3")]
    [InlineData("1.234,5678")]
    [InlineData("1,234")]
    [InlineData("1.")]
    [InlineData(".5")]
    [InlineData(",5")]
    [InlineData("5,")]
    [InlineData("1 234")]
    [InlineData("1 234,56")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("abc")]
    [InlineData("1e5")]
    [InlineData("0x10")]
    [InlineData("1,2.3")]
    [InlineData("1..234")]
    [InlineData("12.500,0,0")]
    [InlineData("+5")]
    [InlineData("١٢٣")]
    public void Da_rejects(string input) => Assert.False(A(input, "da-DK").Ok);

    [Theory]
    [InlineData("en-GB")]
    [InlineData("en-US")]
    public void English_accepts_the_right_forms(string locale)
    {
        foreach (var (input, value) in new[] { ("1,234.56", 1234.56m), ("1234.56", 1234.56m), ("6.8", 6.8m), ("12,500", 12500m), ("12500", 12500m), ("1,234,567.89", 1234567.89m), ("0.5", 0.5m), ("100", 100m), ("1,000", 1000m) })
        {
            var r = A(input, locale);
            Assert.True(r.Ok, input);
            Assert.Equal(value, r.Value);
        }
    }

    [Theory]
    [InlineData("en-GB")]
    [InlineData("en-US")]
    public void English_rejects_look_alikes(string locale)
    {
        foreach (var input in new[] { "6,8", "1,23", "1,2345", "12,34,56", "1234,567", "1,234.567", "1.234,56", "12.5.6", "1,23,456", ",5", ".", "", "1 234.56", "1,,234" })
        {
            Assert.False(A(input, locale).Ok, input);
        }
    }

    [Fact]
    public void Negatives_limits_and_unknown_locales()
    {
        Assert.Equal(ParseFailure.Negative, A("-5", "da-DK").Reason);
        Assert.Equal(ParseFailure.Negative, A("-1.234,00", "da-DK").Reason);
        Assert.Equal(ParseFailure.Negative, A(" -5", "en-GB").Reason);
        Assert.False(A(new string('9', 30), "en-GB").Ok);
        Assert.False(A("100,000,000,000,000", "en-GB").Ok);
        Assert.True(A("99,999,999,999,999", "en-GB").Ok);
        Assert.Equal(1234.5m, A("1,234.5", "xx").Value);
    }

    [Theory]
    [InlineData("7,4590", "7.4590")]
    [InlineData("0,1", "0.1")]
    [InlineData("7,123456", "7.123456")]
    [InlineData("10", "10")]
    [InlineData("1000,5", "1000.5")]
    public void Rate_da_accepts(string input, string value) => Assert.Equal(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), NumberParser.ParseRate(input, "da-DK").Value);

    [Theory]
    [InlineData("7.4590")]
    [InlineData("1.234,5")]
    [InlineData("7,1234567")]
    [InlineData("7,")]
    [InlineData(",5")]
    [InlineData("1.000")]
    [InlineData("7 459")]
    [InlineData("")]
    public void Rate_da_rejects(string input) => Assert.False(NumberParser.ParseRate(input, "da-DK").Ok);

    [Theory]
    [InlineData("7.4590", "7.4590")]
    [InlineData("0.1", "0.1")]
    [InlineData("1000.5", "1000.5")]
    public void Rate_en_accepts(string input, string value) => Assert.Equal(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), NumberParser.ParseRate(input, "en-GB").Value);

    [Theory]
    [InlineData("6,8")]
    [InlineData("7,4590")]
    [InlineData("1,000.5")]
    [InlineData("7.1234567")]
    [InlineData("1,000")]
    public void Rate_en_rejects(string input) => Assert.False(NumberParser.ParseRate(input, "en-GB").Ok);

    [Fact]
    public void Rate_zero_parses_and_negatives_are_flagged()
    {
        Assert.True(NumberParser.ParseRate("0", "da-DK").Ok);
        Assert.Equal(ParseFailure.Negative, NumberParser.ParseRate("-1", "da-DK").Reason);
    }

    [Fact]
    public void Examples_follow_the_locale()
    {
        Assert.Equal("12.500,00", NumberParser.AmountExample("da-DK"));
        Assert.Equal("12,500.00", NumberParser.AmountExample("en-GB"));
        Assert.Equal("7,4590", NumberParser.RateExample("da-DK"));
        Assert.Equal("7.4590", NumberParser.RateExample("en-GB"));
        Assert.Equal("7.4590", NumberParser.RateExample("en-US"));
    }

    [Theory]
    [InlineData("2026-12-31", true)]
    [InlineData("2026-02-30", false)]
    [InlineData("1899-12-31", false)]
    [InlineData("2026-1-1", false)]
    [InlineData("2026-12-31\n", false)]
    [InlineData("٢٠٢٦-١٢-٣١", false)]
    [InlineData("", false)]
    public void Real_dates(string s, bool expected) => Assert.Equal(expected, NumberParser.IsRealDate(s));

    [Fact]
    public void Formatter_uses_the_pinned_profiles_not_the_host()
    {
        var da = new Formatter("da-DK", "Europe/Copenhagen");
        Assert.Equal("DKK 12.500,00", da.Money(12500m, "DKK"));
        Assert.Equal("18.11.2026", da.Date("2026-11-18"));
        Assert.Equal("18.11.2026 14:05", da.DateTime("2026-11-18T13:05:00.000Z"));
        Assert.Equal("1234,5", da.AmountInput(1234.5m));
        Assert.Equal("7,459", da.RateInput(7.459m));
        var gb = new Formatter("en-GB", "UTC");
        Assert.Equal("EUR 1,234.50", gb.Money(1234.5m, "EUR"));
        Assert.Equal("18/11/2026", gb.Date("2026-11-18"));
        var us = new Formatter("en-US", "America/New_York");
        Assert.Equal("11/18/2026", us.Date("2026-11-18T00:00:00"));
        Assert.Equal("11/18/2026 8:05 AM", us.DateTime("2026-11-18T13:05:00.000Z"));
        Assert.Equal("", gb.Date(null));
        Assert.Equal("", gb.DateTime(""));
        var unknown = new Formatter("xx-XX", "Not/AZone");
        Assert.Equal("18/11/2026 13:05", unknown.DateTime("2026-11-18T13:05:00.000Z"));
    }
}
