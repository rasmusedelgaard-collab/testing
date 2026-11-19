using System.Globalization;
using System.Text.RegularExpressions;
using Crm.Web.Domain;
using Crm.Web.Services.Demo;

namespace Crm.Tests.Unit;

/// <summary>The demo generator is pure: same date in, same data out; every record passes the app's own validators; nothing can reach a real person.</summary>
public sealed partial class DemoGeneratorTests
{
    private static readonly DateOnly Today = new(2026, 11, 19);

    [GeneratedRegex(@"^(\+1 555 01\d\d|\+44 7700 900\d\d\d)\z", RegexOptions.CultureInvariant)]
    private static partial Regex FakePhone();

    private static readonly string[] OpenStageNames = ["Qualification", "Needs analysis", "Proposal", "Negotiation"];

    private static Lookups Lookups()
    {
        var stages = new List<Stage>();
        for (var i = 0; i < OpenStageNames.Length; i++) { stages.Add(new Stage(i + 1, OpenStageNames[i], i + 1, 10 + (i * 20), "open")); }
        stages.Add(new Stage(5, "Closed won", 5, 100, "won"));
        stages.Add(new Stage(6, "Closed lost", 6, 0, "lost"));
        PicklistValue[] Values(params string[] labels) => labels.Select((l, i) => new PicklistValue(i + 1, null, l, true)).ToArray();
        return new Lookups(
            new Settings("CRM", "en-GB", "Europe/Copenhagen", "DKK", ["DKK", "EUR", "USD", "SEK"], "private", "#1D4ED8", 1, 1),
            stages,
            new Dictionary<string, IReadOnlyList<PicklistValue>>
            {
                ["industry"] = Values("Manufacturing", "Technology", "Finance", "Healthcare", "Retail", "Services", "Public sector", "Other", "Wind energy", "Building materials", "Industry"),
                ["lead_source"] = Values("Website", "Referral", "Event", "Outbound", "Partner", "Other"),
                ["lost_reason"] = Values("Price", "Competitor", "No budget", "No decision", "Timing", "Other"),
            });
    }

    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public void Same_date_gives_identical_data_and_another_date_shifts_it()
    {
        var a = DemoGenerator.Generate(Today);
        var b = DemoGenerator.Generate(Today);
        Assert.Equal(a.Accounts, b.Accounts);
        Assert.Equal(a.Contacts, b.Contacts);
        static object Flat(DemoOpportunity o) => (o with { StageChanges = [] }, string.Join(",", o.StageChanges));
        Assert.Equal(a.Opportunities.Select(Flat), b.Opportunities.Select(Flat));
        Assert.Equal(a.Activities, b.Activities);
        Assert.Equal(a.FxRates, b.FxRates);
        var later = DemoGenerator.Generate(Today.AddDays(40));
        Assert.Equal(a.Accounts.Select(x => x.Name), later.Accounts.Select(x => x.Name));
        Assert.NotEqual(a.Opportunities.Select(x => x.CloseDate), later.Opportunities.Select(x => x.CloseDate));
    }

    [Fact]
    public void Random_generator_matches_the_published_splitmix64_vector()
    {
        // Seed 0 gives 0xE220A8397B1DCDAF, 0x6E789E6AA1B965F4, 0x06C45D188009454F. If this changes, every seeded database changes with it.
        var r = new DemoRandom(0);
        Assert.Equal(535, r.Next(1000));
        Assert.Equal(700, r.Next(1000));
        Assert.Equal(679, r.Next(1000));
    }

