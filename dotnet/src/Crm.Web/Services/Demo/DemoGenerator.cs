using System.Globalization;

namespace Crm.Web.Services.Demo;

/// <summary>
/// Pure, deterministic demo data generator: the same (<c>today</c>, fixed seed) always gives the same dataset, and every date is relative to
/// <c>today</c> so the data stays fresh. No I/O. The seeder maps keys to database ids; amounts are stored in their own currency.
/// </summary>
public static class DemoGenerator
{
    public const ulong Seed = 20_261_119UL;
    public const int HistoryMonths = 18;
    public const int OpenTotal = 66; // funnel 25 / 18 / 14 / 9
    public const int ActivityTotal = 250;
    public const int ContactTotal = 100;

    /// <summary>Won deals per full month (D6/D7: enough that the previous month-to-date and quarter-to-date are never empty), and in June and December.</summary>
    private const int WonPerMonth = 8;
    private const int WonPerPeakMonth = 12;
    private const decimal MinDkk = 55_000m;
    private const decimal MaxDkk = 4_900_000m;

    private enum Bucket { Overdue, RestOfMonth, NextMonth, NextQuarter, Later }

    private sealed record OppSpec(bool Won, bool Lost, int MonthsBack, Bucket Bucket, int Day = 0);

    public static DemoDataset Generate(DateOnly today, DemoOptions? options = null)
    {
        var rng = new DemoRandom(Seed);
        var fx = BuildFx(today, rng);
        var edge = options?.FxEdge == true;
        var users = DemoCatalog.Users;
        var accounts = BuildAccounts(today, rng);
        var contacts = BuildContacts(accounts, today, rng);
        var opps = BuildOpportunities(today, rng, accounts, contacts, fx, edge);
        var activities = BuildActivities(today, rng, accounts, contacts, opps);
        if (edge)
        {
            fx = fx.Where(r => !(r.Currency == "SEK" && r.Month == FirstOfMonth(today))).ToList();
        }
        return new DemoDataset(today, DemoCatalog.Teams, users, accounts, contacts, opps, activities, fx);
    }

    private static DateTime At(DateOnly d, int hour, int minute = 0) => d.ToDateTime(new TimeOnly(hour, minute));

    private static DateOnly FirstOfMonth(DateOnly d) => new(d.Year, d.Month, 1);

    // ---------- FX ----------

    private static List<DemoFxRate> BuildFx(DateOnly today, DemoRandom rng)
    {
        var first = FirstOfMonth(today).AddMonths(-HistoryMonths - 1);
        var rates = new List<DemoFxRate>();
        decimal eur = 7.4600m, sek = 0.6850m, usd = 6.8500m;
        for (var m = first; m <= FirstOfMonth(today); m = m.AddMonths(1))
        {
            rates.Add(new("EUR", m, eur));
            rates.Add(new("SEK", m, sek));
            rates.Add(new("USD", m, usd));
            eur = Math.Clamp(eur + rng.Range(-4, 4) / 10_000m, 7.4550m, 7.4650m);
            sek = Math.Clamp(sek + rng.Range(-60, 60) / 10_000m, 0.6600m, 0.7100m);
            usd = Math.Clamp(usd - 0.025m + rng.Range(-50, 50) / 1_000m, 6.3500m, 7.0500m);
        }
        return rates;
    }

    private static decimal RateTo(IReadOnlyList<DemoFxRate> fx, string currency, DateOnly date)
    {
        if (currency == "DKK") { return 1m; }
        var month = FirstOfMonth(date);
        var series = fx.Where(r => r.Currency == currency).OrderBy(r => r.Month).ToList();
        return (series.LastOrDefault(r => r.Month <= month) ?? series[0]).Rate;
    }

    // ---------- Accounts and contacts ----------

    private static List<DemoAccount> BuildAccounts(DateOnly today, DemoRandom rng)
    {
        var perTeam = new Dictionary<string, int>();
        var list = new List<DemoAccount>();
        for (var i = 0; i < DemoCatalog.Companies.Count; i++)
        {
            var c = DemoCatalog.Companies[i];
            var team = DemoCatalog.TeamOfCountry(c.Country);
            // Managers own twice as many accounts as users, so the demo persona (a Manager) has a full day. One user has no team at all.
            var members = DemoCatalog.Users.Where(u => u.TeamKey == team && u.Active).SelectMany(u => u.Role == "Manager" ? new[] { u, u } : new[] { u }).ToList();
            var n = perTeam.GetValueOrDefault(team);
            var noTeam = DemoCatalog.NoTeamAccounts.Contains(i);
            if (!noTeam) { perTeam[team] = n + 1; }
            var owner = noTeam ? DemoCatalog.Users.First(u => u.Key == DemoMarker.NoTeamKey) : members[n % members.Count];
            var slug = DemoCatalog.Slug(c.Short);
            var street = $"{rng.Pick(DemoCatalog.Streets[c.Country])} {rng.Range(1, 120)}";
            string? industry = i == DemoCatalog.NoIndustryCompany ? null : DemoCatalog.IndustryLabel(c.Segment); // one account has no industry (empty-state case)
            list.Add(new DemoAccount(
                Key: "A" + (i + 1).ToString("000", CultureInfo.InvariantCulture),
                Name: c.Name,
                Website: $"https://{slug}.{DemoCatalog.Domain}",
                Phone: Phone(c.Country, rng),
                Industry: industry,
                Street: street,
                PostalCode: DemoCatalog.PostalCode(c.Country, rng),
                City: c.City,
                Country: c.Country,
                Notes: i == DemoCatalog.LongNotesCompany ? DemoCatalog.LongNotes(c.Segment) : $"Segment: {DemoCatalog.SegmentLabel(c.Segment)}. Fictitious demo account.",
                OwnerKey: owner.Key,
                CreatedAt: At(today.AddDays(-rng.Range(600, 720)), rng.Range(8, 15), rng.Range(0, 59))));
        }
        return list;
    }

