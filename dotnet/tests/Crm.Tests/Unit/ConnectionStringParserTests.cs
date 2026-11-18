using Crm.Web.Config;

namespace Crm.Tests.Unit;

public class ConnectionStringParserTests
{
    private const string Ok = "Server=db.example.com;Database=crm1;User Id=appuser;Password=s3cr3t-pw;";

    [Fact]
    public void Parses_the_usual_shape()
    {
        var p = ConnectionStringParser.Parse(Ok);
        Assert.Equal(new ParsedConnectionString("db.example.com", null, "crm1", "appuser", "s3cr3t-pw", false), p);
    }

    [Fact]
    public void Accepts_aliases_case_spacing_tcp_prefix_port_and_trust()
    {
        var p = ConnectionStringParser.Parse(" data source = tcp:db.example.com,1433 ;Initial Catalog=crm1; UID=u ; PWD=p ;Encrypt=True;TrustServerCertificate=True;Persist Security Info=False;Connection Timeout=30");
        Assert.Equal(new ParsedConnectionString("db.example.com", 1433, "crm1", "u", "p", true), p);
    }

    [Fact]
    public void Supports_quoted_values()
    {
        Assert.Equal("a;b=c\"d", ConnectionStringParser.Parse("Server=h;Database=d;User Id=u;Password=\"a;b=c\"\"d\";").Password);
        Assert.Equal("x;y", ConnectionStringParser.Parse("Server=h;Database=d;User Id=u;Password='x;y'").Password);
        Assert.Equal("pa=ss", ConnectionStringParser.Parse("Server=h;Database=d;User Id=u;Password=pa=ss").Password);
    }

    [Theory]
    [InlineData("", "missing")]
    [InlineData("Server=h;Database=d;User Id=u", "missing Password")]
    [InlineData("Server=h;Database=d;Password=p", "missing User Id")]
    [InlineData("garbage", "no \"=\"")]
    [InlineData("Server=h;Database=d;User Id=u;Password=p;Foo=bar", "unknown key \"Foo\"")]
    [InlineData("Server=h;Database=d;User Id=u;Password=p;Server=h2", "duplicate key")]
    [InlineData("Server=h;Database=d;User Id=u;Password=p;Encrypt=False", "Encrypt=False")]
    [InlineData("Server=h;Database=d;User Id=u;Password=p;Integrated Security=True", "not supported")]
    [InlineData("Server=h;Database=d;User Id=u;Password=p;Authentication=Active Directory Default", "not supported")]
    [InlineData("Server=h\\inst;Database=d;User Id=u;Password=p", "host or host,port")]
    [InlineData("Server=h,99999;Database=d;User Id=u;Password=p", "port")]
    [InlineData("Server=h;Database=d;User Id=u;Password=\"unterminated", "unterminated")]
    [InlineData("Server=h;Database=d;User Id=u;Password=\"a\"b", "text after")]
    [InlineData("Server=h;Database=d;User Id=u;Password=p;TrustServerCertificate=maybe", "TrustServerCertificate")]
    public void Refuses(string input, string why)
    {
        var e = Assert.Throws<ConnectionStringException>(() => ConnectionStringParser.Parse(input));
        Assert.Contains(why, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_too_long_password()
    {
        var e = Assert.Throws<ConnectionStringException>(() => ConnectionStringParser.Parse($"Server=h;Database=d;User Id=u;Password={new string('x', 300)}"));
        Assert.Contains("invalid \"Password\"", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Server=h;Database=d;User Id=u;Password=TOPSECRET;Foo=bar")]
    [InlineData("Server=h;Database=d;User Id=u;Password=TOPSECRET;Encrypt=False")]
    [InlineData("Server=h,99999;Database=d;User Id=u;Password=TOPSECRET")]
    public void Error_messages_never_contain_the_password(string bad)
    {
        var e = Assert.Throws<ConnectionStringException>(() => ConnectionStringParser.Parse(bad));
        Assert.DoesNotContain("TOPSECRET", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_ascii_digits_are_not_a_port()
    {
        Assert.Throws<ConnectionStringException>(() => ConnectionStringParser.Parse("Server=h,١٢٣;Database=d;User Id=u;Password=p"));
    }
}
