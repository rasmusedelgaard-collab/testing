using System.Globalization;
using Crm.Web.Data;
using Crm.Web.Domain;
using Crm.Web.Pages.Entity;
using Crm.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Crm.Web.Pages;

public sealed record SearchHit(string Name, string Href, string Detail, string Owner, string StageName = "", string StageType = "", int StageIndex = 0, int StageCount = 0, string Icon = "");

public sealed record SearchGroup(string Key, string Plural, string Singular, int Total, IReadOnlyList<SearchHit> Hits, string SeeAllHref);

/// <summary>
/// Search results (UI redesign 5.7, B-4): for each entity the existing list query with the text, page size 5, default sort.
/// RLS applies to every query, so a user only finds what they can see. One RunAsync.
/// </summary>
public sealed class SearchModel(IRlsDb rls, IRlsContextAccessor identity, TimeProvider time) : EntityPageModel(rls, identity, time)
{
    public const int PerGroup = 5;

    public string Text { get; private set; } = "";

    public IReadOnlyList<SearchGroup> Groups { get; private set; } = [];

    public int TotalResults { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        ViewData["Title"] = "Search";
        var raw = Request.Query.TryGetValue("q", out var qv) ? qv.ToString().Trim() : "";
        Text = raw.Length > 100 ? raw[..100] : raw;
        if (Text.Length == 0)
        {
            return Page();
        }
        var data = await RunAsync(async repo =>
        {
            var lk = await repo.LookupsAsync();
            var today = Today(lk.Settings);
            var results = new List<(string Key, IReadOnlyList<Row> Rows, int Total)>();
            foreach (var def in EntityDefinitions.List)
            {
                // Closed opportunities and done activities are found too (status=all), unlike the default list views.
                var filters = new Dictionary<string, string>(StringComparer.Ordinal);
                if (def.Key is "opportunities" or "activities") { filters["status"] = "all"; }
                var (rows, total) = await repo.ListAsync(def.Key, new ListQuery(Text, 1, PerGroup, def.DefaultSort, def.DefaultDir, filters, today));
                results.Add((def.Key, rows, total));
            }
            return (lk, today, results);
        });
        var f = new Formatter(data.lk.Settings.LocaleCode, data.lk.Settings.Timezone);
        var groups = new List<SearchGroup>();
        foreach (var (key, rows, total) in data.results.Where(r => r.Total > 0))
        {
            var def = EntityDefinitions.All[key];
            var hits = rows.Select(r => Hit(key, r, f, data.lk.Stages)).ToList();
            var qs = Uri.EscapeDataString(Text);
            groups.Add(new SearchGroup(key, def.Plural, def.Singular, total, hits, $"{def.Path}?q={qs}{(key is "opportunities" or "activities" ? "&status=all" : "")}"));
        }
        Groups = groups;
        TotalResults = groups.Sum(g => g.Total);
        return Page();
    }

    private static SearchHit Hit(string entity, Row r, Formatter f, IReadOnlyList<Stage> stages)
    {
        var path = EntityDefinitions.All[entity].Path;
        var href = $"{path}/{r.Int("Id")}";
        string Join(params string[] parts) => string.Join(" · ", parts.Where(p => p.Length > 0));
        switch (entity)
        {
            case "accounts":
                return new SearchHit(r.S("Name"), href, Join(r.S("City"), r.S("CountryCode")), r.S("OwnerName"));
            case "contacts":
                return new SearchHit(EntityViews.RecordName(entity, r), href, Join(r.S("JobTitle"), r.S("AccountName"), r.S("Email")), r.S("OwnerName"));
            case "opportunities":
                var (idx, cnt) = EntityViews.OpenPosition(stages, r.Int("StageId"));
                return new SearchHit(r.S("Name"), href, Join(r.S("AccountName"), f.Money(r.Dec("Amount"), r.S("CurrencyCode"))), r.S("OwnerName"), r.S("StageName"), r.S("StageType"), idx, cnt);
            default:
                var rel = EntityViews.RelatedTo(r);
                return new SearchHit(r.S("Subject"), href, Join(EntityViews.TypeLabel.GetValueOrDefault(r.S("ActivityType")) ?? "", rel.Text, f.Date(r.S("DueDate"))), r.S("OwnerName"), Icon: r.S("ActivityType"));
        }
    }
}
