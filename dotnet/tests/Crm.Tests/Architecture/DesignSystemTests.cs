using System.Text.RegularExpressions;

namespace Crm.Tests.Architecture;

/// <summary>Acceptance criteria 1-3 and 10 of docs/ux/ui-redesign.md as source checks on the shipped CSS and views.</summary>
public sealed partial class DesignSystemTests
{
    private static readonly string WebRoot = FindWebRoot();

    private static string FindWebRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "src", "Crm.Web");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "Crm.Web.csproj")))
            {
                return candidate;
            }
        }
        throw new DirectoryNotFoundException("src/Crm.Web not found");
    }

    private static string Css() => File.ReadAllText(Path.Combine(WebRoot, "wwwroot", "css", "app.css"));

    private static string Norm(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    [GeneratedRegex(@"@media \(prefers-color-scheme: dark\) \{\s*:root:not\(\[data-theme=""light""\]\) \{(?<body>[^}]*)\}\s*\}", RegexOptions.CultureInvariant)]
    private static partial Regex SystemDark();

    [GeneratedRegex(@":root\[data-theme=""dark""\] \{(?<body>[^}]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex ManualDark();

    [Theory]
    [InlineData("--surface", "#ffffff")]
    [InlineData("--surface-subtle", "#eeeeee")]
    [InlineData("--surface-panel", "#d6d7d7")]
    [InlineData("--ink", "#505152")]
    [InlineData("--border-input", "#505152")]
    [InlineData("--action", "#00456c")]
    [InlineData("--action-hover", "#003b5c")]
    [InlineData("--on-action", "#ffffff")]
    [InlineData("--danger", "#a4262c")]
    [InlineData("--dafa-blue", "#00456c")]
    [InlineData("--dafa-ochre", "#d2a225")]
    [InlineData("--dafa-mint", "#54bbab")]
    [InlineData("--dafa-wind-100", "#51657f")]
    [InlineData("--dafa-building-100", "#8e5550")]
    [InlineData("--dafa-industry-100", "#4c6c6f")]
    public void Light_tokens_have_the_exact_names_and_values(string name, string value) =>
        Assert.Matches(new Regex($@"{Regex.Escape(name)}:\s*{Regex.Escape(value)};", RegexOptions.CultureInvariant), Css());

    [Fact]
    public void The_two_dark_blocks_are_identical_and_carry_the_dark_values()
    {
        var css = Css();
        var system = SystemDark().Match(css);
        var manual = ManualDark().Match(css);
        Assert.True(system.Success && manual.Success);
        Assert.Equal(Norm(system.Groups["body"].Value), Norm(manual.Groups["body"].Value));
        foreach (var expected in new[] { "--surface:#505152", "--surface-subtle:#48494a", "--surface-panel:#434445", "--ink:#ffffff", "--action:#ffffff", "--on-action:#505152", "--danger:#ffc2bb" })
        {
            Assert.Contains(expected, Norm(system.Groups["body"].Value).Replace(": ", ":", StringComparison.Ordinal), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void No_colour_literal_outside_the_token_block()
    {
        var css = Css();
        var end = css.IndexOf("/* end of tokens */", StringComparison.Ordinal);
        Assert.True(end > 0);
        Assert.DoesNotMatch(@"#[0-9a-fA-F]{3,8}\b", css[end..]);
        Assert.DoesNotMatch(@"\brgba?\(", css[end..]);
    }

    [Fact]
    public void No_shadows_gradients_or_other_radii_and_only_two_font_weights()
    {
        var css = Css();
        Assert.DoesNotContain("box-shadow", css, StringComparison.Ordinal);
        Assert.DoesNotContain("text-shadow", css, StringComparison.Ordinal);
        Assert.DoesNotContain("gradient(", css, StringComparison.Ordinal);
        foreach (Match m in Regex.Matches(css, @"border-radius:\s*([^;}]+)"))
        {
            Assert.Matches(@"^(0|var\(--radius-(?:sm|full|none)\))(?: (0|var\(--radius-(?:sm|full|none)\))){0,3}$", m.Groups[1].Value.Trim());
        }
        Assert.DoesNotMatch(@"font-weight:(?>\s*)(?!var\(--weight-(regular|bold)\)|inherit)", css);
        Assert.DoesNotMatch(@"font:\s*(?!var\(--weight)[0-9]", css);
        Assert.Contains("--font-sans:\"Avenir Next\",\"Avenir Next LT Pro\",Arial,sans-serif;", css, StringComparison.Ordinal);
    }

    [Fact]
    public void No_unicode_symbols_or_emoji_are_used_as_icons_in_views()
    {
        foreach (var f in Directory.EnumerateFiles(WebRoot, "*.cshtml", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var t = File.ReadAllText(f);
            Assert.DoesNotMatch("[←-⇿⌀-⏿■-➿⬀-⯿️\uD83C-\uD83E]", t);
        }
    }

    [Fact]
    public void Brand_assets_and_the_icon_sprite_are_served_unchanged_from_wwwroot()
    {
        var repoAssets = Path.GetFullPath(Path.Combine(WebRoot, "..", "..", "..", "docs", "ux", "design-system", "assets"));
        if (!Directory.Exists(repoAssets))
        {
            return; // the source docs are not part of every checkout layout
        }
        foreach (var file in Directory.EnumerateFiles(repoAssets, "*.svg"))
        {
            var copy = Path.Combine(WebRoot, "wwwroot", "img", "brand", Path.GetFileName(file));
            Assert.True(File.Exists(copy), copy);
            Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(copy));
        }
        var sprite = File.ReadAllText(Path.Combine(WebRoot, "wwwroot", "img", "ui", "icons.svg"));
        foreach (var id in new[] { "dashboard", "pipeline", "accounts", "opportunities", "contacts", "activities", "analytics", "settings", "search", "plus", "edit", "delete", "more", "chevron-left", "chevron-right", "chevron-down", "menu", "close", "call", "meeting", "task", "note", "change", "check", "warning", "error", "info", "success", "calendar", "clock", "trend-up", "trend-down", "trend-flat", "filter", "sort", "sort-asc", "sort-desc", "drag", "theme", "signout", "external" })
        {
            Assert.Contains($"id=\"i-{id}\"", sprite, StringComparison.Ordinal);
        }
    }
}
