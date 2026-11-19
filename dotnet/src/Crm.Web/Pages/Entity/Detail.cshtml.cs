using Crm.Web.Data;
using Crm.Web.Domain;
using Crm.Web.Infrastructure;
using Crm.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Crm.Web.Pages.Entity;

public sealed record DoneAction(string Action, string Label, string ReturnTo);

public sealed record StageActions(string RowVer, IReadOnlyList<PicklistValue> LostReasons, string LostError);

public sealed record TabInfo(string Key, string Label, int Count, string Href, bool Active);

public sealed record TabTable(string Title, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<Cell>> Rows, string NewHref, string NewLabel);

public sealed record CloseResult(string Kind, string Message = "", bool Moved = false, ConfirmPage? Extra = null, object? Data = null);

public sealed record Fact(string Label, string Value, string? Sub = null, string? Chip = null);

public sealed record TrackSegment(string Name, string State);

/// <summary>Stage track of an opportunity: open stages in Settings order (passed, current, later); a lost one has no track, only the reason.</summary>
public sealed record StageTrack(IReadOnlyList<TrackSegment> Segments, string StageType, string StageName, string LostReason, int Index, int Count);

public sealed record ConfirmPage(string Title, string Message, string Action, IReadOnlyList<(string Name, string Value)> Fields, string Label, string Cancel, string RecordName = "");

/// <summary>
/// Record detail (GET), update (POST /{entity}/{id}), opportunity won/lost (POST /opportunities/{id}/won|lost) and activity
/// done/undone (POST /activities/{id}/done|undone). The extra routes are added in Program.cs; all share this page so that a
/// validation problem can re-render the detail or form view.
/// </summary>
public sealed class DetailModel(IRlsDb rls, IRlsContextAccessor identity, TimeProvider time) : EntityPageModel(rls, identity, time)
{
    public ConfirmPage? Confirm { get; private set; }

    public Row? Row { get; private set; }

    public string RecordName { get; private set; } = "";

    public IReadOnlyList<(string Label, string Text, string? Href)> Details { get; private set; } = [];

    public bool CanDelete { get; private set; }

    public DoneAction? DoneAction { get; private set; }

    public StageActions? StageActions { get; private set; }

    public IReadOnlyList<TabInfo> Tabs { get; private set; } = [];

    public TabTable? TabTable { get; private set; }

    public IReadOnlyList<TimelineGroup> Timeline { get; private set; } = [];

    public int ActivityCount { get; private set; }

    public IReadOnlyList<Fact> Facts { get; private set; } = [];

    public StageTrack? Track { get; private set; }

    public string LogHref { get; private set; } = "";

    public bool ShowActivityTab { get; private set; }

    public string SegmentCode { get; private set; } = "";

    public string CreatedAt { get; private set; } = "";

    public string CreatedBy { get; private set; } = "";

    public string ModifiedAt { get; private set; } = "";

    public string ModifiedBy { get; private set; } = "";

    public bool Edited { get; private set; }

    public Task<IActionResult> OnGetAsync([FromRoute] string? entity, [FromRoute] int id) => ShowDetailAsync(entity ?? "", id);

    private sealed record DetailData(Row Row, Lookups Lk, string Today, IReadOnlyDictionary<string, IReadOnlyList<Row>> Related, IReadOnlyList<HistoryRow> History);

