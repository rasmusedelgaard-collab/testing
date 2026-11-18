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

public sealed record CloseResult(string Kind, string Message = "", bool Moved = false, ConfirmPage? Extra = null);

public sealed record HistoryLine(string At, string By, string Text, bool Owner);

public sealed record ConfirmPage(string Title, string Message, string Action, IReadOnlyList<(string Name, string Value)> Fields, string Label, string Cancel);

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

    public IReadOnlyList<HistoryLine> History { get; private set; } = [];

    public string CreatedAt { get; private set; } = "";

    public string CreatedBy { get; private set; } = "";

    public string ModifiedAt { get; private set; } = "";

    public string ModifiedBy { get; private set; } = "";

    public bool Edited { get; private set; }

    public Task<IActionResult> OnGetAsync(string? entity, int id) => ShowDetailAsync(entity ?? "", id, null);

    private async Task<IActionResult> ShowDetailAsync(string entity, int id, string? lostError)
    {
        SetEntity(entity);
        var def = Def;
        var tabParam = Request.Query.TryGetValue("tab", out var tv) ? tv.ToString() : "";
        var data = await RunAsync(async repo =>
        {
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
            var history = await repo.HistoryAsync(def.Key, id);
            return new { Row = row, Lk = lk, Today = today, Related = related, History = history };
        }) ?? throw new NotFoundException();

        var f = new Formatter(data.Lk.Settings.LocaleCode, data.Lk.Settings.Timezone);
        var row = data.Row;
        Row = row;
        RecordName = EntityViews.RecordName(def.Key, row);
        var detailPath = $"{def.Path}/{id}";
        var tabs = data.Related.Select(kv => (Key: kv.Key, Label: EntityDefinitions.All[kv.Key].Plural, Count: kv.Value.Count)).ToList();
        var active = tabs.FirstOrDefault(t => t.Key == tabParam);
        if (active.Key is null && tabs.Count > 0) { active = tabs[0]; }
        Tabs = tabs.Select(t => new TabInfo(t.Key, t.Label, t.Count, $"{detailPath}?tab={t.Key}", t.Key == active.Key)).ToList();
        if (active.Key is not null)
        {
            var linkParam = def.Key == "accounts" ? "accountId" : def.Key == "contacts" ? "contactId" : "opportunityId";
            TabTable = new TabTable(active.Label, EntityViews.ColumnsFor(active.Key).Select(c => c.Label).ToList(),
                data.Related[active.Key].Select(r => EntityViews.CellsFor(active.Key, r, f, data.Today, detailPath)).ToList(),
                $"{EntityDefinitions.All[active.Key].Path}/new?{linkParam}={id}",
                active.Key == "activities" ? "Log activity" : $"New {EntityDefinitions.All[active.Key].Singular}");
        }
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
        History = data.History.Select(h => new HistoryLine(f.DateTime(h.At), h.By, EntityViews.DescribeHistory(h, f), h.Field == "OwnerId")).ToList();
        ViewData["Title"] = $"{Cap(def.Singular)} · {RecordName}";
        if (lostError is not null)
        {
            Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        }
        return Page();
    }

    /// <summary>Update, close (won/lost) or done/undone, depending on the route that was matched.</summary>
    public async Task<IActionResult> OnPostAsync(string? entity, int id, string? outcome, string? verb)
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
        var res = await RunAsync(async repo =>
        {
            var row = await repo.GetAsync(def.Key, id);
            if (row is null) { return new CloseResult("notfound"); }
            if (!CrmRepo.IsRowVer(rowVer)) { return new CloseResult("conflict"); } // the action must carry the RowVer of the page it was clicked on
            var lk = await repo.LookupsAsync();
            var stage = lk.Stages.FirstOrDefault(s => s.Type == outcome);
            if (stage is null) { return new CloseResult("error", $"No {outcome} stage is configured."); }
            _ = EntityDefinitions.TryParseId(lostRaw, out var lostReason);
            var reason = lk.List("lost_reason").FirstOrDefault(p => p.Active && p.Id == lostReason);
            if (outcome == "lost" && reason is null) { return new CloseResult("lostError"); }
            var today = Today(lk.Settings);
            var moved = string.CompareOrdinal(row.S("CloseDate"), today) > 0;
            if (!confirmed)
            {
                var msg = $"Mark {EntityViews.RecordName(def.Key, row)} as {outcome}? The stage changes to {stage.Name}{(outcome == "lost" ? $" (reason: {reason!.Label})" : "")}.{(moved ? " The close date will be set to today." : "")}";
                return new CloseResult("confirm", "", moved, new ConfirmPage($"Mark as {outcome}", msg, $"{def.Path}/{id}/{outcome}",
                    [("rowVer", rowVer), ("lostReason", lostRaw), ("confirm", "1")], $"Mark as {outcome}", $"{def.Path}/{id}"));
            }
            var values = def.Columns.ToDictionary(c => c.Name, c => ColumnValue(row, c), StringComparer.Ordinal);
            values["StageId"] = stage.Id;
            values["LostReasonValueId"] = outcome == "lost" ? lostReason : null;
            values["OwnerId"] = row.Int("OwnerId");
            if (moved) { values["CloseDate"] = today; }
            var r = await repo.UpdateAsync(def.Key, id, values, rowVer, meta);
            return new CloseResult(r == UpdateResult.Ok ? "ok" : r == UpdateResult.Conflict ? "conflict" : "notfound", "", moved);
        });
        switch (res.Kind)
        {
            case "notfound": throw new NotFoundException();
            case "conflict": throw new EditConflictException($"{def.Path}/{id}");
            case "lostError": return await ShowDetailAsync(def.Key, id, "Choose why this was lost.");
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

    private static object? ColumnValue(Row row, ColumnSpec c) => c.Type switch
    {
        ColType.Int => row.IntOrNull(c.Name),
        ColType.Money => row[c.Name] is null ? null : row.Dec(c.Name),
        ColType.Bool => row.Bool(c.Name),
        _ => row[c.Name] is null ? null : row.S(c.Name),
    };
}
