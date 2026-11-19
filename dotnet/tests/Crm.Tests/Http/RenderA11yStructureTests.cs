using AngleSharp.Dom;
using Crm.Web.Pages;
using Crm.Web.Pages.Entity;
using Crm.Web.Pages.Pipeline;

namespace Crm.Tests.Http;

/// <summary>
/// Structural accessibility rules (ui-redesign 7, a subset axe would flag) checked on full-page renders of the redesigned templates in every CI run.
/// Colour contrast and layout need a browser and are not covered here.
/// </summary>
public sealed class RenderA11yStructureTests : IDisposable
{
    private readonly CrmFactory _f = new();

    public void Dispose() => _f.Dispose();

    private static string Name(IElement e)
    {
        var aria = e.GetAttribute("aria-label");
        if (!string.IsNullOrWhiteSpace(aria)) { return aria; }
        var by = e.GetAttribute("aria-labelledby");
        if (by is not null)
        {
            return string.Join(" ", by.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(id => e.Owner!.GetElementById(id)?.TextContent ?? ""));
        }
        var text = e.TextContent;
        var imgAlt = string.Concat(e.QuerySelectorAll("img[alt]").Select(i => i.GetAttribute("alt")));
        return string.IsNullOrWhiteSpace(text) ? imgAlt : text;
    }

    private static void AssertStructure(IDocument doc, string where)
    {
        var dup = doc.QuerySelectorAll("[id]").GroupBy(e => e.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dup.Count == 0, $"{where}: duplicate ids {string.Join(", ", dup)}");
        Assert.True(doc.QuerySelectorAll("h1").Length == 1, $"{where}: expected exactly one h1");
        Assert.True(doc.QuerySelectorAll("main").Length == 1, $"{where}: expected exactly one main");
        Assert.Equal("en", doc.DocumentElement.GetAttribute("lang"));
        Assert.False(string.IsNullOrWhiteSpace(doc.Title), $"{where}: page title missing");
        Assert.True(doc.QuerySelectorAll("img").All(i => i.HasAttribute("alt")), $"{where}: img without alt");
        foreach (var b in doc.QuerySelectorAll("button, a[href], summary"))
        {
            Assert.True(!string.IsNullOrWhiteSpace(Name(b)), $"{where}: {b.TagName} without an accessible name: {b.OuterHtml[..Math.Min(120, b.OuterHtml.Length)]}");
        }
        foreach (var c in doc.QuerySelectorAll("input:not([type=hidden]):not([type=submit]):not([type=button]), select, textarea"))
        {
            var labelled = (c.Id is { Length: > 0 } && doc.QuerySelector($"label[for='{c.Id}']") is not null) || c.GetAttribute("aria-label") is { Length: > 0 } || c.GetAttribute("aria-labelledby") is { Length: > 0 } || c.Closest("label") is not null;
            Assert.True(labelled, $"{where}: unlabelled control {c.OuterHtml[..Math.Min(120, c.OuterHtml.Length)]}");
        }
        foreach (var t in doc.QuerySelectorAll("[aria-labelledby]"))
        {
            foreach (var id in t.GetAttribute("aria-labelledby")!.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                Assert.True(doc.GetElementById(id) is not null, $"{where}: aria-labelledby points at missing #{id}");
            }
        }
        // Every navigation landmark has a name; the main nav and the theme group are present with the persistent controls.
        Assert.All(doc.QuerySelectorAll("nav"), n => Assert.True(!string.IsNullOrWhiteSpace(n.GetAttribute("aria-label")), $"{where}: nav without aria-label"));
        Assert.Equal("Skip to main content", doc.QuerySelector("a.skip-link")?.TextContent);
        Assert.Equal("#main", doc.QuerySelector("a.skip-link")!.GetAttribute("href"));
        Assert.NotNull(doc.GetElementById("main"));
        // Progressive enhancement: the page ships as no-js and the theme choice is a real form (works without script).
        Assert.True(doc.DocumentElement.ClassList.Contains("no-js"), $"{where}: html must start as no-js");
        var theme = doc.QuerySelector("form.theme-group");
        Assert.Equal("post", theme!.GetAttribute("method"), ignoreCase: true);
        Assert.Equal(["Match system", "Light", "Dark"], theme.QuerySelectorAll("button[name=theme]").Select(b => b.TextContent.Trim()).ToArray());
        Assert.Equal(1, theme.QuerySelectorAll("button[aria-pressed=true]").Length);
        // No inline script and no inline event handlers (CSP without unsafe-inline).
        Assert.Empty(doc.QuerySelectorAll("script:not([src])"));
        Assert.DoesNotContain(doc.All.SelectMany(e => e.Attributes), a => a.Name.StartsWith("on", StringComparison.Ordinal) && a.Name.Length > 3 && a.Name != "one");
    }

    [Fact]
    public async Task Board_search_stage_confirm_and_error_pages_keep_the_structure()
    {
        var options = new List<MoveOption> { new(1, "Qualification (current)", true), new(2, "Needs analysis", false) };
        var cards = new List<DealCard>
        {
            new(7, "Blade root seals 2027", "Nordvind Rotor A/S", 3, "EUR 240.000,00", "15.12.2026", true, "Anna Holm", "AAAAAAAAB9E=", false, options),
            new(8, "Nacelle noise insulation", "Nordvind Rotor A/S", 3, "EUR 1.000,00", "15.12.2026", false, "Anna Holm", "AAAAAAAAB9E=", false, options),
        };
        var cols = new List<BoardColumn> { new(1, "Qualification", "10 %", 2, "6,0 mio. kr.", "DKK 6.000.000,00", "0,6 mio. kr.", 83, [], cards, "/opportunities?status=open&stage=1", false) };
        var board = PageRenderHarness.Model<Crm.Web.Pages.Pipeline.IndexModel>(("Columns", cols), ("Filters", new List<FilterField> { new("owner", "Owner", [new SelectOption("", "All I can see")], "") }), ("TotalShort", "6,0 mio. kr."), ("TotalFull", "DKK 6.000.000,00"), ("TotalCount", 2), ("Notes", Array.Empty<string>()));
        var doc = await PageRenderHarness.RenderAsync(_f, "/Pipeline/Index", board, PageRenderHarness.User(), new Dictionary<string, object?> { ["ActiveNav"] = "pipeline", ["Title"] = "Pipeline" });
        AssertStructure(doc, "board");
        // Each select/button pair on the board has its own name (ui-redesign 7.7): two cards, two different names.
        var names = doc.QuerySelectorAll("form.move-form button").Select(b => b.TextContent).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.All(doc.QuerySelectorAll("form.move-form"), f => Assert.NotNull(f.QuerySelector("input[name=_csrf], input[name=__RequestVerificationToken]")));

        var search = await PageRenderHarness.RenderAsync(_f, "/Search", PageRenderHarness.Model<SearchModel>(("Text", "nord"), ("Groups", new List<SearchGroup>()), ("TotalResults", 0)), PageRenderHarness.User(), new Dictionary<string, object?> { ["Title"] = "Search" });
        AssertStructure(search, "search");

        var notFound = PageRenderHarness.Model<ErrorModel>(("Heading", "We can't find that page or record."), ("Message", "It may have been deleted, or you may not have access."), ("Reference", "abc"), ("Status", 404), ("PageTitle", "Not found"));
        AssertStructure(await PageRenderHarness.RenderAsync(_f, "/Errors/Error", notFound, PageRenderHarness.User()), "404");
    }
}