    /// <summary>Everything the detail view needs, read inside the request's single transaction.</summary>
    private async Task<DetailData?> LoadDetailAsync(CrmRepo repo, int id)
    {
        var def = Def;
        var row = await repo.GetAsync(def.Key, id);
        if (row is null)
        {
            return null;
        }
        var lk = await repo.LookupsAsync();
        var today = Today(lk.Settings);
        async Task<IReadOnlyList<Row>> Rel(string e, string filter)
        {
            var filters = new Dictionary<string, string>(StringComparer.Ordinal) { [filter] = id.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            if (e is "opportunities" or "activities") { filters["status"] = "all"; }
            var q = new ListQuery("", 1, 100, EntityDefinitions.All[e].DefaultSort, "asc", filters, today);
            return (await repo.ListAsync(e, q)).Rows;
        }
        var related = new Dictionary<string, IReadOnlyList<Row>>(StringComparer.Ordinal);
        if (def.Key == "accounts") { related["contacts"] = await Rel("contacts", "account"); related["opportunities"] = await Rel("opportunities", "account"); related["activities"] = await Rel("activities", "account"); }
        if (def.Key == "contacts") { related["opportunities"] = await Rel("opportunities", "contact"); related["activities"] = await Rel("activities", "contact"); }
        if (def.Key == "opportunities") { related["activities"] = await Rel("activities", "opportunity"); }
        return new DetailData(row, lk, today, related, await repo.HistoryAsync(def.Key, id));
    }

    private async Task<IActionResult> ShowDetailAsync(string entity, int id)
    {
        SetEntity(entity);
        var data = await RunAsync(repo => LoadDetailAsync(repo, id)) ?? throw new NotFoundException();
        return ApplyDetail(data, id, null);
    }

    private IActionResult ApplyDetail(DetailData data, int id, string? lostError)
    {
        var def = Def;
        var tabParam = Request.Query.TryGetValue("tab", out var tv) ? tv.ToString() : "";
        var f = new Formatter(data.Lk.Settings.LocaleCode, data.Lk.Settings.Timezone);
        var row = data.Row;
        Row = row;
        RecordName = EntityViews.RecordName(def.Key, row);
        var detailPath = $"{def.Path}/{id}";
        var linkParam = def.Key == "accounts" ? "accountId" : def.Key == "contacts" ? "contactId" : "opportunityId";
        LogHref = $"/activities/new?{linkParam}={id}";
        // Records with related lists get tabs: the merged Activity timeline first (the default), then Contacts / Opportunities.
        var activities = data.Related.GetValueOrDefault("activities") ?? [];
        var timeline = def.Key == "activities" || activities.Count > 0 || data.History.Count > 0
            ? TimelineBuilder.Build(activities, data.History, f, data.Today, detailPath, def.Key, id)
            : [];
        Timeline = timeline;
        ActivityCount = TimelineBuilder.Count(timeline);
        var tabs = new List<(string Key, string Label, int Count)>();
        if (def.Key != "activities") { tabs.Add(("activity", "Activity", ActivityCount)); }
        foreach (var kv in data.Related.Where(kv => kv.Key != "activities"))
        {
            tabs.Add((kv.Key, EntityDefinitions.All[kv.Key].Plural, kv.Value.Count));
        }
        var wanted = tabParam == "activities" ? "activity" : tabParam;
        var active = tabs.FirstOrDefault(t => t.Key == wanted);
        if (active.Key is null && tabs.Count > 0) { active = tabs[0]; }
        Tabs = tabs.Select(t => new TabInfo(t.Key, t.Label, t.Count, $"{detailPath}?tab={t.Key}", t.Key == active.Key)).ToList();
        ShowActivityTab = active.Key == "activity";
        if (active.Key is not null && active.Key != "activity")
        {
            TabTable = new TabTable(active.Label, EntityViews.ColumnsFor(active.Key).Select(c => c.Label).ToList(),
                data.Related[active.Key].Select(r => EntityViews.CellsFor(active.Key, r, f, data.Today, detailPath, data.Lk.Stages)).ToList(),
                $"{EntityDefinitions.All[active.Key].Path}/new?{linkParam}={id}",
                $"New {EntityDefinitions.All[active.Key].Singular}");
        }
        SegmentCode = def.Key == "accounts" ? row.S("IndustryCode") : "";
        BuildFacts(data, f, row);
        Details = EntityViews.DetailRows(def.Key, row, f);
        CanDelete = EntityViews.CanDelete(Ctx, row);
        CreatedAt = f.DateTime(row.S("CreatedAt"));
        CreatedBy = row.S("CreatedByName");
        ModifiedAt = f.DateTime(row.S("ModifiedAt"));
        ModifiedBy = row.S("ModifiedByName");
        Edited = row.S("ModifiedAt") != row.S("CreatedAt") || row.Int("ModifiedBy") != row.Int("CreatedBy");
        StageActions = def.Key == "opportunities" && row.S("StageType") == "open"
            ? new StageActions(row.S("RowVer"), data.Lk.List("lost_reason").Where(p => p.Active).ToList(), lostError ?? "")
            : null;
        DoneAction = def.Key == "activities"
            ? new DoneAction($"{detailPath}/{(row.Bool("IsDone") ? "undone" : "done")}", row.Bool("IsDone") ? "Mark as not done" : "Mark as done", detailPath)
            : null;
        ViewData["Title"] = $"{Cap(def.Singular)} · {RecordName}";
        if (lostError is not null)
        {
            Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        }
        return Page();
    }

    private void BuildFacts(DetailData data, Formatter f, Row row)
    {
        var set = data.Lk.Settings;
        switch (Def.Key)
        {
            case "opportunities":
                var open = row.S("StageType") == "open";
                var amount = row.Dec("Amount");
                var currency = row.S("CurrencyCode").Trim();
                var close = row.S("CloseDate");
                var past = open && string.CompareOrdinal(close, data.Today) < 0;
                Facts =
                [
                    new("Amount", f.Money(amount, currency)),
                    new("Weighted amount", f.Money(Math.Round(amount * row.Dec("Probability")) / 100m, currency)),
                    new("Close date", f.Date(close), open ? TimelineBuilder.RelativeDays(close, data.Today, false) : null, past ? "Past close date" : null),
                    new("Probability", NumberFormats.Percent(row.Dec("Probability") / 100m, set.LocaleCode)),
                ];
                Track = BuildTrack(data.Lk.Stages, row);
                break;
            case "accounts":
                var opps = data.Related.GetValueOrDefault("opportunities") ?? [];
                var contacts = data.Related.GetValueOrDefault("contacts") ?? [];
                var last = (data.Related.GetValueOrDefault("activities") ?? [])
                    .Select(a => a.S("DueDate") is { Length: > 0 } d ? d : a.S("CreatedAt").Length >= 10 ? a.S("CreatedAt")[..10] : "")
                    .Where(d => d.Length > 0 && string.CompareOrdinal(d, data.Today) <= 0).OrderBy(d => d, StringComparer.Ordinal).LastOrDefault() ?? "";
                Facts =
                [
                    new("Open opportunities", opps.Count(o => o.S("StageType") == "open").ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    new("Contacts", contacts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    new("Last activity", last.Length > 0 ? f.Date(last) : "–"),
                ];
                break;
        }
    }

    private static StageTrack BuildTrack(IReadOnlyList<Stage> stages, Row row)
    {
        var type = row.S("StageType");
        var open = stages.Where(s => s.Type == "open").OrderBy(s => s.SortOrder).ToList();
        var currentIdx = open.FindIndex(s => s.Id == row.Int("StageId"));
        var segs = type == "lost" ? [] : open.Select((s, i) => new TrackSegment(s.Name, type == "won" || i < currentIdx ? "passed" : i == currentIdx ? "current" : "later")).ToList();
        return new StageTrack(segs, type, row.S("StageName"), row.S("LostReasonLabel"), currentIdx + 1, open.Count);
    }

    /// <summary>Update, close (won/lost) or done/undone, depending on the route that was matched.</summary>
    public async Task<IActionResult> OnPostAsync([FromRoute] string? entity, [FromRoute] int id, [FromRoute] string? outcome, [FromRoute] string? verb)
    {
        if (outcome is not null)
        {
            return await CloseAsync("opportunities", id, outcome);
        }
        if (verb is not null)
        {
            return await DoneAsync(id, verb == "done");
        }
        return await UpdateAsync(entity ?? "", id);
    }

    private async Task<IActionResult> UpdateAsync(string entity, int id)
    {
        SetEntity(entity);
        var def = Def;
        var body = FormBody();
        var rowVer = body.GetValueOrDefault("rowVer") ?? "";
        var meta = MetaOf();
        var (outcome, name) = await RunAsync(async repo =>
        {
            var current = await repo.GetAsync(def.Key, id);
            if (current is null)
            {
                return (new SaveOutcome("notfound", null, new Dictionary<string, string>()), "");
            }
            var lk = await repo.LookupsAsync();
            var env = await FormEnvAsync(repo, lk, current);
            var result = await PrepareAsync(repo, lk, env, body, current);
            var recName = EntityViews.RecordName(def.Key, current);
            if (!result.Ok)
            {
                return (new SaveOutcome("invalid", env, result.Errors), recName);
            }
            UpdateResult r;
            try
            {
                r = await repo.UpdateAsync(def.Key, id, result.Values, rowVer, meta);
            }
            catch (ForbiddenException)
            {
                // The database refused the resulting row (33504): keep the user's input and say why.
                return (new SaveOutcome("invalid", env, new Dictionary<string, string> { ["OwnerId"] = WriteBlocked }), recName);
            }
            if (r != UpdateResult.Ok)
            {
                return (new SaveOutcome(r == UpdateResult.Conflict ? "conflict" : "notfound", null, new Dictionary<string, string>()), recName);
            }
            var visible = await repo.GetAsync(def.Key, id) is not null;
            var owner = await repo.OwnerByIdAsync((int)result.Values["OwnerId"]!);
            return (new SaveOutcome("saved", env, result.Errors, id, visible, owner?.Name ?? ""), recName);
        });
        switch (outcome.Kind)
        {
            case "notfound": throw new NotFoundException();
            case "conflict": throw new EditConflictException($"{def.Path}/{id}");
            case "invalid":
                return RenderForm(BuildForm($"Edit {name}", $"{def.Path}/{id}", $"{def.Path}/{id}", body, outcome.Errors, outcome.Env!, rowVer));
            default:
                return AfterSave(id, outcome.Visible, outcome.OwnerName);
        }
    }

    private async Task<IActionResult> DoneAsync(int id, bool done)
    {
        SetEntity("activities");
        var meta = MetaOf();
        var ok = await RunAsync(repo => repo.SetDoneAsync(id, done, meta));
        if (!ok)
        {
            throw new NotFoundException();
        }
        Flash(done ? "Marked as done." : "Marked as not done.");
        return SeeOther(Crm.Web.Auth.ReturnTo.Safe(FormBody().GetValueOrDefault("returnTo"), Def.Path));
    }

    private async Task<IActionResult> CloseAsync(string entity, int id, string outcome)
    {
        SetEntity(entity);
        var def = Def;
        var body = FormBody();
        var confirmed = body.GetValueOrDefault("confirm") == "1";
        var rowVer = body.GetValueOrDefault("rowVer") ?? "";
        var meta = MetaOf();
        var lostRaw = body.GetValueOrDefault("lostReason") ?? "";
        var res = await RunAsync(repo => CloseCoreAsync(repo, outcome, id, rowVer, lostRaw, confirmed, meta, async r => await LoadDetailAsync(r, id)));
        switch (res.Kind)
        {
            case "notfound": throw new NotFoundException();
            case "conflict": throw new EditConflictException($"{def.Path}/{id}");
            case "lostError": return ApplyDetail((DetailData)res.Data!, id, "Choose why this was lost.");
            case "confirm":
                Confirm = res.Extra;
                ViewData["Title"] = res.Extra!.Title;
                return Page();
            case "error":
                Flash(res.Message, "error");
                break;
            default:
                Flash($"Opportunity marked as {outcome}.{(res.Moved ? " Close date set to today." : "")}");
                break;
        }
        return SeeOther($"{def.Path}/{id}");
    }
}
