using System.Globalization;
using System.Text.RegularExpressions;
using Crm.Web.Data;
using Crm.Web.Domain;
using Crm.Web.Infrastructure;
using Crm.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Crm.Web.Pages.Settings;

public sealed record FxRowVm(FxRateRec Rate, string MonthLabel, string RateText, string At);

public sealed record FxPageData(Lookups Lookups, IReadOnlyList<FxRateRec> Rates);

/// <summary>
/// FX rates. GET /settings/fx, GET .../{id}/edit, POST /settings/fx (add), POST .../{id} (change), GET/POST .../{id}/delete.
/// The extra routes are added in Program.cs. One RunAsync per request: lookups, rates, validation and save share the transaction.
/// </summary>
public sealed partial class FxModel(IRlsDb rls, IRlsContextAccessor identity, BrandingCache branding, TimeProvider time) : SettingsPageModel(rls, identity, branding)
{
    public string Base { get; private set; } = "";

    public IReadOnlyList<string> Currencies { get; private set; } = [];

    public string CurMonth { get; private set; } = "";

    public string CurLabel { get; private set; } = "";

    public IReadOnlyList<string> Years { get; private set; } = [];

    public string WantCur { get; private set; } = "";

    public string WantYear { get; private set; } = "";

    public IReadOnlyList<FxRowVm> Rows { get; private set; } = [];

    public IReadOnlyList<string> Missing { get; private set; } = [];

    public int? EditId { get; private set; }

    public int DupId { get; private set; }

    public string CurrencyValue { get; private set; } = "";

    public string MonthValue { get; private set; } = "";

    public string RateValue { get; private set; } = "";

    [GeneratedRegex(@"^[0-9]{4}\z", RegexOptions.CultureInvariant)]
    private static partial Regex YearPattern();

    public static string MonthLabelOf(string m) => $"{m[5..7]}.{m[..4]}";

    private static async Task<FxPageData> LoadCoreAsync(AdminRepo admin, CrmRepo repo)
    {
        var lk = await repo.LookupsAsync();
        return new FxPageData(lk, await admin.FxRatesAsync(lk.Settings.BaseCurrency));
    }

    private void Apply(FxPageData data, int? editId, string currency, string month, string rate, int dupId = 0)
    {
        Setup("fx", "FX rates");
        var st = data.Lookups.Settings;
        var f = new Formatter(st.LocaleCode, st.Timezone);
        var p = LocaleProfile.For(st.LocaleCode);
        CurMonth = TimeZones.TodayIn(st.Timezone, time).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        CurLabel = MonthLabelOf(CurMonth);
        var q = Request.Query;
        WantCur = q.TryGetValue("currency", out var qc) ? qc.ToString().ToUpperInvariant() : "";
        WantYear = q.TryGetValue("year", out var qy) && YearPattern().IsMatch(qy.ToString()) ? qy.ToString() : "";
        Base = st.BaseCurrency;
        Currencies = st.EnabledCurrencies.Where(c => c != Base).ToList();
        Missing = Currencies.Where(c => !data.Rates.Any(r => r.Currency == c && r.Month == CurMonth)).ToList();
        Years = data.Rates.Select(r => r.Month[..4]).Append(CurMonth[..4]).Distinct().OrderByDescending(y => y, StringComparer.Ordinal).ToList();
        Rows = data.Rates.Where(r => (WantCur.Length == 0 || r.Currency == WantCur) && (WantYear.Length == 0 || r.Month.StartsWith(WantYear, StringComparison.Ordinal)))
            .Select(r => new FxRowVm(r, MonthLabelOf(r.Month), r.Rate.ToString("#,##0.0000##", p.Number), f.DateTime(r.At))).ToList();
        EditId = editId;
        DupId = dupId;
        CurrencyValue = currency;
        MonthValue = month.Length > 0 ? month : CurMonth;
        RateValue = rate;
    }

