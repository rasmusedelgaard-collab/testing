using System.Text;
using Crm.Web.Data;

namespace Crm.Tests.Unit;

public class SqlBatchesTests
{
    [Fact]
    public void Splits_on_GO_lines_only_and_drops_empty_batches()
    {
        Assert.Equal(["SELECT 1;", "SELECT 2; -- GO here"], SqlBatches.Split("SELECT 1;\nGO\n\nSELECT 2; -- GO here\nGO  \nGO\n"));
        Assert.Equal(["SELECT 1", "SELECT 2"], SqlBatches.Split("SELECT 1\r\ngo\r\nSELECT 2"));
        Assert.Equal(["-- GOAL\nSELECT 1"], SqlBatches.Split("-- GOAL\nSELECT 1"));
    }

    [Fact]
    public void Checksum_ignores_crlf_and_is_content_sensitive()
    {
        Assert.Equal(SqlBatches.Checksum("a\r\nb"u8), SqlBatches.Checksum("a\nb"u8));
        Assert.NotEqual(SqlBatches.Checksum("a"u8), SqlBatches.Checksum("b"u8));
        Assert.Equal(64, SqlBatches.Checksum("a"u8).Length);
    }

    [Fact]
    public void Checksum_keeps_a_bom()
    {
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat("a"u8.ToArray()).ToArray();
        Assert.NotEqual(SqlBatches.Checksum("a"u8), SqlBatches.Checksum(withBom));
    }

    [Theory]
    [InlineData("001_schema.sql", "21f1d90c4325a4695b50b7091033a6ca6477750694ef743af694d4cbdd7c6963")]
    [InlineData("002_rls.sql", "3856a7f0b33961a8049b46c1e820e77fa73114f6bda922670f8a151cfe7765f4")]
    [InlineData("003_grants.sql", "c1bc06c7eba46ba449aa5b316e762aa7f5ac144a9886550d410a1cbd7068a0a0")]
    [InlineData("004_seed.sql", "3039dca397f42ac8305c2ef1957bce7f484f40b4aa351003dd0e6c4f24d5e8bf")]
    public void Embedded_migrations_match_the_golden_sha256sum_values(string file, string golden)
    {
        var (_, bytes) = Migrator.EmbeddedMigrations().Single(m => m.File == file);
        Assert.Equal(golden, SqlBatches.Checksum(bytes));
        // a CRLF copy gives the same value
        var crlf = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.Equal(golden, SqlBatches.Checksum(crlf));
    }

    [Fact]
    public void Migrations_are_numbered_without_gaps()
    {
        var files = Migrator.EmbeddedMigrations().Select(m => SqlBatches.ParseMigrationName(m.File)!.Value.Version).ToList();
        Assert.Equal(Enumerable.Range(1, files.Count), files);
    }

    [Fact]
    public void Migration_002_creates_both_policies_enabled_and_schema_bound()
    {
        var text = Encoding.UTF8.GetString(Migrator.EmbeddedMigrations().Single(m => m.File == "002_rls.sql").Bytes);
        Assert.Contains("CREATE SECURITY POLICY sec.CrmRowPolicy", text, StringComparison.Ordinal);
        Assert.Contains("CREATE SECURITY POLICY sec.CrmConfigPolicy", text, StringComparison.Ordinal);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(text, @"WITH \(STATE = ON, SCHEMABINDING = ON\)").Count);
    }

    [Theory]
    [InlineData("001_schema.sql", 1, "schema")]
    [InlineData("12_x.sql", 0, "")]
    public void Parses_migration_names(string file, int version, string name)
    {
        var m = SqlBatches.ParseMigrationName(file);
        if (version == 0) { Assert.Null(m); } else { Assert.Equal((version, name), m!.Value); }
    }
}
