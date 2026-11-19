using Crm.Web.Data;
using Crm.Web.Domain;
using Crm.Web.Infrastructure;
using Crm.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Crm.Web.Pages.Entity;

public sealed record Head(string Label, string? Href, string Aria, bool Right);

public sealed record RowAction(string Name, string EditHref);

public sealed class IndexModel(IRlsDb rls, IRlsContextAccessor identity, TimeProvider time) : EntityPageModel(rls, identity, time)
{
    public int Total { get; private set; }

    public ListQuery Q { get; private set; } = null!;

    public bool IsFiltered { get; private set; }

    public IReadOnlyList<Column> Columns { get; private set; } = [];

    public IReadOnlyList<Head> Heads { get; private set; } = [];

    public IReadOnlyList<RowAction> RowActions { get; private set; } = [];

    /// <summary>One entry per filter that is not at its default: the chip text and the URL without that filter.</summary>
    public IReadOnlyList<(string Label, string Href)> ActiveFilters { get; private set; } = [];

    public string BoardHref { get; private set; } = "/pipeline";

    public string SearchHint => Def.Key switch
    {
        "accounts" => "Search name, city...",
        "contacts" => "Search name, account, email...",
        "opportunities" => "Search name, account...",
        _ => "Search subject...",
    };

    public string EmptyHint => Def.Key switch
    {
        "accounts" => "Accounts are the companies you sell to.",
        "contacts" => "Contacts are the people you work with at your accounts.",
        "opportunities" => "Opportunities track deals from first contact to close.",
        _ => "Activities record calls, meetings, tasks and notes.",
    };

    public IReadOnlyList<IReadOnlyList<Cell>> Rows { get; private set; } = [];

    public IReadOnlyList<FilterField> Filters { get; private set; } = [];

    public int PageNo { get; private set; }

    public int PageCount { get; private set; }

    public string? PrevHref { get; private set; }

    public string? NextHref { get; private set; }

    public int From { get; private set; }

    public int To { get; private set; }