    public async Task<IActionResult> OnGetAsync([FromRoute] int? id, [FromRoute] string? verb)
    {
        if (id is not null && verb is null) { throw new NotFoundException(); } // POST-only route
        var (data, r) = await RunAsync(async (admin, repo) =>
        {
            var d = await LoadCoreAsync(admin, repo);
            return (d, id is null ? null : await admin.FxRateAsync(id.Value));
        });
        if (id is null)
        {
            Apply(data, null, "", "", "");
            return Page();
        }
        if (r is null) { throw new NotFoundException(); }
        if (verb == "delete")
        {
            Setup("fx", "Delete rate");
            return ShowConfirm("Delete rate", $"Delete the {r.Currency} rate for {MonthLabelOf(r.Month)}? This changes totals on the dashboard and in analytics for {MonthLabelOf(r.Month)}. Continue?",
                $"/settings/fx/{id}/delete", [], "Delete rate", true, "/settings/fx");
        }
        Apply(data, id, r.Currency, r.Month, new Formatter(data.Lookups.Settings.LocaleCode, data.Lookups.Settings.Timezone).RateInput(r.Rate));
        return Page();
    }

    public async Task<IActionResult> OnPostAsync([FromRoute] int? id, [FromRoute] string? verb, string? currency, string? month, string? rate, string? confirm)
    {
        if (verb == "edit") { throw new NotFoundException(); } // GET-only route
        var meta = MetaOf();
        // RunAsync: exclusive branches (delete returns before the save branch runs)
        if (verb == "delete")
        {
            var deleted = await RunAsync(async (admin, _) => await admin.DeleteFxRateAsync(id!.Value, meta));
            if (!deleted) { throw new NotFoundException(); }
            Flash("Rate deleted.");
            return SeeOther("/settings/fx");
        }
        var (data, kind, errors, confirmMonth, dupId, dupName) = await RunAsync(async (admin, repo) =>
        {
            var d = await LoadCoreAsync(admin, repo);
            var st = d.Lookups.Settings;
            var (cur, mon, rt, errs) = SettingsValidators.ValidateFx(currency, month, rate, st.BaseCurrency, st.EnabledCurrencies, st.LocaleCode);
            if (errs.Count > 0) { return (d, "invalid", errs, "", 0, ""); }
            if (id is not null)
            {
                var old = d.Rates.FirstOrDefault(x => x.Id == id) ?? await admin.FxRateAsync(id.Value) ?? throw new NotFoundException();
                if (confirm != "1")
                {
                    // Changing an existing rate restates totals: ask first, unless nothing changes.
                    var clash = d.Rates.Any(x => x.Id != id && x.Currency == cur && x.Month == mon);
                    if (!clash && (old.Currency != cur || old.Month != mon || old.Rate != rt)) { return (d, "confirm", errs, old.Month, 0, ""); }
                }
            }
            var r = await admin.SaveFxRateAsync(id, st.BaseCurrency, cur, mon, rt, meta);
            return r.Ok ? (d, "ok", errs, "", 0, "") : (d, "dup", errs, "", r.DuplicateOf, $"{cur} in {MonthLabelOf(mon)}");
        });
        switch (kind)
        {
            case "confirm":
                Setup("fx", "Change rate");
                return ShowConfirm("Change rate", $"This changes totals on the dashboard and in analytics for {MonthLabelOf(confirmMonth)}. Continue?", $"/settings/fx/{id}",
                    [("currency", currency ?? ""), ("month", month ?? ""), ("rate", rate ?? ""), ("confirm", "1")], "Save rate", false, "/settings/fx");
            case "invalid":
                Apply(data, id, currency ?? "", month ?? "", rate ?? "");
                Errors = errors;
                return Invalid();
            case "dup":
                Apply(data, id, currency ?? "", month ?? "", rate ?? "", dupId);
                Errors = new Dictionary<string, string> { ["currency"] = $"A rate for {dupName} already exists." };
                return Invalid();
            default:
                Flash("Rate saved.");
                return SeeOther("/settings/fx");
        }
    }
}
