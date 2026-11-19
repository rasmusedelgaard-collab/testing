using System.Globalization;
using Crm.Web.Data;
using Crm.Web.Domain;
using Crm.Web.Pages.Entity;
using Crm.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Crm.Web.Pages.Pipeline;

public sealed record MoveOption(int StageId, string Label, bool Current);

public sealed record DealCard(int Id, string Name, string Account, int AccountId, string Money, string CloseDate, bool PastClose, string Owner, string RowVer, bool Moved, IReadOnlyList<MoveOption> Options);

public sealed record BoardColumn(int StageId, string Name, string Probability, int Count, string TotalShort, string TotalFull, string WeightedShort, decimal Share, IReadOnlyList<string> Notes, IReadOnlyList<DealCard> Cards, string AllHref, bool ShowAll);

/// <summary>
/// Pipeline board (UI redesign 5.6, B-5): one column per open stage in Settings order. Cards come from the existing opportunity list query
/// (status=open, one stage, close date ascending, 20 per column); column totals from the open-pipeline aggregate grouped by stage and
/// converted exactly as the dashboard does. Everything is read in one RunAsync, so RLS filters every card and figure.
/// </summary>
public sealed class IndexModel(IRlsDb rls, IRlsContextAccessor identity, TimeProvider time) : EntityPageModel(rls, identity, time)
{
    public const int CardsPerColumn = 20;

    public IReadOnlyList<BoardColumn> Columns { get; private set; } = [];

    public IReadOnlyList<FilterField> Filters { get; private set; } = [];

    public string OwnerValue { get; private set; } = "";

    public string TotalShort { get; private set; } = "";

    public string TotalFull { get; private set; } = "";

    public int TotalCount { get; private set; }

    public IReadOnlyList<string> Notes { get; private set; } = [];

    public string ClosedHref { get; private set; } = "/opportunities?status=closed";

    public string ListHref { get; private set; } = "/opportunities";

    public string ReturnTo { get; private set; } = "/pipeline";