    /// <summary>
    /// Only ranges reserved for fiction: US +1 555 01xx and UK +44 7700 900xxx. Other countries have no such range, so the phone stays empty.
    /// The random draw is always made, so the data of the other fields does not shift with this rule.
    /// </summary>
    private static string? Phone(string country, DemoRandom rng)
    {
        var n = rng.Range(0, 999);
        return country switch
        {
            "US" => $"+1 555 01{(n % 100).ToString("00", CultureInfo.InvariantCulture)}",
            "GB" => $"+44 7700 900{n.ToString("000", CultureInfo.InvariantCulture)}",
            _ => null,
        };
    }

    private static List<DemoContact> BuildContacts(IReadOnlyList<DemoAccount> accounts, DateOnly today, DemoRandom rng)
    {
        // The key account has 8 contacts, the "no contacts" account none, the rest 2 or 3; one more contact has no account at all.
        var regular = accounts.Count - 2;
        var extra = ContactTotal - 1 - DemoCatalog.KeyAccountContacts - (2 * regular);
        var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<DemoContact>();
        DemoContact Make(string? accountKey, string country, string domain, string ownerKey, DateTime createdAt, bool noFirstName = false, string? jobTitle = null)
        {
            var (first, last) = DemoCatalog.Names[country];
            var fn = rng.Pick(first);
            var ln = rng.Pick(last);
            var email = $"{DemoCatalog.Slug(fn, "")}.{DemoCatalog.Slug(ln, "")}@{domain}";
            for (var n = 2; !emails.Add(email); n++)
            {
                email = $"{DemoCatalog.Slug(fn, "")}.{DemoCatalog.Slug(ln, "")}{n.ToString(CultureInfo.InvariantCulture)}@{domain}";
            }
            return new DemoContact(
                Key: "C" + (list.Count + 1).ToString("000", CultureInfo.InvariantCulture),
                AccountKey: accountKey,
                FirstName: noFirstName ? null : fn,
                LastName: ln,
                JobTitle: jobTitle ?? rng.Pick(DemoCatalog.JobTitles),
                Email: email,
                Phone: Phone(country, rng),
                Mobile: Phone(country, rng),
                OwnerKey: ownerKey,
                CreatedAt: createdAt.AddDays(rng.Range(0, 200)).AddHours(rng.Range(0, 5)));
        }
        for (var i = 0; i < accounts.Count; i++)
        {
            var a = accounts[i];
            if (a.Key == DemoCatalog.NoContactsAccountKey) { continue; }
            var count = a.Key == DemoCatalog.KeyAccountKey ? DemoCatalog.KeyAccountContacts : 2 + (i % 2 == 0 && extra > 0 ? 1 : 0);
            if (count == 3) { extra--; }
            var domain = a.Website["https://".Length..];
            for (var k = 0; k < count; k++)
            {
                // List position 4 is the contact without a first name; the first contact of the second account has a very long job title.
                var noFirstName = list.Count == 4;
                var longTitle = list.Count == 3 ? DemoCatalog.LongJobTitle : null;
                list.Add(Make(a.Key, a.Country, domain, a.OwnerKey, a.CreatedAt, noFirstName, longTitle));
            }
        }
        // A contact who belongs to no account (a private person at an event, say): Danish, owned by the first Nordics user.
        var orphanOwner = DemoCatalog.Users.First(u => u.TeamKey == "nordics" && u.Active && u.Role == "User");
        list.Add(Make(null, "DK", DemoCatalog.Domain, orphanOwner.Key, At(today.AddDays(-rng.Range(100, 300)), 10), jobTitle: "Independent consultant"));
        return list;
    }

    // ---------- Opportunities ----------

