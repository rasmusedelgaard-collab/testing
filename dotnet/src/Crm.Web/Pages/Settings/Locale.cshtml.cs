using System.Globalization;
using Crm.Web.Data;
using Crm.Web.Domain;
using Crm.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Crm.Web.Pages.Settings;

/// <summary>Currency and locale (GET/POST /settings/locale): base and enabled currencies, locale, time zone, first weekday, fiscal year start.</summary>
public sealed class LocaleModel(IRlsDb rls, IRlsContextAccessor identity, BrandingCache branding, TimeProvider time) : SettingsPageModel(rls, identity, branding)
{
    public static readonly IReadOnlyList<string> Days = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

    public static readonly IReadOnlyList<string> Months = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    public IReadOnlyList<string> Zones { get; private set; } = [];

    public IReadOnlyList<string> Warnings { get; private set; } = [];

    public string Preview { get; private set; } = "";

    public SettingsValidators.LocaleValues Values { get; private set; } = new("DKK", ["DKK"], "en-GB", "UTC", "1", "1");

    private static IReadOnlyList<string> WarningsFor(IReadOnlyList<string> enabled, IReadOnlyDictionary<string, int> usage) =>
        usage.Where(u => u.Value > 0 && !enabled.Contains(u.Key)).OrderBy(u => u.Key, StringComparer.Ordinal)
            .Select(u => $"{u.Key} is disabled but {u.Value} {(u.Value == 1 ? "opportunity still uses" : "opportunities still use")} it. They keep their currency; new records can't use it.").ToList();

    private void Show(SettingsValidators.LocaleValues values, IReadOnlyList<string> warnings)
    {
        Setup("locale", "Currency & locale");
        Values = values;
        Warnings = warnings;
        var zones = TimeZones.Listed.ToList();
        if (values.Timezone.Length > 0 && !zones.Contains(values.Timezone)) { zones.Add(values.Timezone); }
        Zones = zones;
        if (LocaleProfile.Allowed.Contains(values.Locale))
        {
            var f = new Formatter(values.Locale, values.Timezone);
            Preview = $"{f.Money(1234567.89m, SettingsValidators.Currencies.Contains(values.Base) ? values.Base : "DKK")} · {f.Date(TimeZones.TodayIn(values.Timezone, time).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}";
        }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var (s, usage) = await RunAsync(async (admin, _) => (await admin.SettingsMapAsync(), await admin.CurrencyUsageAsync()));
        string Get(string k, string d) => s.GetValueOrDefault(k) ?? d;
        var enabled = Get("currency.enabled", "").Split(',').Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
        Show(new SettingsValidators.LocaleValues(Get("currency.base", "DKK"), enabled, Get("locale.code", "en-GB"), Get("locale.timezone", "UTC"), Get("locale.firstDayOfWeek", "1"), Get("fiscal.startMonth", "1")), WarningsFor(enabled, usage));
        return Page();
    }

    public async Task<IActionResult> OnPostAsync([FromForm(Name = "base")] string? baseCurrency, [FromForm(Name = "enabled")] string[]? enabled, string? locale, string? timezone, string? firstDay, string? fiscalStart)
    {
        var (v, errors) = SettingsValidators.ValidateLocale(baseCurrency, enabled, locale, timezone, firstDay, fiscalStart, TimeZones.Listed);
        if (errors.Count > 0)
        {
            Show(v with { FirstDay = firstDay ?? "", FiscalStart = fiscalStart ?? "" }, []);
            Errors = errors;
            return Invalid();
        }
        var meta = MetaOf();
        var usage = await RunAsync(async (admin, _) =>
        {
            await admin.SaveSettingsAsync(new Dictionary<string, string>
            {
                ["currency.base"] = v.Base,
                ["currency.enabled"] = string.Join(',', v.Enabled),
                ["locale.code"] = v.Locale,
                ["locale.timezone"] = v.Timezone,
                ["locale.firstDayOfWeek"] = v.FirstDay,
                ["fiscal.startMonth"] = v.FiscalStart,
            }, meta);
            return await admin.CurrencyUsageAsync();
        });
        var warn = WarningsFor(v.Enabled, usage);
        Flash(warn.Count > 0 ? $"Settings saved. {string.Join(' ', warn)}" : "Settings saved.");
        return SeeOther("/settings/locale");
    }
}