    [Theory]
    [InlineData(2026, 11, 19)]
    [InlineData(2026, 1, 1)]
    [InlineData(2026, 12, 31)]
    [InlineData(2028, 2, 29)]
    public void Volumes_and_shape(int y, int m, int d)
    {
        var today = new DateOnly(y, m, d);
        var s = DemoGenerator.Generate(today);
        Assert.Equal(3, s.Teams.Count);
        Assert.Equal(10, s.Users.Count);
        Assert.Equal(["Admin", "Manager", "User"], s.Users.Select(u => u.Role).Distinct().Order().ToArray());
        Assert.Equal(41, s.Accounts.Count);
        Assert.Equal(100, s.Contacts.Count);
        Assert.Equal(DemoGenerator.OpenTotal, s.Opportunities.Count(o => o.IsOpen));
        Assert.InRange(s.Opportunities.Count(o => !o.IsOpen), 220, 250);
        Assert.Equal(250, s.Activities.Count);
        Assert.Equal(["CN", "DE", "DK", "IT", "PL", "SE", "US"], s.Accounts.Select(a => a.Country).Distinct().Order().ToArray());
        Assert.Equal(41, s.Accounts.Select(a => a.Name).Distinct().Count());
        Assert.Equal(["Americas & Asia", "DACH & Poland", "Nordics"], s.Teams.Select(t => t.Name).Order().ToArray());
        Assert.All(s.Users, u => Assert.StartsWith("00000000-0000-4000-8000-", u.Oid.ToString(), StringComparison.Ordinal));
        Assert.All(s.Accounts, a => Assert.Contains(s.Users, u => u.Key == a.OwnerKey && u.Role != "Admin" && u.Active));
        // Edge users: one without a team who owns records, one inactive who owns closed deals (and nothing else).
        Assert.Contains(s.Accounts, a => a.OwnerKey == DemoMarker.NoTeamKey);
        Assert.Null(s.Users.Single(u => u.Key == DemoMarker.NoTeamKey).TeamKey);
        Assert.False(s.Users.Single(u => u.Key == DemoMarker.InactiveKey).Active);
        var peters = s.Opportunities.Where(o => o.OwnerKey == DemoMarker.InactiveKey).ToList();
        Assert.Equal(3, peters.Count);
        Assert.All(peters, o => Assert.False(o.IsOpen));
        Assert.DoesNotContain(s.Accounts, a => a.OwnerKey == DemoMarker.InactiveKey);
        Assert.Single(s.Accounts, a => a.Industry is null);
        Assert.Equal(["Building materials", "Industry", "Wind energy"], s.Accounts.Where(a => a.Industry is not null).Select(a => a.Industry!).Distinct().Order().ToArray());
        Assert.Contains(s.Contacts, c => c.FirstName is null);
    }

