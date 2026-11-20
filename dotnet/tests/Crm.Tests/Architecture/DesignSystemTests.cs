using System.Text.RegularExpressions;

namespace Crm.Tests.Architecture;

/// <summary>The v2 visual language (docs/ux/visual-v2.md) as source checks on the shipped CSS and views: token names and values, contrast, no stray colours, shape and motion rules.</summary>
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

    private static string Tokens() { var css = Css(); return css[..css.IndexOf("/* end of tokens */", StringComparison.Ordinal)]; }

    private static string LightBlock() { var t = Tokens(); return t[..t.IndexOf("@media (prefers-color-scheme: dark)", StringComparison.Ordinal)]; }

    private static Dictionary<string, string> Map(string block) =>
        Regex.Matches(block, @"(--[a-z0-9-]+):\s*([^;]+);", RegexOptions.CultureInvariant).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim());

    /// <summary>Light tokens with the dark overrides on top: what the page really uses in that theme.</summary>
    private static Dictionary<string, string> Theme(bool dark)
    {
        var map = Map(LightBlock());
        if (dark)
        {
            foreach (var (k, v) in Map(ManualDark().Match(Css()).Groups["body"].Value)) { map[k] = v; }
        }
        return map;
    }

    [Theory]
    [InlineData("--page", "#f5f7fa")]
    [InlineData("--surface", "#ffffff")]
    [InlineData("--tint", "#eaf2fd")]
    [InlineData("--ink", "#1d1d1f")]
    [InlineData("--action", "#1a66cc")]
    [InlineData("--action-hover", "#1450a3")]
    [InlineData("--on-action", "#ffffff")]
    [InlineData("--accent-soft", "#4a90e2")]
    [InlineData("--accent-soft-top", "#5b9bf0")]
    [InlineData("--danger", "#b3261e")]
    [InlineData("--blue-500", "#4a90e2")]
    public void Light_tokens_have_the_exact_names_and_values(string name, string value) =>
        Assert.Matches(new Regex($@"{Regex.Escape(name)}:\s*{Regex.Escape(value)};", RegexOptions.CultureInvariant), LightBlock());

    [Fact]
    public void Every_token_family_is_present_in_the_light_block()
    {
        var map = Map(LightBlock());
        string[] names =
        [
            "--page", "--surface", "--surface-subtle", "--surface-panel", "--column-bg", "--field-bg", "--tint", "--tint-strong", "--ink", "--ink-muted", "--hairline", "--border-input", "--btn-border",
            "--glass", "--action", "--action-hover", "--on-action", "--focus", "--accent-soft", "--danger", "--on-danger", "--shadow-1", "--shadow-2", "--hero-1", "--hero-2",
            "--blue-bg", "--blue-fg", "--green-bg", "--green-fg", "--orange-bg", "--orange-fg", "--red-bg", "--red-fg", "--purple-bg", "--purple-fg", "--teal-bg", "--teal-fg", "--gray-bg", "--gray-fg",
            "--chart-1", "--chart-2", "--chart-3", "--chart-4", "--chart-5", "--chart-6", "--chart-grid", "--chart-axis",
            "--font-sans", "--weight-regular", "--weight-medium", "--weight-semibold", "--weight-bold", "--text-display", "--text-title", "--text-body", "--text-small", "--text-caption",
            "--radius-xs", "--radius-sm", "--radius-md", "--radius-lg", "--radius-full", "--control-h", "--duration",
        ];
        foreach (var n in names) { Assert.True(map.ContainsKey(n), n); }
        Assert.Equal("34px", map["--text-display"]);
        Assert.Equal("16px", map["--radius-lg"]);
        Assert.Equal("10px", map["--radius-sm"]);
        Assert.Equal("9999px", map["--radius-full"]);
        Assert.Equal("180ms", map["--duration"]);
    }

    [Fact]
    public void The_two_dark_blocks_are_identical_and_carry_the_dark_values()
    {
        var css = Css();
        var system = SystemDark().Match(css);
        var manual = ManualDark().Match(css);
        Assert.True(system.Success && manual.Success);
        Assert.Equal(Norm(system.Groups["body"].Value), Norm(manual.Groups["body"].Value));
        foreach (var expected in new[] { "--page:#1c1c1e", "--surface:#2c2c2e", "--ink:#f5f5f7", "--action:#6aa9ff", "--on-action:#0a1d38", "--danger:#ff8a80" })
        {
            Assert.Contains(expected, Norm(system.Groups["body"].Value).Replace(": ", ":", StringComparison.Ordinal), StringComparison.Ordinal);
        }
        var light = Map(LightBlock());
        foreach (var name in Map(manual.Groups["body"].Value).Keys.Where(k => k != "color-scheme")) { Assert.True(light.ContainsKey(name), $"{name} is overridden in Dark but not defined in Light"); }
    }

    [Fact]
    public void No_colour_literal_outside_the_token_block()
    {
        var css = Css();
        var end = css.IndexOf("/* end of tokens */", StringComparison.Ordinal);
        Assert.True(end > 0);
        Assert.DoesNotMatch(@"#[0-9a-fA-F]{3,8}\b", css[end..]);
        Assert.DoesNotMatch(@"\brgba?\(|\bhsla?\(", css[end..]);
        Assert.DoesNotMatch(@"--dafa-", css);
    }

    public static TheoryData<string, string, double> ContrastPairs()
    {
        var data = new TheoryData<string, string, double>();
        foreach (var theme in new[] { "light", "dark" })
        {
            (string Fg, string Bg, double Min)[] pairs =
            [
                ("--ink", "--surface", 7), ("--ink", "--page", 7), ("--ink-muted", "--surface", 4.5), ("--ink-muted", "--page", 4.5), ("--ink-muted", "--surface-panel", 4.5), ("--ink-muted", "--column-bg", 4.5), ("--ink", "--column-bg", 7),
                ("--action", "--surface", 4.5), ("--action", "--page", 4.5), ("--action", "--tint", 4.5), ("--on-action", "--action", 4.5), ("--on-danger", "--danger", 4.5),
                ("--danger", "--surface", 4.5), ("--border-input", "--surface", 3), ("--border-input", "--field-bg", 3),
                ("--blue-fg", "--blue-bg", 4.5), ("--green-fg", "--green-bg", 4.5), ("--orange-fg", "--orange-bg", 4.5), ("--red-fg", "--red-bg", 4.5),
                ("--purple-fg", "--purple-bg", 4.5), ("--teal-fg", "--teal-bg", 4.5), ("--gray-fg", "--gray-bg", 4.5),
                ("--chart-1", "--surface", 3), ("--chart-2", "--surface", 3), ("--chart-3", "--surface", 3), ("--chart-4", "--surface", 3), ("--chart-5", "--surface", 3), ("--chart-6", "--surface", 3),
                ("--chart-axis", "--surface", 4.5),
            ];
            foreach (var (fg, bg, min) in pairs) { data.Add(theme, fg + " on " + bg, min); }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ContrastPairs))]
    public void Key_colour_pairs_meet_the_contrast_minimum_in_both_themes(string theme, string pair, double min)
    {
        var map = Theme(theme == "dark");
        var parts = pair.Split(" on ");
        var ratio = Crm.Web.Services.Color.ContrastAgainst(map[parts[0]], map[parts[1]]);
        Assert.True(ratio >= min, $"{theme}: {pair} is {ratio:0.00}:1, needs {min}:1");
    }

    [Fact]
    public void The_default_accent_matches_the_token_and_passes_the_accent_rule()
    {
        Assert.Equal(Map(LightBlock())["--action"], Crm.Web.Services.Color.DefaultAccent.ToLowerInvariant());
        Assert.True(Crm.Web.Services.Color.AccentContrast(Crm.Web.Services.Color.DefaultAccent) >= 4.5);
        Assert.Equal(Map(LightBlock())["--tint"], Crm.Web.Services.Color.PanelBg.ToLowerInvariant());
    }

    [Fact]
    public void Shape_depth_and_type_follow_the_tokens()
    {
        var css = Css();
        var body = css[css.IndexOf("/* end of tokens */", StringComparison.Ordinal)..];
        foreach (Match m in Regex.Matches(body, @"border-radius:\s*([^;}]+)"))
        {
            Assert.Matches(@"^(0|var\(--radius-(?:xs|sm|md|lg|full|none)\))(?: (0|var\(--radius-(?:xs|sm|md|lg|full|none)\))){0,3}$", m.Groups[1].Value.Trim());
        }
        foreach (Match m in Regex.Matches(body, @"box-shadow:\s*([^;}]+)"))
        {
            Assert.Matches(@"^(none|var\(--shadow-(?:1|2|btn)\)|0 1px 3px var\(--focus-ring\)|0 0 0 5px var\(--focus-ring\))$", m.Groups[1].Value.Trim());
        }
        Assert.DoesNotMatch(@"font-weight:(?>\s*)(?!var\(--weight-(regular|medium|semibold|bold)\)|inherit)", css);
        Assert.DoesNotMatch(@"font:\s*(?!var\(--weight)[0-9]", css);
        Assert.Contains("--font-sans:-apple-system,BlinkMacSystemFont,\"SF Pro Text\",\"Segoe UI\",Roboto,\"Helvetica Neue\",Arial,sans-serif;", css, StringComparison.Ordinal);
        Assert.DoesNotContain("@font-face", css, StringComparison.Ordinal);
        Assert.DoesNotContain("@import", css, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"url\((?!/img/|#|""?data:)", css);
        Assert.DoesNotMatch(@"transition:[^;]*\d+m?s", body); // durations come from --duration
    }

    [Fact]
    public void Frosted_bars_have_a_solid_fallback_and_motion_respects_the_user_setting()
    {
        var css = Norm(Css());
        Assert.Matches(@"\.sidebar \{[^}]*background: var\(--surface\);", css); // solid first
        Assert.Matches(@"@supports \(\(-webkit-backdrop-filter: blur\(1px\)\) or \(backdrop-filter: blur\(1px\)\)\) \{ \.sidebar, \.topbar \{ background: var\(--glass\);", css);
        Assert.Contains("@media (prefers-reduced-motion: reduce) { :root { --duration:0ms; } }", css, StringComparison.Ordinal);
    }

    [Fact]
    public void The_dafa_logo_is_kept_and_its_soft_blue_copy_differs_only_in_fill_colours()
    {
        var dir = Path.Combine(WebRoot, "wwwroot", "img", "brand");
        var original = File.ReadAllText(Path.Combine(dir, "dafa-logo-primary-blue.svg"));
        var recoloured = File.ReadAllText(Path.Combine(dir, "dafa-logo-softblue.svg"));
        Assert.Equal(original.Replace("#00456c", "#1a66cc", StringComparison.Ordinal).Replace("#abadae", "#8cb8f0", StringComparison.Ordinal), recoloured);
        var rings = File.ReadAllText(Path.Combine(dir, "dafa-rings-gray.svg"));
        Assert.Equal(rings.Replace("#505152", "#4a90e2", StringComparison.Ordinal), File.ReadAllText(Path.Combine(dir, "dafa-rings-softblue.svg")));
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

    /// <summary>UX review H1: the dense-table row height (40px) is more specific than the card-view reset, so the phone block must reset both.</summary>
    [Fact]
    public void Phone_card_view_resets_the_dense_row_height_too()
    {
        var css = Norm(Css());
        var phone = css[css.IndexOf("@media (max-width: 639px) { table.data, table.data tbody", StringComparison.Ordinal)..];
        phone = phone[..phone.IndexOf(".act-text { display: none; }", StringComparison.Ordinal)];
        Assert.Matches(@"table\.data tbody tr, table\.data\.dense tbody tr \{[^}]*height: auto;", phone);
        // The card view: record link first, two key fields, chips and owner, actions last; Edit shows its text.
        foreach (var rule in new[] { "table.data td.c-name { order: 1;", "table.data td.c-k { order: 2;", "table.data td.c-m { order: 3;", "table.data td.act { order: 4;", "table.data td.act .act-text { display: inline; }" })
        {
            Assert.Contains(rule, phone, StringComparison.Ordinal);
        }
    }

    /// <summary>UX review M1: the rail rules belong to the sidebar's brand link, not to every .brand (the waking page has one too).</summary>
    [Fact]
    public void Collapsed_sidebar_rules_are_scoped_to_the_sidebar()
    {
        var css = Css();
        Assert.DoesNotMatch(@"data-sidebar=""collapsed""\] \.brand", css);
        Assert.Contains("html[data-sidebar=\"collapsed\"] .sidebar .brand .logo-full", css, StringComparison.Ordinal);
    }

    /// <summary>UX review M2: in Dark the current stage is not the same colour as the passed ones.</summary>
    [Fact]
    public void Dark_stage_track_tells_the_current_stage_from_passed_ones()
    {
        var css = Css();
        foreach (var block in new[] { SystemDark().Match(css).Groups["body"].Value, ManualDark().Match(css).Groups["body"].Value })
        {
            var b = Norm(block).Replace(": ", ":", StringComparison.Ordinal);
            Assert.Contains("--stage-passed:#5b9bf0", b, StringComparison.Ordinal);
            Assert.Contains("--stage-current:#ffffff", b, StringComparison.Ordinal);
        }
        Assert.Contains(".stage-track .passed .seg-bar { fill: var(--stage-passed); }", Norm(css), StringComparison.Ordinal);
        Assert.Contains(".stage-track .current .seg-bar { fill: var(--stage-current); }", Norm(css), StringComparison.Ordinal);
    }

    /// <summary>Cards are white surfaces with the layered shadow; board columns are the inset panel colour; sparklines are 44px.</summary>
    [Fact]
    public void Cards_board_columns_and_tiles_use_the_surface_tokens()
    {
        var css = Norm(Css());
        Assert.Contains(".panel { background: var(--surface); border: 1px solid var(--card-border); border-radius: var(--radius-lg); box-shadow: var(--shadow-1);", css, StringComparison.Ordinal);
        Assert.Contains(".board-col { flex: 1 0 272px; max-width: 320px; background: var(--column-bg); border-radius: var(--radius-lg);", css, StringComparison.Ordinal);
        Assert.Contains("background: var(--surface); border: 1px solid var(--card-border); border-radius: var(--radius-lg); box-shadow: var(--shadow-1); padding: var(--space-md) var(--space-lg);", css, StringComparison.Ordinal);
        Assert.Contains(".spark { display: block; width: 100%; height: 44px;", css, StringComparison.Ordinal);
    }

    /// <summary>UX review L1: six KPI columns follow the width of the content area, not the viewport.</summary>
    [Fact]
    public void Kpi_grid_switches_on_the_container_width()
    {
        var css = Norm(Css());
        Assert.Contains(".tiles-box { container-type: inline-size; }", css, StringComparison.Ordinal);
        Assert.Matches(@"@container \(min-width: 1[23]\d\dpx\) \{ \.tiles \{ grid-template-columns: repeat\(6,", css);
        Assert.DoesNotMatch(@"\.tile-value \{[^}]*\dvw", css);
    }
    /// <summary>Round 4 rollout: the column background token exists in both themes and the shared component patterns are in the CSS.</summary>
    [Fact]
    public void Rollout_components_use_tokens_and_the_column_token_exists_in_both_themes()
    {
        Assert.Equal("#edf1f7", Theme(dark: false)["--column-bg"]);
        Assert.Equal("#242426", Theme(dark: true)["--column-bg"]);
        var css = Norm(Css());
        foreach (var rule in new[]
        {
            ".deal-card { position: relative;", ".col-stats {", ".seg-radio {", ".signin-card {", ".form-group { background: var(--surface); border: 1px solid var(--card-border); border-radius: var(--radius-lg);",
            ".user-card + .user-card::before", ".filters .field:not(.search-field) {", ".stage-track { gap:", ".record-side .details dt, .record-side .details dd {", ".pagination .button { border-radius: var(--radius-full);",
        })
        {
            Assert.Contains(rule, css, StringComparison.Ordinal);
        }
    }

    /// <summary>The page-level .record rule is a grid, so a table cell must not carry that class (it turned the name cell into a grid and misaligned the row).</summary>
    [Fact]
    public void Table_name_cells_do_not_use_the_record_page_grid_class()
    {
        var td = File.ReadAllText(Path.Combine(WebRoot, "Pages", "Shared", "_Td.cshtml"));
        Assert.Contains("\"rec-link\"", td, StringComparison.Ordinal);
        Assert.DoesNotContain("\"record\"", td, StringComparison.Ordinal);
    }

    [Fact]
    public void Absolutely_positioned_hidden_text_is_anchored_so_it_cannot_widen_the_page()
    {
        // D1: .visually-hidden is position:absolute; without a positioned ancestor it sits at the page edge and makes the whole page scroll sideways.
        var css = Css();
        Assert.Matches(@"\.board-col \{[^}]*position: relative;", css);
        Assert.Matches(@"\.tile-trend \{ position: relative;", css);
    }

    [Fact]
    public void Dashboard_trend_pill_is_capped_and_truncates_on_phones()
    {
        // D2
        var css = Css();
        Assert.Contains(".tile-trend { max-width: 50%; min-width: 0; }", css, StringComparison.Ordinal);
        Assert.Contains(".tile-trend > span:not(.visually-hidden) { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Light_brand_preview_pins_the_light_tokens()
    {
        // D3: the Light sample must not inherit the Dark action colour (2.4:1 on white).
        Assert.Contains("[data-preview=\"light\"] { --action:#1a66cc;", Tokens(), StringComparison.Ordinal);
    }
}