    private static List<DemoOpportunity> BuildOpportunities(DateOnly today, DemoRandom rng, IReadOnlyList<DemoAccount> accounts, IReadOnlyList<DemoContact> contacts, IReadOnlyList<DemoFxRate> fx, bool edge)
    {
        // Closed deals: at least 8 won per month spread evenly over the days (first on day 1), more in June and December; fewer lost deals in the
        // last three months than before (the win-rate trend shows "up"). Lost reasons follow the brief's mix exactly, dealt from a shuffled deck.
        var closed = PlanClosed(today, rng);
        var reasons = new Queue<string>(LostReasonDeck(closed.Count(c => c.Lost), rng));
        var openCount = OpenTotal;

        // Open funnel 25 / 18 / 14 / 9 (scaled if the total ever changes), close dates from a fixed mix so every bucket is populated.
        var slots = new List<int>();
        int[] funnel = [25, 18, 14, 9];
        for (var st = 0; st < funnel.Length; st++) { slots.AddRange(Enumerable.Repeat(st, funnel[st])); }
        while (slots.Count < openCount) { slots.Add(0); }
        slots = slots.Take(openCount).ToList();
        rng.Shuffle(slots);
        Bucket[] mix = [Bucket.Overdue, Bucket.RestOfMonth, Bucket.NextMonth, Bucket.NextQuarter, Bucket.NextMonth, Bucket.RestOfMonth, Bucket.NextQuarter, Bucket.Later, Bucket.NextMonth, Bucket.RestOfMonth, Bucket.NextQuarter, Bucket.NextMonth, Bucket.Later, Bucket.NextQuarter];

        var owners = DemoCatalog.Users.Where(u => u.Role != "Admin" && u.Active).Select(u => u.Key).ToList();
        var accountsByOwner = accounts.GroupBy(a => a.OwnerKey).ToDictionary(g => g.Key, g => g.ToList());
        var companies = DemoCatalog.Companies.ToDictionary(c => c.Name);
        var accountsByKey = accounts.ToDictionary(a => a.Key);
        var keyAccount = accountsByKey[DemoCatalog.KeyAccountKey];
        var keyDeals = 0;
        var usedScopes = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<DemoOpportunity>();

        DemoOpportunity Build(DemoAccount account, OppSpec spec, int openSlot, int ordinal)
        {
            var company = companies[account.Name];
            var scopes = DemoCatalog.Scopes[company.Segment];
            string scope;
            var tries = 0;
            do
            {
                scope = scopes[rng.Next(scopes.Length)];
                tries++;
            }
            while (tries < 30 && usedScopes.Contains(account.Key + "|" + scope));
            usedScopes.Add(account.Key + "|" + scope);
            var name = $"{company.Short} – {scope}";
            for (var n = 2; !names.Add(name); n++)
            {
                name = $"{company.Short} – {scope} (phase {n.ToString(CultureInfo.InvariantCulture)})";
            }
            var accountContacts = contacts.Where(c => c.AccountKey == account.Key).ToList();
            var contactKey = accountContacts.Count > 0 && rng.Chance(0.9) ? rng.Pick(accountContacts).Key : null;
            var currency = rng.Weighted(DemoCatalog.CurrenciesFor(account.Country));
            var lead = rng.Weighted(DemoCatalog.LeadSources);
            var key = "O" + (ordinal + 1).ToString("000", CultureInfo.InvariantCulture);
            return spec.Won || spec.Lost
                ? Closed(key, name, account, contactKey, currency, lead, spec, spec.Lost ? reasons.Dequeue() : null, today, rng, fx)
                : Open(key, name, account, contactKey, currency, lead, spec.Bucket, openSlot, today, rng, fx);
        }

        // Closed deals are dealt to the owners in turn (oldest first within each round), so every owner has plenty of closed deals in the last 12 months.
        var ownerTurn = 0;
        foreach (var spec in closed.OrderByDescending(c => c.MonthsBack))
        {
            var owner = owners[ownerTurn++ % owners.Count];
            // The key account (many deals) takes every third turn of its owner, until it has 8 closed deals.
            var account = owner == keyAccount.OwnerKey && ownerTurn % 3 == 0 && keyDeals < 8 ? keyAccount : rng.Pick(accountsByOwner[owner]);
            if (account == keyAccount) { keyDeals++; }
            result.Add(Build(account, spec, 0, result.Count));
        }
        for (var i = 0; i < openCount; i++)
        {
            var account = i % 9 == 4 && keyDeals < DemoCatalog.KeyAccountMinOpportunities + 2 ? keyAccount : rng.Pick(accounts);
            if (account == keyAccount) { keyDeals++; }
            result.Add(Build(account, new OppSpec(false, false, 0, mix[i % mix.Length]), slots[i], result.Count));
        }
        ShapeWonRevenue(result, today, rng, fx);

        // The inactive user ("left the company") still owns three closed deals from the last year.
        var peter = DemoCatalog.Users.First(u => u.Key == DemoMarker.InactiveKey);
        var nordicOwners = DemoCatalog.Users.Where(u => u.TeamKey == peter.TeamKey && u.Active).Select(u => u.Key).ToHashSet();
        var lastYear = FirstOfMonth(today).AddMonths(-11);
        var moved = 0;
        for (var i = 0; i < result.Count && moved < 3; i++)
        {
            var o = result[i];
            if (!o.IsOpen && o.CloseDate >= lastYear && nordicOwners.Contains(o.OwnerKey))
            {
                result[i] = o with { OwnerKey = peter.Key };
                moved++;
            }
        }

        // Zero amount on one open deal, and (QA edge variant) two open deals in NOK, a currency without any rate.
        var firstOpen = result.FindIndex(o => o.IsOpen);
        result[firstOpen] = result[firstOpen] with { Amount = 0m };
        if (edge)
        {
            var nok = 0;
            for (var i = result.Count - 1; i >= 0 && nok < 2; i--)
            {
                if (result[i].IsOpen && result[i].Amount > 0m && result[i].Currency != "NOK")
                {
                    result[i] = result[i] with { Currency = "NOK", Amount = Math.Round(result[i].Amount * 0.7m / 100m) * 100m };
                    nok++;
                }
            }
        }
        return AddHistory(result, rng);
    }