    [Fact]
    public void Opportunities_cover_history_open_pipeline_and_the_requested_amounts_and_currencies()
    {
        var s = DemoGenerator.Generate(Today);
        var fx = s.FxRates.ToLookup(r => r.Currency);
        decimal Rate(string c, DateOnly date) => c == "DKK" ? 1m : fx[c].Where(r => r.Month <= new DateOnly(date.Year, date.Month, 1)).MaxBy(r => r.Month)!.Rate;
        var won = s.Opportunities.Where(o => o.StageSlot == DemoOpportunity.SlotWon).ToList();
        var lost = s.Opportunities.Where(o => o.StageSlot == DemoOpportunity.SlotLost).ToList();
        var open = s.Opportunities.Where(o => o.IsOpen).ToList();
        Assert.Equal(66, open.Count);
        Assert.Equal([25, 18, 14, 9], Enumerable.Range(0, 4).Select(i => open.Count(o => o.StageSlot == i)).ToArray());
        Assert.InRange(won.Count + lost.Count, 220, 250);
        Assert.True(won.Count >= 8 * DemoGenerator.HistoryMonths - 8);
        Assert.Equal(["DKK", "EUR", "SEK", "USD"], s.Opportunities.Select(o => o.Currency).Distinct().Order().ToArray());
        Assert.All(s.Opportunities, o =>
        {
            var dkk = o.Amount * Rate(o.Currency, o.IsOpen ? Today : o.CloseDate);
            if (o.Amount == 0m) { return; }
            Assert.InRange(dkk, 50_000m, 5_500_000m);
        });
        Assert.Equal(["Competitor", "No budget", "No decision", "Other", "Price", "Timing"], lost.Select(o => o.LostReason!).Distinct().Order().ToArray());
        Assert.All(won.Concat(open), o => Assert.Null(o.LostReason));
        // 14 calendar months of closed deals, each with at least one won deal; none in the future.
        var oldest = new DateOnly(Today.Year, Today.Month, 1).AddMonths(-(DemoGenerator.HistoryMonths - 1));
        Assert.All(won.Concat(lost), o => { Assert.InRange(o.CloseDate, oldest, Today); Assert.NotNull(o.ClosedAt); });
        for (var m = oldest; m <= Today; m = m.AddMonths(1))
        {
            Assert.Contains(won, o => o.CloseDate.Year == m.Year && o.CloseDate.Month == m.Month);
        }
        Assert.True(lost.Select(o => (o.CloseDate.Year, o.CloseDate.Month)).Distinct().Count() >= 16);
        Assert.Single(open, o => o.Amount == 0m);
        // Every owner has at least 5 closed deals in the last 12 months ("Win rate by owner").
        foreach (var u in s.Users.Where(u => u.Role != "Admin" && u.Active))
        {
            Assert.True(won.Concat(lost).Count(o => o.OwnerKey == u.Key && o.CloseDate >= Today.AddMonths(-12)) >= 5, u.Key);
        }
        // Win rate: better in the last 90 days than in the 90 days before.
        double Rate90(int from, int to) { var c = won.Concat(lost).Where(o => o.CloseDate > Today.AddDays(-to) && o.CloseDate <= Today.AddDays(-from)).ToList(); return (double)c.Count(o => o.StageSlot == DemoOpportunity.SlotWon) / c.Count; }
        Assert.True(Rate90(0, 90) > Rate90(90, 180));
        // History: stage changes on many deals, owner changes on some; timestamps are in order and never in the future.
        Assert.True(s.Opportunities.Count(o => o.StageChanges.Count > 0) > 100);
        Assert.True(s.Opportunities.Count(o => o.StageChanges.Count >= 2) > 20);
        Assert.InRange(s.Opportunities.Count(o => o.PreviousOwnerKey is not null), 8, 40);
        Assert.All(s.Opportunities, o => Assert.True(o.StageChanges.All(c => c.At >= o.CreatedAt) && o.StageChanges.Zip(o.StageChanges.Skip(1)).All(p => p.First.At <= p.Second.At)));
        Assert.All(won.Concat(lost), o => Assert.Equal(o.StageSlot, o.StageChanges[^1].ToSlot));
        Assert.All(open.Where(o => o.StageSlot > 0), o => Assert.Equal(o.StageSlot, o.StageChanges[^1].ToSlot));
        // Open pipeline: some deals close this month, next month and next quarter; creation is weighted towards recent months.
        Assert.Contains(open, o => o.CloseDate.Month == Today.Month && o.CloseDate >= Today);
        var next = new DateOnly(Today.Year, Today.Month, 1).AddMonths(1);
        Assert.True(open.Count(o => o.CloseDate.Year == next.Year && o.CloseDate.Month == next.Month) >= 5);
        var nextQuarter = new DateOnly(Today.Year, (((Today.Month - 1) / 3) * 3) + 1, 1).AddMonths(3);
        Assert.True(open.Count(o => o.CloseDate >= nextQuarter && o.CloseDate < nextQuarter.AddMonths(3)) >= 5);
        Assert.True(open.Count(o => o.CreatedAt >= Today.AddDays(-60).ToDateTime(TimeOnly.MinValue)) > open.Count / 2);
        // The dashboard has figures: won this month and this quarter.
        Assert.Contains(won, o => o.CloseDate.Month == Today.Month);
        Assert.All(s.Opportunities, o => Assert.True(o.ModifiedAt >= o.CreatedAt));
    }

