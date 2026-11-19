using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Crm.Web.Data;
using Crm.Web.Domain;
using Crm.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Crm.Web.Pages.Dashboard;

public sealed record ChartView(ChartVm Chart, string Json);

public sealed partial class AnalyticsModel(IRlsDb rls, IRlsContextAccessor identity, TimeProvider time) : PageModel
{
    /// <summary>The default encoder escapes &lt; &gt; &amp; ' +, so the JSON cannot close the script element (never UnsafeRelaxedJsonEscaping).</summary>
    private static readonly JsonSerializerOptions ChartJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [GeneratedRegex(@"^team:([1-9][0-9]{0,9})\z", RegexOptions.CultureInvariant)]
    private static partial Regex TeamValue();

    public string Period { get; private set; } = "";

    public string Error { get; private set; } = "";

    public string From { get; private set; } = "";

    public string To { get; private set; } = "";

    public string RangeText { get; private set; } = "";

    public string OwnerValue { get; private set; } = "";

    public IReadOnlyList<(string Value, string Label)> TeamOptions { get; private set; } = [];

    public IReadOnlyList<(string Id, string Label)> Owners { get; private set; } = [];

    public IReadOnlyList<ChartView> Charts { get; private set; } = [];

    public bool FxMissing { get; private set; }

    public bool IsAdmin { get; private set; }

    public string BaseCurrency { get; private set; } = "";

    private static int? InRange(string? v) => v is not null && EntityDefinitions.TryParseId(v, out var n) ? n : null;

    public async Task<IActionResult> OnGetAsync()
    {
        ViewData["Title"] = "Analytics";
        ViewData["ActiveNav"] = "analytics";
        ViewData["Scripts"] = new[] { "/vendor/chart.umd.js", "/js/charts.js" };
        var ctx = identity.Current;
        string Q(string k) => Request.Query.TryGetValue(k, out var v) ? v.ToString() : "";
        var ownerRaw = Q("owner");
        var teamMatch = TeamValue().Match(ownerRaw);
        var teamId = teamMatch.Success ? InRange(teamMatch.Groups[1].Value) : null;
        var ownerId = teamMatch.Success ? null : InRange(ownerRaw);
        var data = await rls.RunAsync(async tx =>
        {
            var repo = new CrmRepo(tx, ctx);
            var lk = await repo.LookupsAsync();
            var s = lk.Settings;
            var today = TimeZones.TodayIn(s.Timezone, time).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var period = Periods.Resolve(Q("period"), Q("from"), Q("to"), Q("shown"), today, s.FiscalStartMonth);
            var r = period.Range;
            var rates = await repo.FxRatesAsync(s.BaseCurrency);
            var open = await repo.OppAggregatesAsync(new OppAggFilter(["open"], r.From, r.To, ownerId, teamId, GroupBy: ["stage"]));
            var closed = await repo.OppAggregatesAsync(new OppAggFilter(["won", "lost"], r.From, r.To, ownerId, teamId, GroupBy: ["owner", "month"]));
            var all = await repo.OppAggregatesAsync(new OppAggFilter(["open", "won", "lost"], r.From, r.To, ownerId, teamId, GroupBy: ["leadSource"]));
            var acts = await repo.ActivitiesByTypeAsync(r, ownerId, teamId);
            var owners = await repo.OwnersAsync();
            var teams = ctx.Role == CrmRole.User ? [] : (await repo.TeamsAsync()).Where(t => ctx.Role == CrmRole.Admin || t.Id == ctx.TeamId).ToList();
            return (lk, today, period, rates, open, closed, all, acts, owners, teams);
        }, HttpContext.RequestAborted);

        var set = data.lk.Settings;
        var f = new Formatter(set.LocaleCode, set.Timezone);
        var c = new FormatCtx(f, set.LocaleCode, set.BaseCurrency);
        var conv = new Converter(set.BaseCurrency, data.rates);
        var wonRows = data.closed.Where(x => x.StageType == "won").ToList();
        var charts = new[]
        {
            Services.Charts.PipelineByStage(data.open, data.lk.Stages, conv, Periods.MonthOf(data.today), c),
            Services.Charts.WonRevenueByMonth(wonRows, data.period.Range, conv, c),
            Services.Charts.WinRateByOwner(data.closed, c),
            Services.Charts.ActivitiesByType(data.acts, c),
            Services.Charts.OppsByLeadSource(data.all, c),
        };
        FxMissing = charts.Any(ch => ch.Notes.Any(n => n.Contains("not included", StringComparison.Ordinal)));
        Period = data.period.Key;
        Error = data.period.Ok ? "" : data.period.Error ?? "";
        From = data.period.Key == "custom" ? Q("from") : data.period.Range.From;
        To = data.period.Key == "custom" ? Q("to") : data.period.Range.To;
        RangeText = $"{f.Date(data.period.Range.From)} – {f.Date(data.period.Range.To)}";
        OwnerValue = teamId is { } t ? $"team:{t}" : ownerId is { } o ? o.ToString(CultureInfo.InvariantCulture) : "";
        TeamOptions = data.teams.Select(t2 => ($"team:{t2.Id}", $"Team: {t2.Name}")).ToList();
        Owners = data.owners.Select(o2 => (o2.Id.ToString(CultureInfo.InvariantCulture), ctx.Role == CrmRole.Admin && o2.TeamName is not null ? $"{o2.Name} ({o2.TeamName})" : o2.Name)).ToList();
        Charts = charts.Select(ch => new ChartView(ch, JsonSerializer.Serialize(new { kind = ch.Kind, labels = ch.Labels, data = ch.Data, valueKind = ch.ValueKind, seriesLabel = ch.SeriesLabel, locale = set.LocaleCode, currency = set.BaseCurrency }, ChartJson))).ToList();
        IsAdmin = ctx.Role == CrmRole.Admin;
        BaseCurrency = set.BaseCurrency;
        return Page();
    }
}