    /// <summary>Stage changes (1-4 on ~40% of deals, otherwise just the last step) and, on ~10%, an owner change.</summary>
    private static List<DemoOpportunity> AddHistory(List<DemoOpportunity> opps, DemoRandom rng)
    {
        var users = DemoCatalog.Users;
        for (var i = 0; i < opps.Count; i++)
        {
            var o = opps[i];
            var end = o.ClosedAt ?? o.ModifiedAt;
            var changes = new List<DemoStageChange>();
            if (o.StageSlot > 0)
            {
                var full = rng.Chance(0.4);
                var last = o.IsOpen ? o.StageSlot : 3;
                var steps = new List<(int From, int To)>();
                if (full) { for (var s = 0; s < last; s++) { steps.Add((s, s + 1)); } }
                else { steps.Add((last - 1, last)); }
                if (!o.IsOpen) { steps.Add((3, o.StageSlot)); }
                if (steps.Count > 4) { steps = steps.Skip(steps.Count - 4).ToList(); }
                var span = end - o.CreatedAt;
                for (var k = 0; k < steps.Count; k++)
                {
                    var finalClosing = !o.IsOpen && k == steps.Count - 1;
                    var at = finalClosing ? end : o.CreatedAt + (span * (k + 1) / (steps.Count + (o.IsOpen ? 1 : 0))) + TimeSpan.FromMinutes(k + 1);
                    changes.Add(new DemoStageChange(steps[k].From, steps[k].To, at));
                }
            }
            string? previous = null;
            DateTime? changedAt = null;
            var owner = users.First(u => u.Key == o.OwnerKey);
            if (o.OwnerKey != DemoMarker.InactiveKey && owner.TeamKey is not null && rng.Chance(0.1))
            {
                var others = users.Where(u => u.TeamKey == owner.TeamKey && u.Active && u.Key != owner.Key).ToList();
                if (others.Count > 0)
                {
                    previous = rng.Pick(others).Key;
                    changedAt = o.CreatedAt + ((end - o.CreatedAt) / 3);
                }
            }
            opps[i] = o with { StageChanges = changes, PreviousOwnerKey = previous, OwnerChangedAt = changedAt };
        }
        return opps;
    }

    private static decimal AmountIn(string currency, DateOnly rateDate, DemoRandom rng, IReadOnlyList<DemoFxRate> fx)
    {
        // Skewed towards small deals: u^1.6 on a log scale between MinDkk and MaxDkk.
        var u = Math.Pow(rng.NextDouble(), 1.6);
        var dkk = (decimal)((double)MinDkk * Math.Pow((double)(MaxDkk / MinDkk), u));
        var local = dkk / RateTo(fx, currency, rateDate);
        var step = local >= 2_000_000m ? 5_000m : local >= 300_000m ? 1_000m : 100m;
        return Math.Round(local / step, 0, MidpointRounding.AwayFromZero) * step;
    }

    private static DemoOpportunity Closed(string key, string name, DemoAccount account, string? contactKey, string currency, string lead, OppSpec spec, string? lostReason, DateOnly today, DemoRandom rng, IReadOnlyList<DemoFxRate> fx)
    {
        var month = FirstOfMonth(today).AddMonths(-spec.MonthsBack);
        var maxDay = spec.MonthsBack == 0 ? today.Day : DateTime.DaysInMonth(month.Year, month.Month);
        var day = spec.Day > 0 ? Math.Min(spec.Day, maxDay) : rng.Range(1, maxDay);
        var close = month.AddDays(day - 1);
        var closedAt = At(close, close == today ? 6 : rng.Range(7, 16), rng.Range(0, 59));
        var created = At(close.AddDays(-rng.Range(25, 160)), rng.Range(8, 16), rng.Range(0, 59));
        if (created < account.CreatedAt) { created = account.CreatedAt.AddDays(1); }
        return new DemoOpportunity(key, name, account.Key, contactKey, spec.Won ? DemoOpportunity.SlotWon : DemoOpportunity.SlotLost,
            AmountIn(currency, close, rng, fx), currency, close, lead,
            lostReason, closedAt, account.OwnerKey, created, closedAt);
    }

