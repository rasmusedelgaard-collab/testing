using System.Globalization;
using Crm.Web.Services.Demo;

namespace Crm.Tests.Unit;

/// <summary>
/// QA checks of the generated demo dataset against docs/ux/demo-data-brief.md ("What alive should look like"), evaluated on the
/// generator output so they run in every CI run without SQL Server. Each test names the brief section it covers.
/// </summary>
public sealed class DemoBriefAcceptanceTests
{
    private static readonly DateOnly Anchor = new(2026, 11, 19);

    private static decimal Dkk(DemoDataset s, DemoOpportunity o)
    {
        if (o.Currency == "DKK") { return o.Amount; }
        var month = new DateOnly(o.CloseDate.Year, o.CloseDate.Month, 1);
        var rate = s.FxRates.Where(r => r.Currency == o.Currency && r.Month <= month).MaxBy(r => r.Month)?.Rate ?? 0m;
        return o.Amount * rate;
    }

    private static IEnumerable<DemoOpportunity> Won(DemoDataset s) => s.Opportunities.Where(o => o.StageSlot == DemoOpportunity.SlotWon);

    private static IEnumerable<DemoOpportunity> Lost(DemoDataset s) => s.Opportunities.Where(o => o.StageSlot == DemoOpportunity.SlotLost);

    private static decimal WonDkk(DemoDataset s, DateOnly from, DateOnly to) => Won(s).Where(o => o.CloseDate >= from && o.CloseDate <= to).Sum(o => Dkk(s, o));