    [Fact]
    public void Activities_include_today_overdue_upcoming_and_many_done()
    {
        var s = DemoGenerator.Generate(Today);
        var actionable = s.Activities.Where(a => a.Type != "note").ToList();
        Assert.Equal(["call", "meeting", "note", "task"], s.Activities.Select(a => a.Type).Distinct().Order().ToArray());
        Assert.True(actionable.Count(a => a.IsDone) >= 100);
        Assert.True(actionable.Count(a => !a.IsDone && a.DueDate == Today) >= 7);
        // The demo persona: 2 overdue (1 and 5 days), 3 due today (two with times), 5 in the next 7 days, and about 12 open deals.
        var mine = actionable.Where(a => a.OwnerKey == DemoMarker.PersonaKey && !a.IsDone).ToList();
        Assert.Contains(mine, a => a.DueDate == Today.AddDays(-1));
        Assert.Contains(mine, a => a.DueDate == Today.AddDays(-5));
        Assert.True(mine.Count(a => a.DueDate == Today) >= 3 && mine.Count(a => a.DueDate == Today && a.DueTime is not null) >= 2 && mine.Any(a => a.DueDate == Today && a.DueTime is null));
        Assert.True(mine.Count(a => a.DueDate > Today && a.DueDate <= Today.AddDays(7)) >= 5);
        Assert.InRange(s.Opportunities.Count(o => o.IsOpen && o.OwnerKey == DemoMarker.PersonaKey), 8, 18);
        Assert.True(actionable.Count(a => !a.IsDone && a.DueDate < Today) >= 7);
        Assert.True(actionable.Count(a => !a.IsDone && a.DueDate > Today && a.DueDate <= Today.AddDays(7)) >= 7);
        Assert.True(actionable.Count(a => !a.IsDone && a.DueDate > Today.AddDays(7)) >= 7);
        // Done activities are in the past, except the one edge case: done although its due date is still ahead.
        Assert.All(actionable.Where(a => a.IsDone), a => { Assert.True(a.DueDate < Today || a.DueDate == Today.AddDays(3)); Assert.NotNull(a.DoneAt); });
        Assert.Single(actionable, a => a.IsDone && a.DueDate > Today);
        Assert.All(s.Activities.Where(a => a.Type == "note"), a => { Assert.Null(a.DueDate); Assert.Null(a.DueTime); });
        // Every non-admin user has work due today and overdue on their dashboard.
        foreach (var u in s.Users.Where(u => u.Role != "Admin" && u.Active))
        {
            Assert.Contains(actionable, a => a.OwnerKey == u.Key && !a.IsDone && a.DueDate == Today);
            Assert.Contains(actionable, a => a.OwnerKey == u.Key && !a.IsDone && a.DueDate < Today);
        }
        // Done history reaches back a long way.
        Assert.Contains(actionable, a => a.IsDone && a.DueDate < Today.AddDays(-180));
    }

    [Fact]
    public void Fx_rates_cover_the_period_at_realistic_values()
    {
        var s = DemoGenerator.Generate(Today);
        var first = new DateOnly(Today.Year, Today.Month, 1).AddMonths(-(DemoGenerator.HistoryMonths + 1));
        foreach (var c in new[] { "EUR", "SEK", "USD" })
        {
            var series = s.FxRates.Where(r => r.Currency == c).OrderBy(r => r.Month).ToList();
            Assert.Equal(first, series[0].Month);
            Assert.Equal(new DateOnly(Today.Year, Today.Month, 1), series[^1].Month);
            for (var i = 1; i < series.Count; i++) { Assert.Equal(series[i - 1].Month.AddMonths(1), series[i].Month); }
            Assert.All(series, r => Assert.Equal(1, r.Month.Day));
        }
        Assert.All(s.FxRates.Where(r => r.Currency == "EUR"), r => Assert.InRange(r.Rate, 7.455m, 7.465m));
        Assert.All(s.FxRates.Where(r => r.Currency == "SEK"), r => Assert.InRange(r.Rate, 0.66m, 0.71m));
        Assert.All(s.FxRates.Where(r => r.Currency == "USD"), r => Assert.InRange(r.Rate, 6.35m, 7.05m));
        Assert.True(s.FxRates.Where(r => r.Currency == "USD").Select(r => r.Rate).Distinct().Count() > 5, "USD should drift");
    }

