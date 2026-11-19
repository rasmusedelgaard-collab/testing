using Crm.Web.Domain;
using Crm.Web.Pages.Entity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Tests.Http;

/// <summary>Renders the shared partials with in-memory models (no database), so their Razor logic is exercised in every CI run.</summary>
public sealed class PartialRenderTests : IDisposable
{
    private readonly CrmFactory _f = new();

    public void Dispose() => _f.Dispose();

    private async Task<string> RenderHtmlAsync(string view, object model)
    {
        using var scope = _f.Services.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        var found = scope.ServiceProvider.GetRequiredService<IRazorViewEngine>().GetView(null, view, false);
        Assert.True(found.Success, string.Join(", ", found.SearchedLocations ?? []));
        var viewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary()) { Model = model };
        await using var writer = new StringWriter();
        var tempData = new TempDataDictionary(http, scope.ServiceProvider.GetRequiredService<ITempDataProvider>());
        await found.View!.RenderAsync(new ViewContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), found.View, viewData, tempData, writer, new HtmlHelperOptions()));
        return writer.ToString();
    }

    private async Task<AngleSharp.Html.Dom.IHtmlDocument> RenderAsync(string view, object model) =>
        new AngleSharp.Html.Parser.HtmlParser().ParseDocument("<table><tbody><tr>" + await RenderHtmlAsync(view, model) + "</tr></tbody></table>");

    private async Task<AngleSharp.Html.Dom.IHtmlDocument> RenderForm2(string view, object model) =>
        new AngleSharp.Html.Parser.HtmlParser().ParseDocument(await RenderHtmlAsync(view, model));

    private Task<AngleSharp.Html.Dom.IHtmlDocument> RenderForm(FormPage page) => RenderForm2("/Pages/Shared/_EntityForm.cshtml", page);

    private static AngleSharp.Dom.IElement Td(AngleSharp.Html.Dom.IHtmlDocument d) => d.QuerySelector("td")!;

    [Fact]
    public async Task Table_cells_render_owner_stage_segment_type_and_notes()
    {
        Assert.Equal("Anna Holm", (await Cell(new Cell("Anna Holm", Kind: "owner"))).QuerySelector(".owner span:last-child")!.TextContent);
        var stage = await Cell(new Cell("Proposal", Kind: "stage", Aux: "open", N: 3, Of: 4));
        Assert.Equal(3, stage.QuerySelectorAll(".pip.on").Length);
        Assert.Equal(4, stage.QuerySelectorAll(".pip").Length);
        Assert.Contains("Proposal", stage.TextContent, StringComparison.Ordinal);
        Assert.NotNull((await Cell(new Cell("Closed won", Kind: "stage", Aux: "won"))).QuerySelector(".chip.won"));
        Assert.NotNull((await Cell(new Cell("Wind", Kind: "segment", Aux: "wind"))).QuerySelector(".chip.segment.wind"));
        Assert.Equal("Call", (await Cell(new Cell("Call", Kind: "type", Aux: "call"))).QuerySelector(".owner span:last-child")!.TextContent);
        var mail = await Cell(new Cell("a@b.dk", "mailto:a@b.dk", Kind: "email"));
        Assert.Equal("mailto:a@b.dk", mail.QuerySelector("a")!.GetAttribute("href"));
        var note = await Cell(new Cell("15.12.2026", Note: "Past close date"));
        Assert.Contains("Past close date", note.QuerySelector(".chip.warn")!.TextContent, StringComparison.Ordinal);
        Assert.NotNull((await Cell(new Cell("1.1.2026", Note: "Overdue", NoteDanger: true))).QuerySelector(".chip.danger"));
        var evil = await Cell(new Cell("<b>x</b>", "/accounts/1"));
        Assert.Equal("<b>x</b>", evil.QuerySelector("a")!.TextContent);
        Assert.Null(evil.QuerySelector("b"));
    }

    private async Task<AngleSharp.Dom.IElement> Cell(Cell c) => Td(await RenderAsync("/Pages/Shared/_Td.cshtml", new TdModel(c, "Label")));

    [Fact]
    public async Task Done_cell_is_an_icon_button_with_a_unique_name_and_a_safe_return()
    {
        var td = await Cell(new Cell("Open", Kind: "done", DoneForm: new DoneForm("/activities/5/done", "Call Nordvind", false, "/activities?status=open")));
        var button = td.QuerySelector("form button")!;
        Assert.Equal("Mark 'Call Nordvind' as done", button.GetAttribute("aria-label"));
        Assert.Equal("/activities/5/done", td.QuerySelector("form")!.GetAttribute("action"));
        Assert.Equal("/activities?status=open", td.QuerySelector("input[name=returnTo]")!.GetAttribute("value"));
        Assert.NotNull(td.QuerySelector("input[name=_csrf]"));
        var done = await Cell(new Cell("Done", Kind: "done", DoneForm: new DoneForm("/activities/5/undone", "Call Nordvind", true, "/")));
        Assert.Equal("Mark 'Call Nordvind' as not done", done.QuerySelector("button")!.GetAttribute("aria-label"));
        Assert.Equal("true", done.QuerySelector("button")!.GetAttribute("aria-pressed"));
    }

    [Fact]
    public async Task Timeline_partial_renders_groups_items_actions_and_truncates_long_descriptions()
    {
        var longText = new string('a', 300);
        var groups = new List<TimelineGroup>
        {
            new("Overdue", [new TimelineItem("task", "Task: Send quote", "/activities/1", true, "10.11.2026", "9 days overdue", true, "Anna Holm", "Nordvind", "/accounts/3", longText, false, "/activities/1", new DoneForm("/activities/1/done", "Send quote", false, "/opportunities/7"))]),
            new("November 2026", [new TimelineItem("change", "Owner changed from A to B", null, true, "18.11.2026 09:12", "", false, "Mette Holm", null, null, "", false, null, null)]),
        };
        var doc = await RenderForm2("/Pages/Shared/_Timeline.cshtml", groups);
        var tl = doc.QuerySelector("ol.timeline")!;
        Assert.Equal(["Overdue", "November 2026"], tl.Children.Select(li => li.QuerySelector("h3")!.TextContent));
        Assert.Contains("9 days overdue", tl.QuerySelector(".chip.danger")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("Mark done: Send quote", tl.QuerySelector("form.tl-action button")!.TextContent);
        Assert.Equal("/activities/1", tl.QuerySelector(".tl-desc a")!.GetAttribute("href"));
        Assert.True(tl.QuerySelector(".tl-desc")!.TextContent.Length < 300);
        Assert.NotNull(tl.QuerySelector("strong")); // owner changes stay bold
    }

    [Fact]
    public async Task Form_partial_groups_fields_with_pairs_a_fieldset_and_the_error_summary()
    {
        var fields = new List<FormField>
        {
            new("ActivityType", "Type (required)", "radio", "task", Required: true, Options: [new SelectOption("call", "Call"), new SelectOption("task", "Task", true)]),
            new("Subject", "Subject (required)", "text", "", Error: "Enter a subject.", Required: true),
            new("AccountId", "Related account", "select", "", Options: [new SelectOption("", "— none —")]),
            new("DueDate", "Due date", "date", ""), new("DueTime", "Due time", "time", ""),
            new("IsDone", "Done", "checkbox", ""), new("Description", "Description", "textarea", ""),
            new("OwnerId", "Owner (required)", "select", "1", Required: true, Options: [new SelectOption("1", "Me", true)]),
        };
        var page = new FormPage("New activity", "/activities", "/activities", fields, [("Subject", "Enter a subject.")], "", "activities");
        var doc = await RenderForm(page);
        Assert.Equal("Fix 1 problem to save", doc.QuerySelector("#error-summary h2")!.TextContent);
        Assert.Equal("#f-Subject", doc.QuerySelector("#error-summary a")!.GetAttribute("href"));
        Assert.Equal(["Activity", "Timing", "Details"], doc.QuerySelectorAll(".form-group > h2").Select(h => h.TextContent));
        Assert.Equal("Related to (link at least one)", doc.QuerySelector(".form-group > fieldset > legend.group-label")!.TextContent);
        Assert.Equal(2, doc.QuerySelector(".form-pair")!.QuerySelectorAll(".field").Length);
        Assert.Equal("true", doc.QuerySelector("#f-Subject")!.GetAttribute("aria-invalid"));
        Assert.Equal("e-Subject", doc.QuerySelector("#f-Subject")!.GetAttribute("aria-describedby"));
        Assert.Equal("Activities", doc.QuerySelector(".breadcrumb li a")!.TextContent);
        Assert.Equal("New activity", doc.QuerySelector(".breadcrumb li[aria-current=page]")!.TextContent);
        Assert.Equal("Save", doc.QuerySelector("button.primary")!.TextContent);
        Assert.NotNull(doc.QuerySelector("input[name=_csrf]"));
        foreach (var id in new[] { "f-ActivityType", "f-ActivityType-1", "f-Subject", "f-AccountId", "f-DueDate", "f-DueTime", "f-IsDone", "f-Description", "f-OwnerId" })
        {
            Assert.NotNull(doc.QuerySelector("#" + id));
        }
    }

    [Fact]
    public async Task Confirm_partial_shows_the_question_the_message_and_a_way_back()
    {
        var confirm = new ConfirmPage("Mark as won", "Mark Deal as won? The stage changes to Closed won.", "/opportunities/7/won", [("rowVer", "AAAAAAAAB9E="), ("confirm", "1")], "Mark as won", "/opportunities/7", "Deal");
        var doc = await RenderForm2("/Pages/Shared/_Confirm.cshtml", confirm);
        Assert.Equal("Mark as won", doc.QuerySelector("h1")!.TextContent);
        Assert.Contains("The stage changes to Closed won.", doc.QuerySelector("main, .confirm")!.QuerySelector("p")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("1", doc.QuerySelector("input[name=confirm]")!.GetAttribute("value"));
        Assert.Equal("/opportunities/7", doc.QuerySelector("a.button.quiet")!.GetAttribute("href"));
        Assert.Equal(["Opportunities", "Deal", "Mark as won"], doc.QuerySelectorAll(".breadcrumb li").Select(li => li.TextContent));
    }

    [Fact]
    public async Task Settings_nav_keeps_its_labels_and_marks_the_current_page()
    {
        var doc = await RenderForm2("/Pages/Shared/_SettingsNav.cshtml", "stages");
        Assert.Equal(["General", "Currency & locale", "FX rates", "Pipeline stages", "Picklists", "Visibility", "Users & teams"], doc.QuerySelectorAll("nav.subnav a").Select(a => a.TextContent));
        Assert.Equal("page", doc.QuerySelector("nav.subnav a[href='/settings/stages']")!.GetAttribute("aria-current"));
    }
}