    public async Task<IActionResult> OnGetAsync()
    {
        SetEntity("opportunities");
        ViewData["ActiveNav"] = "pipeline";
        ViewData["Title"] = "Pipeline";
        var ownerRaw = Request.Query.TryGetValue("owner", out var ov) ? ov.ToString().Trim() : "";
        int? owner = EntityDefinitions.TryParseId(ownerRaw, out var oid) ? oid : null;
        var movedRaw = TempData["MovedId"] as string;
        var movedId = int.TryParse(movedRaw, NumberStyles.None, CultureInfo.InvariantCulture, out var mid) ? mid : 0;
        var data = await RunAsync(async repo =>
        {
            var lk = await repo.LookupsAsync();
            var today = Today(lk.Settings);
            var rates = await repo.FxRatesAsync(lk.Settings.BaseCurrency);
            var agg = await repo.OppAggregatesAsync(new OppAggFilter(["open"], OwnerId: owner, GroupBy: ["stage"]));
            var owners = await repo.OwnersAsync();
            var lists = new Dictionary<int, (IReadOnlyList<Row> Rows, int Total)>();
            foreach (var st in lk.Stages.Where(s => s.Type == "open").OrderBy(s => s.SortOrder))
            {
                var filters = new Dictionary<string, string>(StringComparer.Ordinal) { ["status"] = "open", ["stage"] = st.Id.ToString(CultureInfo.InvariantCulture) };
                if (owner is { } o) { filters["owner"] = o.ToString(CultureInfo.InvariantCulture); }
                lists[st.Id] = await repo.ListAsync("opportunities", new ListQuery("", 1, CardsPerColumn, "close", "asc", filters, today));
            }
            return (lk, today, rates, agg, owners, lists);
        });

        var set = data.lk.Settings;
        var f = new Formatter(set.LocaleCode, set.Timezone);
        var conv = new Converter(set.BaseCurrency, data.rates);
        var curMonth = Periods.MonthOf(data.today);
        var openStages = data.lk.Stages.Where(s => s.Type == "open").OrderBy(s => s.SortOrder).ToList();
        var stageAmounts = openStages.Select(st =>
        {
            var rows = data.agg.Where(a => a.StageId == st.Id).ToList();
            return (Stage: st, Total: conv.ConvertRows(rows, a => (a.Currency, a.Count, a.Amount), _ => curMonth),
                Weighted: conv.ConvertRows(rows, a => (a.Currency, a.Count, a.Amount), _ => curMonth, _ => st.Probability / 100m), Count: rows.Sum(a => a.Count));
        }).ToList();
        var max = stageAmounts.Count == 0 ? 0m : stageAmounts.Max(x => x.Total.Total);
        var ownerQs = owner is { } ow ? $"&owner={ow.ToString(CultureInfo.InvariantCulture)}" : "";
        Columns = stageAmounts.Select(x =>
        {
            var (rows, total) = data.lists[x.Stage.Id];
            var options = openStages.Select(o => new MoveOption(o.Id, o.Id == x.Stage.Id ? $"{o.Name} (current)" : o.Name, o.Id == x.Stage.Id)).ToList();
            var cards = rows.Select(r => new DealCard(r.Int("Id"), r.S("Name"), r.S("AccountName"), r.Int("AccountId"), f.Money(r.Dec("Amount"), r.S("CurrencyCode")), f.Date(r.S("CloseDate")),
                string.CompareOrdinal(r.S("CloseDate"), data.today) < 0, r.S("OwnerName"), r.S("RowVer"), r.Int("Id") == movedId, options)).ToList();
            var notes = new[] { Converter.ExclusionNote(x.Total), Converter.FallbackNote(x.Total) }.Where(n => n.Length > 0).ToList();
            return new BoardColumn(x.Stage.Id, x.Stage.Name, NumberFormats.Percent(x.Stage.Probability / 100m, set.LocaleCode), x.Count,
                NumberFormats.CompactMoney(x.Total.Total, set.BaseCurrency, set.LocaleCode), f.Money(x.Total.Total, set.BaseCurrency),
                NumberFormats.CompactMoney(x.Weighted.Total, set.BaseCurrency, set.LocaleCode), max == 0 ? 0 : x.Total.Total / max * 100, notes, cards,
                $"/opportunities?status=open&stage={x.Stage.Id.ToString(CultureInfo.InvariantCulture)}{ownerQs}", total > cards.Count);
        }).ToList();
        var totals = Kpi.PipelineTotals(data.agg, data.lk.Stages, conv, curMonth);
        TotalShort = NumberFormats.CompactMoney(totals.Total.Total, set.BaseCurrency, set.LocaleCode);
        TotalFull = f.Money(totals.Total.Total, set.BaseCurrency);
        TotalCount = totals.Count;
        Notes = new[] { Converter.ExclusionNote(totals.Total), Converter.FallbackNote(totals.Total) }.Where(n => n.Length > 0).ToList();
        OwnerValue = owner is { } o2 ? o2.ToString(CultureInfo.InvariantCulture) : "";
        var admin = Ctx.Role == CrmRole.Admin;
        Filters = [new FilterField("owner", "Owner", [new SelectOption("", "All I can see"), .. data.owners.Select(o => new SelectOption(o.Id.ToString(CultureInfo.InvariantCulture), EntityViews.OwnerLabel(o, admin)))], OwnerValue)];
        ClosedHref = $"/opportunities?status=closed{ownerQs}";
        ListHref = owner is { } ow2 ? $"/opportunities?owner={ow2.ToString(CultureInfo.InvariantCulture)}" : "/opportunities";
        ReturnTo = owner is { } ow3 ? $"/pipeline?owner={ow3.ToString(CultureInfo.InvariantCulture)}" : "/pipeline";
        return Page();
    }
}