    [Fact]
    public void Every_record_passes_the_apps_own_validators()
    {
        var s = DemoGenerator.Generate(Today);
        var lk = Lookups();
        var industry = lk.List("industry").ToDictionary(p => p.Label, p => p.Id);
        var lead = lk.List("lead_source").ToDictionary(p => p.Label, p => p.Id);
        var lostReason = lk.List("lost_reason").ToDictionary(p => p.Label, p => p.Id);
        string N(int n) => n.ToString(CultureInfo.InvariantCulture);
        void Ok(string entity, Dictionary<string, string> body)
        {
            var r = EntityValidator.Validate(entity, body, lk);
            Assert.True(r.Ok, $"{entity} {body.GetValueOrDefault("Name") ?? body.GetValueOrDefault("Subject") ?? body.GetValueOrDefault("LastName")}: {string.Join("; ", r.Errors.Select(e => e.Key + "=" + e.Value))}");
        }
        foreach (var a in s.Accounts)
        {
            Ok("accounts", new() { ["OwnerId"] = "1", ["Name"] = a.Name, ["Website"] = a.Website, ["Phone"] = a.Phone ?? "", ["IndustryValueId"] = a.Industry is null ? "" : N(industry[a.Industry]), ["Street"] = a.Street, ["PostalCode"] = a.PostalCode, ["City"] = a.City, ["CountryCode"] = a.Country, ["Notes"] = a.Notes });
        }
        foreach (var c in s.Contacts)
        {
            Ok("contacts", new() { ["OwnerId"] = "1", ["AccountId"] = c.AccountKey is null ? "" : "1", ["FirstName"] = c.FirstName ?? "", ["LastName"] = c.LastName, ["JobTitle"] = c.JobTitle, ["Email"] = c.Email, ["Phone"] = c.Phone ?? "", ["Mobile"] = c.Mobile ?? "" });
        }
        foreach (var o in s.Opportunities)
        {
            var stageId = o.StageSlot == DemoOpportunity.SlotWon ? 5 : o.StageSlot == DemoOpportunity.SlotLost ? 6 : o.StageSlot + 1;
            Ok("opportunities", new()
            {
                ["OwnerId"] = "1",
                ["Name"] = o.Name,
                ["AccountId"] = "1",
                ["PrimaryContactId"] = o.ContactKey is null ? "" : "1",
                ["StageId"] = N(stageId),
                ["Amount"] = o.Amount.ToString("0.00", CultureInfo.InvariantCulture),
                ["CurrencyCode"] = o.Currency,
                ["CloseDate"] = D(o.CloseDate),
                ["LeadSourceValueId"] = N(lead[o.LeadSource]),
                ["LostReasonValueId"] = o.LostReason is null ? "" : N(lostReason[o.LostReason]),
            });
        }
        foreach (var a in s.Activities)
        {
            Ok("activities", new()
            {
                ["OwnerId"] = "1",
                ["ActivityType"] = a.Type,
                ["Subject"] = a.Subject,
                ["Description"] = a.Description ?? "",
                ["AccountId"] = "1",
                ["ContactId"] = a.ContactKey is null ? "" : "1",
                ["OpportunityId"] = a.OpportunityKey is null ? "" : "1",
                ["DueDate"] = a.DueDate is { } d ? D(d) : "",
                ["DueTime"] = a.DueTime is { } t ? t.ToString("HH:mm", CultureInfo.InvariantCulture) : "",
                ["IsDone"] = a.IsDone ? "on" : "",
            });
        }
    }

    [Fact]
    public void The_edge_fx_variant_drops_current_sek_and_adds_two_open_nok_deals_while_the_default_is_clean()
    {
        var clean = DemoGenerator.Generate(Today);
        Assert.DoesNotContain(clean.Opportunities, o => o.Currency == "NOK");
        var edge = DemoGenerator.Generate(Today, new DemoOptions(FxEdge: true));
        var thisMonth = new DateOnly(Today.Year, Today.Month, 1);
        Assert.Contains(clean.FxRates, r => r.Currency == "SEK" && r.Month == thisMonth);
        Assert.DoesNotContain(edge.FxRates, r => r.Currency == "SEK" && r.Month == thisMonth);
        Assert.Equal(2, edge.Opportunities.Count(o => o.Currency == "NOK" && o.IsOpen));
        Assert.DoesNotContain(edge.FxRates, r => r.Currency == "NOK");
    }