    // ---------- Closed-deal plan and revenue shape ----------

    private static List<OppSpec> PlanClosed(DateOnly today, DemoRandom rng)
    {
        var plan = new List<OppSpec>();
        for (var back = 0; back < HistoryMonths; back++)
        {
            var month = FirstOfMonth(today).AddMonths(-back);
            var fullDays = DateTime.DaysInMonth(month.Year, month.Month);
            var days = back == 0 ? today.Day : fullDays;
            var wonFull = month.Month is 6 or 12 ? WonPerPeakMonth : WonPerMonth;
            var lostFull = back < 3 ? 3 : new[] { 4, 5, 5, 6 }[rng.Next(4)];
            var won = back == 0 ? Math.Max(1, (int)Math.Round((double)wonFull * days / fullDays)) : wonFull;
            var lost = back == 0 ? Math.Max(1, (int)Math.Round((double)lostFull * days / fullDays)) : lostFull;
            for (var k = 0; k < won; k++)
            {
                // Evenly over the days, the first on day 1 and the last on the last day, so any "same days" window of the previous month or quarter has a deal.
                var day = won == 1 ? 1 : 1 + (int)Math.Round(k * (days - 1) / (double)(won - 1));
                if (k > 0 && k < won - 1) { day = Math.Clamp(day + rng.Range(-1, 1), 2, Math.Max(2, days - 1)); }
                plan.Add(new OppSpec(true, false, back, Bucket.Overdue, Math.Min(day, days)));
            }
            for (var k = 0; k < lost; k++) { plan.Add(new OppSpec(false, true, back, Bucket.Overdue, rng.Range(1, days))); }
        }
        return plan;
    }

    /// <summary>Exactly the brief's mix (largest-remainder rounding), in random order.</summary>
    private static List<string> LostReasonDeck(int count, DemoRandom rng)
    {
        var total = DemoCatalog.LostReasons.Sum(r => r.Weight);
        var counts = DemoCatalog.LostReasons.Select(r => (Label: r.Label, Floor: count * r.Weight / total, Remainder: count * r.Weight % total)).ToList();
        var missing = count - counts.Sum(c => c.Floor);
        var bumped = counts.OrderByDescending(c => c.Remainder).Take(missing).Select(c => c.Label).ToHashSet();
        var deck = counts.SelectMany(c => Enumerable.Repeat(c.Label, c.Floor + (bumped.Contains(c.Label) ? 1 : 0))).ToList();
        rng.Shuffle(deck);
        return deck;
    }

    private static decimal ToDkk(DemoOpportunity o, IReadOnlyList<DemoFxRate> fx) => o.Amount * RateTo(fx, o.Currency, o.CloseDate);

    private static decimal RoundAmount(decimal local)
    {
        var step = local >= 2_000_000m ? 5_000m : local >= 300_000m ? 1_000m : 100m;
        return Math.Max(step, Math.Round(local / step, 0, MidpointRounding.AwayFromZero) * step);
    }

    private static DemoOpportunity Scale(DemoOpportunity o, decimal factor) => o with { Amount = RoundAmount(o.Amount * factor) };