    public async Task<IActionResult> OnGetAsync([FromRoute] string entity)
    {
        SetEntity(entity);
        var def = Def;
        var query = Request.Query;
        var data = await RunAsync(async repo =>
        {
            var lk = await repo.LookupsAsync();
            var today = Today(lk.Settings);
            var q = EntityDefinitions.ParseListQuery(def, k => query.TryGetValue(k, out var v) ? v.ToString() : null, today);
            // Sequential on purpose: one transaction = one connection = one request at a time.
            var (rows, total) = await repo.ListAsync(def.Key, q);
            var owners = await repo.OwnersAsync();
            if (def.Key == "accounts" && rows.Count > 0)
            {
                // Open pipeline per account in the base currency at the current month's rate (ADR-0005); unconvertible deals are called out.
                var aggs = await repo.OppAggregatesAsync(new OppAggFilter(["open"], AccountIds: rows.Select(r => r.Int("Id")).ToList(), GroupBy: ["account"]));
                var rates = await repo.FxRatesAsync(lk.Settings.BaseCurrency);
                var conv = new Converter(lk.Settings.BaseCurrency, rates);
                var fmt = new Formatter(lk.Settings.LocaleCode, lk.Settings.Timezone);
                foreach (var r in rows)
                {
                    var mine = aggs.Where(a => a.AccountId == r.Int("Id")).ToList();
                    if (mine.Count == 0)
                    {
                        continue;
                    }
                    var c = conv.ConvertRows(mine, a => (a.Currency, a.Count, a.Amount), _ => today[..7]);
                    var n = c.Excluded.Sum(e => e.Count);
                    var note = n > 0 ? $"{n} {(n == 1 ? "opportunity" : "opportunities")} not converted (no rate: {string.Join(", ", c.Excluded.Select(e => e.Currency))})" : "";
                    r.Set("OpenPipeline", (c.Total > 0 || note.Length == 0 ? fmt.Money(c.Total, lk.Settings.BaseCurrency) : "") + (note.Length > 0 ? (c.Total > 0 ? " + " : "") + note : ""));
                }
            }
            return (lk, q, rows, total, owners, today);
        });
        var f = new Formatter(data.lk.Settings.LocaleCode, data.lk.Settings.Timezone);
        Q = data.q;
        Total = data.total;
        var returnTo = EntityViews.PageHref(def.Path, data.q, new Dictionary<string, string?>());
        PageCount = Math.Max(1, (int)Math.Ceiling(data.total / (double)data.q.PageSize));
        IsFiltered = data.q.Q.Length > 0 || data.q.Filters.Any(kv => def.Filters.First(x => x.Key == kv.Key).Default != kv.Value);
        Columns = EntityViews.ColumnsFor(def.Key);
        Heads = Columns.Select(c => new Head(c.Label,
            c.Sort is null ? null : EntityViews.PageHref(def.Path, data.q, new Dictionary<string, string?> { ["sort"] = c.Sort, ["dir"] = data.q.Sort == c.Sort && data.q.Dir == "asc" ? "desc" : "asc", ["page"] = "1" }),
            data.q.Sort == c.Sort ? (data.q.Dir == "asc" ? "ascending" : "descending") : "none",
            c.Label is "Amount" or "Open pipeline")).ToList();
        Rows = data.rows.Select(r => EntityViews.CellsFor(def.Key, r, f, data.today, returnTo, data.lk.Stages)).ToList();
        RowActions = data.rows.Select(r => new RowAction(EntityViews.RecordName(def.Key, r), $"{def.Path}/{r.Int("Id")}/edit")).ToList();
        Filters = EntityViews.FilterFields(def.Key, data.q, data.lk, data.owners, Ctx.Role == CrmRole.Admin);
        ActiveFilters = EntityViews.ActiveFilters(def, data.q, Filters);
        BoardHref = data.q.Filter("owner") is { Length: > 0 } ow ? $"/pipeline?owner={Uri.EscapeDataString(ow)}" : "/pipeline";
        PageNo = data.q.Page;
        PrevHref = data.q.Page > 1 ? EntityViews.PageHref(def.Path, data.q, new Dictionary<string, string?> { ["page"] = (data.q.Page - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) }) : null;
        NextHref = data.q.Page < PageCount ? EntityViews.PageHref(def.Path, data.q, new Dictionary<string, string?> { ["page"] = (data.q.Page + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) }) : null;
        From = data.total == 0 ? 0 : (data.q.Page - 1) * data.q.PageSize + 1;
        To = Math.Min(data.total, data.q.Page * data.q.PageSize);
        ViewData["Title"] = def.Plural;
        return Page();
    }

    /// <summary>Create. Validation failure re-renders the form (422); success is a 303 to the record (or the list on hand-off).</summary>
    public async Task<IActionResult> OnPostAsync([FromRoute] string entity)
    {
        SetEntity(entity);
        var body = FormBody();
        var meta = MetaOf();
        var def = Def;
        var outcome = await RunAsync(async repo =>
        {
            var lk = await repo.LookupsAsync();
            var env = await FormEnvAsync(repo, lk, null);
            var result = await PrepareAsync(repo, lk, env, body, null);
            if (!result.Ok)
            {
                return new SaveOutcome("invalid", env, result.Errors);
            }
            try
            {
                var id = await repo.CreateAsync(def.Key, result.Values, meta);
                var visible = await repo.GetAsync(def.Key, id) is not null;
                var owner = await repo.OwnerByIdAsync((int)result.Values["OwnerId"]!);
                return new SaveOutcome("saved", env, result.Errors, id, visible, owner?.Name ?? "");
            }
            catch (ForbiddenException)
            {
                return new SaveOutcome("invalid", env, new Dictionary<string, string> { ["OwnerId"] = WriteBlocked });
            }
        });
        if (outcome.Kind == "invalid")
        {
            return RenderForm(BuildForm($"New {def.Singular}", def.Path, def.Path, body, outcome.Errors, outcome.Env!));
        }
        return AfterSave(outcome.Id, outcome.Visible, outcome.OwnerName);
    }
}