    /// <summary>Brief check 1: every KPI card has a value and the trend comparisons have data on both sides, on every day of a leap year (month ends, quarter starts, Jan 1).</summary>
    [Fact]
    public void Every_day_of_the_year_gives_non_empty_kpis_and_both_win_rate_windows()
    {
        var problems = new List<string>();
        for (var d = new DateOnly(2028, 1, 1); d <= new DateOnly(2028, 12, 31); d = d.AddDays(1))
        {
            var s = DemoGenerator.Generate(d);
            var open = s.Opportunities.Where(o => o.IsOpen).ToList();
            var monthStart = new DateOnly(d.Year, d.Month, 1);
            var qStart = new DateOnly(d.Year, ((d.Month - 1) / 3 * 3) + 1, 1);
            var prevMonth = monthStart.AddMonths(-1);
            var prevQ = qStart.AddMonths(-3);
            var elapsedQ = d.DayNumber - qStart.DayNumber;
            if (open.Count == 0 || open.Sum(o => Dkk(s, o)) == 0m) { problems.Add($"{d:yyyy-MM-dd}: open pipeline empty"); }
            if (WonDkk(s, monthStart, d) == 0m) { problems.Add($"{d:yyyy-MM-dd}: won this month is 0"); }
            if (WonDkk(s, qStart, d) == 0m) { problems.Add($"{d:yyyy-MM-dd}: won this quarter is 0"); }
            // D6/D7: the comparison windows of the Won cards are never empty (>= 8 won deals per month, the first on day 1), so the cards show Up/Down.
            var elapsedM = d.DayNumber - monthStart.DayNumber;
            var prevMonthEnd = Min(prevMonth.AddDays(elapsedM), monthStart.AddDays(-1));
            var prevQEnd = Min(prevQ.AddDays(elapsedQ), qStart.AddDays(-1));
            if (WonDkk(s, prevMonth, prevMonthEnd) == 0m) { problems.Add($"{d:yyyy-MM-dd}: won in the same days last month is 0"); }
            if (WonDkk(s, prevQ, prevQEnd) == 0m) { problems.Add($"{d:yyyy-MM-dd}: won in the same days last quarter is 0"); }
            // D2: the demo persona has exactly 2 overdue, 3 due today and 5 in the next 7 days, whatever the date.
            var persona = s.Activities.Where(a => a.OwnerKey == DemoMarker.PersonaKey && !a.IsDone && a.Type != "note").ToList();
            var mine = (persona.Count(a => a.DueDate < d), persona.Count(a => a.DueDate == d), persona.Count(a => a.DueDate > d && a.DueDate <= d.AddDays(7)));
            if (mine != (2, 3, 5)) { problems.Add($"{d:yyyy-MM-dd}: persona My day is {mine}, expected (2, 3, 5)"); }
            var closed = Won(s).Concat(Lost(s)).ToList();
            foreach (var (from, to) in new[] { (0, 90), (90, 180) })
            {
                if (!closed.Any(o => o.CloseDate > d.AddDays(-to) && o.CloseDate <= d.AddDays(-from))) { problems.Add($"{d:yyyy-MM-dd}: no closed deals in win-rate window {from}-{to}"); }
            }
            if (!open.Any(o => o.CloseDate < d)) { problems.Add($"{d:yyyy-MM-dd}: no open deal with a past close date"); }
        }
        // Report the first few only; the full list is in the assertion message of a failing run.
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(25)) + $"\n({problems.Count} problems)");
    }

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;

    /// <summary>Brief check 1 (trends): month to date is up and quarter to date is a little down at the brief's date, so both directions show.</summary>
    [Fact]
    public void Month_to_date_is_up_and_quarter_to_date_slightly_down()
    {
        var s = DemoGenerator.Generate(Anchor);
        var mtd = WonDkk(s, new(2026, 11, 1), Anchor);
        var prevMtd = WonDkk(s, new(2026, 10, 1), new(2026, 10, 19));
        var qtd = WonDkk(s, new(2026, 10, 1), Anchor);
        var prevQtd = WonDkk(s, new(2026, 7, 1), new(2026, 8, 19));
        Assert.True(mtd > prevMtd, $"month to date {mtd:N0} vs {prevMtd:N0}");
        Assert.InRange(qtd / prevQtd, 0.85m, 0.999m);
    }

    /// <summary>Brief "Opportunities": the win rate is better in the last 90 days, won revenue per full month is 1.5-3.5 mio (June and December highest), 8+ won deals a month.</summary>
    [Fact]
    public void Won_revenue_per_month_follows_the_brief()
    {
        var s = DemoGenerator.Generate(Anchor);
        var closed = Won(s).Concat(Lost(s)).ToList();
        Assert.InRange(closed.Count, 220, 250);
        double Rate(int from, int to) { var c = closed.Where(o => o.CloseDate > Anchor.AddDays(-to) && o.CloseDate <= Anchor.AddDays(-from)).ToList(); return (double)c.Count(o => o.StageSlot == DemoOpportunity.SlotWon) / c.Count; }
        Assert.True(Rate(0, 90) > Rate(90, 180));
        var byMonth = Won(s).GroupBy(o => new DateOnly(o.CloseDate.Year, o.CloseDate.Month, 1)).ToDictionary(g => g.Key, g => (Count: g.Count(), Dkk: g.Sum(o => Dkk(s, o))));
        foreach (var (month, v) in byMonth.Where(m => m.Key < new DateOnly(2026, 11, 1)))
        {
            Assert.True(v.Count >= 8, $"{month:yyyy-MM}: {v.Count} won");
            Assert.InRange(v.Dkk, 1_200_000m, 4_000_000m);
        }
        var typical = byMonth.Where(m => m.Key.Month is not (6 or 12) && m.Key < new DateOnly(2026, 11, 1)).Average(m => m.Value.Dkk);
        Assert.All(byMonth.Where(m => m.Key.Month is 6 or 12), m => Assert.True(m.Value.Dkk > typical, $"{m.Key:yyyy-MM} is a peak month"));
    }

    /// <summary>Brief "Lost reasons": Price 35, Competitor 25, No decision 20, Timing 12, No budget 5, Other 3 (percent).</summary>
    [Fact]
    public void Lost_reasons_follow_the_brief_mix()
    {
        var s = DemoGenerator.Generate(Anchor);
        var lost = Lost(s).ToList();
        var expected = new Dictionary<string, int> { ["Price"] = 35, ["Competitor"] = 25, ["No decision"] = 20, ["Timing"] = 12, ["No budget"] = 5, ["Other"] = 3 };
        foreach (var (reason, percent) in expected)
        {
            Assert.InRange(100.0 * lost.Count(o => o.LostReason == reason) / lost.Count, percent - 1.5, percent + 1.5);
        }
    }

    /// <summary>Brief "Activities": every other active user has 0-3 overdue, 1-4 due today and 2-6 in the next 7 days.</summary>
    [Fact]
    public void Other_users_stay_inside_the_brief_ranges_and_the_persona_gets_no_extras()
    {
        var s = DemoGenerator.Generate(Anchor);
        foreach (var u in s.Users.Where(u => u.Role != "Admin" && u.Active && u.Key != DemoMarker.PersonaKey))
        {
            var open = s.Activities.Where(a => a.OwnerKey == u.Key && !a.IsDone && a.Type != "note").ToList();
            Assert.InRange(open.Count(a => a.DueDate < Anchor), 0, 3);
            Assert.InRange(open.Count(a => a.DueDate == Anchor), 1, 4);
            Assert.InRange(open.Count(a => a.DueDate > Anchor && a.DueDate <= Anchor.AddDays(7)), 2, 6);
        }
        var persona = s.Activities.Where(a => a.OwnerKey == DemoMarker.PersonaKey && !a.IsDone && a.Type != "note").ToList();
        Assert.Equal(2, persona.Count(a => a.DueDate < Anchor));
        Assert.Equal(3, persona.Count(a => a.DueDate == Anchor));
        Assert.Equal(5, persona.Count(a => a.DueDate > Anchor && a.DueDate <= Anchor.AddDays(7)));
        Assert.Equal(2, persona.Count(a => a.DueDate == Anchor && a.DueTime is not null));
    }

    /// <summary>L-3: phone numbers and addresses can never reach a real person.</summary>
    [Fact]
    public void Phones_use_only_reserved_ranges_and_websites_are_example_com()
    {
        var s = DemoGenerator.Generate(Anchor);
        var countries = s.Accounts.ToDictionary(a => a.Key, a => a.Country);
        Assert.All(s.Accounts.Where(a => a.Country != "US"), a => Assert.Null(a.Phone));
        Assert.All(s.Accounts.Where(a => a.Country == "US"), a => Assert.Matches(@"^\+1 555 01\d\d\z", a.Phone!));
        Assert.All(s.Contacts.Where(c => c.AccountKey is not null && countries[c.AccountKey] != "US"), c => { Assert.Null(c.Phone); Assert.Null(c.Mobile); });
        Assert.All(s.Accounts, a => Assert.Matches(@"^https://[a-z0-9-]+\.example\.com\z", a.Website));
    }

    /// <summary>Brief check 1 (sparklines): twelve months of won revenue with visible variation.</summary>
    [Fact]
    public void The_won_sparkline_has_twelve_months_of_visible_variation()
    {
        var s = DemoGenerator.Generate(Anchor);
        var months = Enumerable.Range(0, 12).Select(i => new DateOnly(Anchor.Year, Anchor.Month, 1).AddMonths(-11 + i)).ToList();
        var values = months.Select(m => WonDkk(s, m, m.AddMonths(1).AddDays(-1))).ToList();
        Assert.All(values, v => Assert.True(v > 0m));
        Assert.True(values.Max() / values.Min() >= 1.5m, "the line must not be flat");
    }

    /// <summary>Brief check 3: four populated, strictly decreasing funnel columns and at least one past close date.</summary>
    [Fact]
    public void The_funnel_has_four_decreasing_columns_and_a_past_close_date()
    {
        var s = DemoGenerator.Generate(Anchor);
        var counts = Enumerable.Range(0, 4).Select(i => s.Opportunities.Count(o => o.IsOpen && o.StageSlot == i)).ToList();
        Assert.Equal(counts.OrderByDescending(c => c), counts);
        Assert.Equal(counts.Distinct().Count(), counts.Count);
        Assert.Contains(s.Opportunities, o => o.IsOpen && o.CloseDate < Anchor);
        // The column cap of the board is 20 cards: the first column must exercise "Show all 25 in Qualification".
        Assert.True(counts[0] > 20);
    }

    /// <summary>Brief check 4: each of the five Analytics charts has data for the three standard periods.</summary>
    [Theory]
    [InlineData("this-quarter")]
    [InlineData("this-year")]
    [InlineData("last-12-months")]
    public void Every_analytics_chart_has_data_in_the_period(string period)
    {
        var s = DemoGenerator.Generate(Anchor);
        var (from, to) = period switch
        {
            "this-quarter" => (new DateOnly(2026, 10, 1), new DateOnly(2026, 12, 31)),
            "this-year" => (new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)),
            _ => (new DateOnly(2025, 12, 1), new DateOnly(2026, 11, 30)),
        };
        bool In(DateOnly d) => d >= from && d <= to;
        var opps = s.Opportunities.Where(o => In(o.CloseDate)).ToList();
        Assert.Contains(opps, o => o.IsOpen); // pipeline by stage
        Assert.Contains(opps, o => o.StageSlot == DemoOpportunity.SlotWon); // won revenue by month
        Assert.True(opps.Where(o => !o.IsOpen).Select(o => o.OwnerKey).Distinct().Count() >= 3); // win rate by owner
        Assert.True(opps.Select(o => o.LeadSource).Distinct().Count() >= 3); // lead source
        Assert.Contains(s.Activities, a => (a.DueDate ?? DateOnly.FromDateTime(a.CreatedAt)) is var d && In(d)); // activities by type
    }

    /// <summary>Brief check 5: all three segments are present as account industries (the picklist Code decides the chip).</summary>
    [Fact]
    public void All_three_segments_exist_with_their_picklist_codes()
    {
        Assert.Equal([("wind", "Wind energy"), ("building", "Building materials"), ("industry", "Industry")], DemoCatalog.IndustryValues.ToArray());
        var s = DemoGenerator.Generate(Anchor);
        foreach (var (_, label) in DemoCatalog.IndustryValues) { Assert.True(s.Accounts.Count(a => a.Industry == label) >= 8, label); }
    }

    /// <summary>Brief check 6: the no-team user and the inactive owner.</summary>
    [Fact]
    public void The_no_team_user_owns_records_and_the_inactive_owner_only_closed_deals()
    {
        var s = DemoGenerator.Generate(Anchor);
        Assert.Contains(s.Accounts, a => a.OwnerKey == DemoMarker.NoTeamKey);
        Assert.Contains(s.Opportunities, o => o.OwnerKey == DemoMarker.NoTeamKey);
        Assert.Contains(s.Activities, a => a.OwnerKey == DemoMarker.NoTeamKey);
        var peter = s.Opportunities.Where(o => o.OwnerKey == DemoMarker.InactiveKey).ToList();
        Assert.Equal(3, peter.Count);
        Assert.All(peter, o => Assert.False(o.IsOpen));
        Assert.DoesNotContain(s.Activities, a => a.OwnerKey == DemoMarker.InactiveKey && !a.IsDone);
        Assert.Equal(1, s.Users.Count(u => !u.Active));
        Assert.Equal(1, s.Users.Count(u => u.TeamKey is null && u.Role != "Admin"));
        Assert.DoesNotContain(s.Accounts.Concat<object>(s.Contacts).Concat(s.Opportunities).Concat(s.Activities), r => r is DemoAccount { OwnerKey: "admin" } or DemoContact { OwnerKey: "admin" } or DemoOpportunity { OwnerKey: "admin" } or DemoActivity { OwnerKey: "admin" });
    }

    /// <summary>Brief "Hard rules": invented people only, reserved domains, ASCII email local parts, all dates in the past or relative to today.</summary>
    [Fact]
    public void Emails_are_ascii_on_reserved_domains_and_history_is_never_in_the_future()
    {
        var s = DemoGenerator.Generate(Anchor);
        var emails = s.Users.Select(u => u.Email).Concat(s.Contacts.Select(c => c.Email)).ToList();
        Assert.All(emails, e =>
        {
            Assert.Matches(@"^[a-z0-9.]+@([a-z0-9-]+\.)*example\.com$", e);
            Assert.Equal(e, e.Normalize(System.Text.NormalizationForm.FormD));
        });
        Assert.Equal(emails.Count, emails.Select(e => e.ToUpperInvariant()).Distinct().Count());
        Assert.All(s.Users, u => Assert.Matches(@"^[a-z]+\.[a-z]+@example\.com$", u.Email));
        var now = Anchor.ToDateTime(new TimeOnly(23, 59));
        Assert.All(s.Opportunities.Where(o => !o.IsOpen), o => Assert.True(o.ClosedAt <= now && o.CloseDate <= Anchor));
        Assert.All(s.Activities.Where(a => a.IsDone), a => Assert.True(a.DoneAt <= now));
        Assert.All(s.Accounts, a => Assert.True(a.CreatedAt <= now));
    }

    /// <summary>Brief "FX rates", clean variant: every month for every currency, and the edge variant removes only SEK of the current month.</summary>
    [Fact]
    public void Clean_fx_has_no_gaps_and_the_edge_variant_differs_only_by_current_sek_and_two_nok_deals()
    {
        var clean = DemoGenerator.Generate(Anchor);
        var edge = DemoGenerator.Generate(Anchor, new DemoOptions(FxEdge: true));
        var month = new DateOnly(Anchor.Year, Anchor.Month, 1);
        Assert.Equal(3 * (clean.FxRates.Count / 3), clean.FxRates.Count);
        Assert.Equal(clean.FxRates.Where(r => !(r.Currency == "SEK" && r.Month == month)).ToList(), edge.FxRates.ToList());
        Assert.DoesNotContain(edge.FxRates, r => r.Currency == "NOK");
        var nok = edge.Opportunities.Where(o => o.Currency == "NOK").ToList();
        Assert.Equal(2, nok.Count);
        Assert.All(nok, o => Assert.True(o.IsOpen && o.Amount > 0m));
        Assert.DoesNotContain(clean.Opportunities, o => o.Currency == "NOK");
        // Everything else is identical: the edge variant must not reshuffle the dataset.
        Assert.Equal(clean.Accounts, edge.Accounts);
        Assert.Equal(clean.Opportunities.Count, edge.Opportunities.Count);
        Assert.Equal(clean.Opportunities.Count(o => o.IsOpen), edge.Opportunities.Count(o => o.IsOpen));
    }

    /// <summary>Brief "Through the rules": the owner a record is written with is the owner the activities and contacts hang under.</summary>
    [Fact]
    public void Keys_resolve_and_children_belong_to_their_parents()
    {
        var s = DemoGenerator.Generate(Anchor);
        var accounts = s.Accounts.ToDictionary(a => a.Key);
        var contacts = s.Contacts.ToDictionary(c => c.Key);
        var opps = s.Opportunities.ToDictionary(o => o.Key);
        Assert.Equal(s.Opportunities.Count, opps.Count);
        Assert.All(s.Contacts.Where(c => c.AccountKey is not null), c => Assert.Equal(accounts[c.AccountKey!].OwnerKey, c.OwnerKey));
        Assert.All(s.Opportunities.Where(o => o.ContactKey is not null), o => Assert.Equal(o.AccountKey, contacts[o.ContactKey!].AccountKey));
        Assert.All(s.Activities.Where(a => a.ContactKey is not null), a => Assert.Equal(a.AccountKey, contacts[a.ContactKey!].AccountKey));
        Assert.All(s.Activities.Where(a => a.OpportunityKey is not null), a => Assert.Equal(a.AccountKey, opps[a.OpportunityKey!].AccountKey));
        Assert.All(s.Activities, a => Assert.Equal(accounts[a.AccountKey].OwnerKey, a.OwnerKey));
        // Owner change history never names the record's current owner or an inactive/no-team stranger as the previous owner.
        var users = s.Users.ToDictionary(u => u.Key);
        Assert.All(s.Opportunities.Where(o => o.PreviousOwnerKey is not null), o =>
        {
            Assert.NotEqual(o.OwnerKey, o.PreviousOwnerKey);
            Assert.Equal(users[o.OwnerKey].TeamKey, users[o.PreviousOwnerKey!].TeamKey);
            Assert.NotNull(o.OwnerChangedAt);
        });
    }

    [Fact]
    public void Generated_text_never_uses_the_current_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            var tr = DemoGenerator.Generate(Anchor);
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var inv = DemoGenerator.Generate(Anchor);
            Assert.Equal(inv.Accounts, tr.Accounts);
            Assert.Equal(inv.Contacts, tr.Contacts);
            Assert.Equal(inv.Opportunities.Select(o => (o.Key, o.Name, o.Amount)), tr.Opportunities.Select(o => (o.Key, o.Name, o.Amount)));
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }
}