    /// <summary>
    /// Won revenue per month lands between about 1.5 and 3.5 mio. DKK (June and December highest), by giving each month's won deals random shares of a monthly target.
    /// Then the two trends on the dashboard are tuned where the data allows it: month-to-date about 25% above the same days last month, and
    /// quarter-to-date a few percent below the same days last quarter (both directions on one screen). Pure maths on the generated deals.
    /// </summary>
    private static void ShapeWonRevenue(List<DemoOpportunity> opps, DateOnly today, DemoRandom rng, IReadOnlyList<DemoFxRate> fx)
    {
        bool IsWon(DemoOpportunity o) => o.StageSlot == DemoOpportunity.SlotWon;
        var months = opps.Where(IsWon).Select(o => FirstOfMonth(o.CloseDate)).Distinct().ToList();
        foreach (var month in months)
        {
            var full = DateTime.DaysInMonth(month.Year, month.Month);
            var shareOfMonth = month == FirstOfMonth(today) ? (decimal)today.Day / full : 1m;
            var target = (month.Month is 6 or 12 ? 3_100_000m + rng.Range(0, 300_000) : 1_800_000m + rng.Range(0, 900_000)) * shareOfMonth;
            // Shares of the month's total between 0.4 and 2.0 times the average: no tiny and no dominating deal.
            var idx = Enumerable.Range(0, opps.Count).Where(i => IsWon(opps[i]) && FirstOfMonth(opps[i].CloseDate) == month).ToList();
            if (idx.Count == 0) { continue; }
            var weights = idx.Select(_ => 0.4 + (rng.NextDouble() * 1.6)).ToList();
            var totalWeight = weights.Sum();
            for (var k = 0; k < idx.Count; k++)
            {
                var o = opps[idx[k]];
                var dkk = target * (decimal)(weights[k] / totalWeight);
                opps[idx[k]] = o with { Amount = RoundAmount(dkk / RateTo(fx, o.Currency, o.CloseDate)) };
            }
        }

        var monthStart = FirstOfMonth(today);
        var quarterStart = new DateOnly(today.Year, ((today.Month - 1) / 3 * 3) + 1, 1);
        var prevMonthStart = monthStart.AddMonths(-1);
        var prevQuarterStart = quarterStart.AddMonths(-3);
        var prevMonthEnd = Min(prevMonthStart.AddDays(today.DayNumber - monthStart.DayNumber), monthStart.AddDays(-1));
        var prevQuarterEnd = Min(prevQuarterStart.AddDays(today.DayNumber - quarterStart.DayNumber), quarterStart.AddDays(-1));
        decimal Sum(DateOnly from, DateOnly to) => opps.Where(o => IsWon(o) && o.CloseDate >= from && o.CloseDate <= to).Sum(o => ToDkk(o, fx));
        var mtd = Sum(monthStart, today);
        var prevMtd = Sum(prevMonthStart, prevMonthEnd);
        var prevQtd = Sum(prevQuarterStart, prevQuarterEnd);
        var earlierInQuarter = Sum(quarterStart, monthStart.AddDays(-1)); // complete months before this one, inside this quarter
        if (mtd == 0m || prevMtd == 0m || prevQtd == 0m) { return; }
        const decimal MtdUp = 1.25m;
        const decimal QtdDown = 0.93m;
        decimal a, b = 1m;
        if (earlierInQuarter > 0m)
        {
            // Last month lies inside this quarter and is scaled with the rest of it: solve for both factors at once.
            b = Math.Clamp(QtdDown * prevQtd / ((MtdUp * prevMtd) + earlierInQuarter), 0.7m, 1.4m);
            a = Math.Clamp(MtdUp * b * prevMtd / mtd, 0.6m, 1.8m);
        }
        else
        {
            a = Math.Clamp(MtdUp * prevMtd / mtd, 0.6m, 1.8m);
        }
        for (var i = 0; i < opps.Count; i++)
        {
            var o = opps[i];
            if (!IsWon(o)) { continue; }
            if (o.CloseDate >= monthStart && o.CloseDate <= today) { opps[i] = Scale(o, a); }
            else if (o.CloseDate >= quarterStart && o.CloseDate < monthStart) { opps[i] = Scale(o, b); }
        }
    }

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;