    [Fact]
    public void Nothing_can_reach_a_real_person_or_company()
    {
        var s = DemoGenerator.Generate(Today);
        var emails = s.Contacts.Select(c => c.Email).Concat(s.Users.Select(u => u.Email)).ToList();
        Assert.All(emails, e => Assert.Matches(@"^[a-z0-9.]+@([a-z0-9-]+\.)*example\.com\z", e));
        Assert.Equal(emails.Count, emails.Select(e => e.ToUpperInvariant()).Distinct().Count());
        Assert.All(s.Accounts, a => Assert.Matches(@"^https://[a-z0-9-]+\.example\.com\z", a.Website));
        // L-3: phones only in the reserved fictional ranges (US +1 555 01xx, UK +44 7700 900xxx); every other country has none.
        var phones = s.Accounts.Select(a => (a.Country, Phone: a.Phone)).Concat(s.Contacts.SelectMany(c => new[] { (Country: AccountCountry(s, c), Phone: c.Phone), (Country: AccountCountry(s, c), Phone: c.Mobile) })).ToList();
        Assert.All(phones.Where(p => p.Country != "US" && p.Country != "GB"), p => Assert.Null(p.Phone));
        Assert.All(phones.Where(p => p.Country == "US"), p => Assert.Matches(FakePhone(), p.Phone!));
        Assert.Contains(phones, p => p.Country == "US");
        // Free text never carries a real address or number.
        Assert.All(s.Activities, a => Assert.DoesNotMatch(@"@(?!example\.com)|https?://|\d{6,}", (a.Subject + " " + a.Description)));
    }

    private static string AccountCountry(DemoDataset s, DemoContact c) => c.AccountKey is null ? "DK" : s.Accounts.Single(a => a.Key == c.AccountKey).Country;

    /// <summary>D9: the cheap edge cases from the brief exist.</summary>
    [Fact]
    public void Edge_cases_from_the_brief_exist()
    {
        var s = DemoGenerator.Generate(Today);
        Assert.Contains(s.Accounts, a => a.Name.Length is >= 100 and <= 120);
        Assert.Contains(s.Accounts, a => a.Notes.Length is >= 1_400 and <= 2_000);
        var keyCounts = s.Opportunities.GroupBy(o => o.AccountKey).Select(g => g.Count()).ToList();
        Assert.True(keyCounts.Max() >= 12);
        Assert.Equal(8, s.Contacts.Count(c => c.AccountKey == DemoCatalog.KeyAccountKey));
        Assert.Contains(s.Accounts, a => s.Contacts.All(c => c.AccountKey != a.Key));
        Assert.Single(s.Contacts, c => c.AccountKey is null);
        Assert.Contains(s.Contacts, c => c.JobTitle.Length > 100);
        Assert.Contains(s.Activities, a => a.Subject.Length == 160);
        Assert.Contains(s.Activities, a => a.Description is { Length: > 2_800 });
        Assert.Contains(s.Activities, a => a.IsDone && a.DueDate > Today);
        // Everything still fits the columns.
        Assert.All(s.Accounts, a => Assert.True(a.Name.Length <= 120 && a.Notes.Length <= 2_000));
        Assert.All(s.Opportunities, o => Assert.True(o.Name.Length <= 120));
        Assert.All(s.Activities, a => Assert.True(a.Subject.Length <= 160 && (a.Description?.Length ?? 0) <= 4_000));
    }

    [Fact]
    public void Slug_is_ascii_and_stable()
    {
        Assert.Equal("kuestenfeld-rotorblatt", DemoCatalog.Slug("Küstenfeld Rotorblatt"));
        Assert.Equal("soren", DemoCatalog.Slug("Søren"));
        Assert.Equal("pawel.wisniewski", DemoCatalog.Slug("Paweł", "") + "." + DemoCatalog.Slug("Wiśniewski", ""));
        Assert.Equal("nordic-lime-mortel", DemoCatalog.Slug("Nordic Lime & Mørtel"));
    }
}