    private static DemoOpportunity Open(string key, string name, DemoAccount account, string? contactKey, string currency, string lead, Bucket bucket, int slot, DateOnly today, DemoRandom rng, IReadOnlyList<DemoFxRate> fx)
    {
        var thisMonth = FirstOfMonth(today);
        var monthEnd = thisMonth.AddMonths(1).AddDays(-1);
        var nextMonth = thisMonth.AddMonths(1);
        var quarterStart = new DateOnly(today.Year, ((today.Month - 1) / 3) * 3 + 1, 1).AddMonths(3);
        var close = bucket switch
        {
            Bucket.Overdue => today.AddDays(-rng.Range(1, 10)),
            Bucket.RestOfMonth => today.AddDays(rng.Range(0, Math.Max(0, monthEnd.DayNumber - today.DayNumber))),
            Bucket.NextMonth => nextMonth.AddDays(rng.Range(0, DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month) - 1)),
            Bucket.NextQuarter => quarterStart.AddDays(rng.Range(0, quarterStart.AddMonths(3).DayNumber - quarterStart.DayNumber - 1)),
            _ => quarterStart.AddMonths(3).AddDays(rng.Range(0, 180)),
        };
        var (ageLow, ageHigh) = slot switch { 0 => (1, 45), 1 => (5, 80), 2 => (14, 120), _ => (25, 150) };
        var ageDays = ageLow + (int)((ageHigh - ageLow) * Math.Pow(rng.NextDouble(), 1.8)); // weighted towards recent creation
        var created = At(today.AddDays(-ageDays), rng.Range(8, 16), rng.Range(0, 59));
        if (created < account.CreatedAt) { created = account.CreatedAt.AddDays(1); }
        var modified = At(today.AddDays(-rng.Range(0, Math.Min(ageDays, 30))), rng.Range(8, 16), rng.Range(0, 59));
        if (modified < created) { modified = created; }
        return new DemoOpportunity(key, name, account.Key, contactKey, slot, AmountIn(currency, today, rng, fx), currency, close, lead, null, null, account.OwnerKey, created, modified);
    }

    // ---------- Activities ----------

    private static readonly string[] Descriptions =
    [
        "Customer asked for a revised delivery schedule.", "Agreed to share drawings before the next call.", "Waiting for their purchasing team to confirm the volume.",
        "Technical questions about tolerances and certification.", "Price is the main concern; discussed a volume discount.", "Positive reaction, wants a written proposal.",
        "Competitor quote mentioned; need to follow up on lead times.", "Sample batch arrived and is being tested on site.",
    ];

    /// <summary>Three done activities become edge cases: a 160-character subject, a ~3,000-character description, and a done one whose due date is still ahead.</summary>
    private static void AddActivityEdgeCases(List<DemoActivity> list, DateOnly today)
    {
        var first = list.FindIndex(a => a.IsDone && a.Type != "note");
        var subject = "Quarterly business review with the technical and purchasing teams about the framework agreement, delivery schedule, certification requirements and the price adjustment";
        subject = (subject + " - follow-up with all attendees and the customer's quality department")[..160];
        list[first] = list[first] with { Subject = subject };
        var second = list.FindIndex(first + 1, a => a.IsDone && a.Type != "note");
        var text = new System.Text.StringBuilder();
        for (var i = 0; text.Length < 2_950; i++) { text.Append(Descriptions[i % Descriptions.Length]).Append(' '); }
        list[second] = list[second] with { Description = text.ToString().TrimEnd() };
        var third = list.FindIndex(second + 1, a => a.IsDone && a.Type != "note");
        var done = today.ToDateTime(new TimeOnly(8, 30));
        list[third] = list[third] with { DueDate = today.AddDays(3), DueTime = new TimeOnly(10, 0), DoneAt = done, ModifiedAt = done };
    }

    private static List<DemoActivity> BuildActivities(DateOnly today, DemoRandom rng, IReadOnlyList<DemoAccount> accounts, IReadOnlyList<DemoContact> contacts, IReadOnlyList<DemoOpportunity> opps)
    {
        var list = new List<DemoActivity>();
        var accountByKey = accounts.ToDictionary(a => a.Key);
        var contactsByAccount = contacts.Where(c => c.AccountKey is not null).GroupBy(c => c.AccountKey!).ToDictionary(g => g.Key, g => g.ToList());
        var contactByKey = contacts.ToDictionary(c => c.Key);
        var owners = DemoCatalog.Users.Where(u => u.Role != "Admin" && u.Active).Select(u => u.Key).ToList();
        var openByOwner = owners.ToDictionary(o => o, o => opps.Where(x => x.IsOpen && x.OwnerKey == o).ToList());
        var accountsByOwner = owners.ToDictionary(o => o, o => accounts.Where(a => a.OwnerKey == o).ToList());

        (DemoAccount Account, DemoContact? Contact, DemoOpportunity? Opp) Target(string? owner, bool preferOpen)
        {
            DemoOpportunity? opp = null;
            if (owner is not null && preferOpen && openByOwner[owner].Count > 0 && rng.Chance(0.85))
            {
                opp = rng.Pick(openByOwner[owner]);
            }
            else if (owner is null && preferOpen)
            {
                var open = opps.Where(x => x.IsOpen).ToList();
                opp = open.Count > 0 && rng.Chance(0.7) ? rng.Pick(open) : null;
            }
            opp ??= owner is null && !preferOpen && rng.Chance(0.7) ? rng.Pick(opps) : null;
            var account = opp is not null ? accountByKey[opp.AccountKey] : rng.Pick(owner is null ? accounts : accountsByOwner[owner]);
            var contact = opp?.ContactKey is { } ck ? contactByKey[ck] : rng.Chance(0.8) && contactsByAccount.TryGetValue(account.Key, out var pool) ? rng.Pick(pool) : null;
            return (account, contact, opp);
        }

        string Subject(string type, DemoContact? contact, DemoAccount account, DemoOpportunity? opp)
        {
            var topic = rng.Pick(DemoCatalog.Topics);
            var who = contact?.FirstName ?? account.Name;
            var scope = opp is null ? topic : opp.Name[(opp.Name.IndexOf('–', StringComparison.Ordinal) + 2)..];
            var options = type switch
            {
                "call" => new[] { $"Call with {who} about {topic}", $"Discovery call – {topic}", "Follow-up call: quote status", "Check-in call after delivery", $"Call: {scope}" },
                "meeting" => new[] { $"Site visit at {account.Name}", $"Technical workshop – {topic}", "Quarterly business review", "Proposal walkthrough", $"Video meeting with {who}" },
                "task" => new[] { $"Email {who}: send updated proposal", $"Email: confirm {topic}", "Email follow-up: technical specs", $"Prepare quote – {scope}", "Update pricing sheet" },
                _ => new[] { $"Email summary from {who}", $"Notes from call: {topic}", "Competitor intel from site visit", $"Customer feedback on {topic}", $"Email thread: {scope}" },
            };
            var text = rng.Pick(options);
            return text.Length > 160 ? text[..160] : text;
        }

        string? Description() => rng.Chance(0.6) ? rng.Pick(Descriptions) : null;

        string PastType() => rng.Weighted([("call", 45), ("meeting", 30), ("task", 25)]);

        DemoActivity PastActivity(string type)
        {
            var (account, contact, opp) = Target(null, false);
            var start = (opp is null ? account.CreatedAt : opp.CreatedAt).AddDays(1);
            var startDate = DateOnly.FromDateTime(start);
            var endDate = opp is { IsOpen: false } ? opp.CloseDate.AddDays(-1) : today.AddDays(-1);
            if (endDate < startDate) { endDate = startDate; }
            var span = Math.Max(1, endDate.DayNumber - startDate.DayNumber);
            // Weighted towards recent history.
            var back = (int)(span * Math.Pow(rng.NextDouble(), 1.6));
            var due = endDate.AddDays(-back);
            if (due > today.AddDays(-1)) { due = today.AddDays(-1); }
            var time = new TimeOnly(rng.Range(8, 16), rng.Chance(0.5) ? 0 : 30);
            var doneAt = due.ToDateTime(time).AddMinutes(rng.Range(15, 150));
            var created = due.AddDays(-rng.Range(1, 14)).ToDateTime(new TimeOnly(rng.Range(8, 16), rng.Range(0, 59)));
            return new DemoActivity(type, Subject(type, contact, account, opp), Description(), account.Key, contact?.Key, opp?.Key, due, time, true, doneAt, account.OwnerKey, created, doneAt);
        }

        DemoActivity Note()
        {
            var (account, contact, opp) = Target(null, false);
            var day = today.AddDays(-(int)(430 * Math.Pow(rng.NextDouble(), 1.6)) - 1);
            var created = day.ToDateTime(new TimeOnly(rng.Range(8, 16), rng.Range(0, 59)));
            if (created < account.CreatedAt) { created = account.CreatedAt.AddDays(1); }
            return new DemoActivity("note", Subject("note", contact, account, opp), rng.Pick(Descriptions), account.Key, contact?.Key, opp?.Key, null, null, true, created, account.OwnerKey, created, created);
        }

        DemoActivity OpenActivity(string? owner, DateOnly due, bool withTime, int hour = 0)
        {
            var type = rng.Weighted([("call", 40), ("meeting", 25), ("task", 35)]);
            var (account, contact, opp) = Target(owner, true);
            TimeOnly? time = withTime ? new TimeOnly(hour > 0 ? hour : rng.Range(8, 16), hour == 14 ? 30 : hour > 0 ? 0 : rng.Chance(0.5) ? 0 : 30) : null;
            var created = today.AddDays(-rng.Range(1, 10)).ToDateTime(new TimeOnly(rng.Range(7, 9), rng.Range(0, 59)));
            return new DemoActivity(type, Subject(type, contact, account, opp), Description(), account.Key, contact?.Key, opp?.Key, due, time, false, null, account.OwnerKey, created, created);
        }

        for (var i = 0; i < 50; i++) { list.Add(Note()); }
        var doneCount = ActivityTotal - 50 - 80 - 10;
        for (var i = 0; i < doneCount; i++) { list.Add(PastActivity(PastType())); }
        AddActivityEdgeCases(list, today);
        // The demo persona (the Nordics manager) gets a full "My day": exactly 2 overdue (1 and 5 days), 3 due today (two with times) and 5 in the
        // next 7 days. The round-robin extras below go to everybody else, so the persona's numbers stay exactly these.
        var persona = DemoMarker.PersonaKey;
        foreach (var d in new[] { -1, -5 }) { list.Add(OpenActivity(persona, today.AddDays(d), true)); }
        foreach (var h in new[] { 10, 14, 0 }) { list.Add(OpenActivity(persona, today, h > 0, h)); }
        foreach (var d in new[] { 1, 2, 4, 5, 7 }) { list.Add(OpenActivity(persona, today.AddDays(d), true)); }
        // Everybody else: 2-3 overdue, 1-2 due today, 3-4 in the next 7 days (round-robin, so each is inside the brief's ranges), and later ones.
        var others = owners.Where(o => o != persona).ToList();
        for (var i = 0; i < 20; i++) { list.Add(OpenActivity(others[i % others.Count], today.AddDays(-rng.Range(1, 21)), rng.Chance(0.7))); }
        for (var i = 0; i < 12; i++) { list.Add(OpenActivity(others[i % others.Count], today, true, 8 + (i % 9))); }
        for (var i = 0; i < 24; i++) { list.Add(OpenActivity(others[i % others.Count], today.AddDays(rng.Range(1, 7)), rng.Chance(0.7))); }
        for (var i = 0; i < 24; i++) { list.Add(OpenActivity(rng.Pick(others), today.AddDays(rng.Range(8, 45)), rng.Chance(0.5))); }
        return list;
    }
}
