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
    public const int OpportunityTotal = 180;
    public const int ActivityTotal = 250;
    public const int ContactTotal = 100;

    private const int MaxClosed = 114; // leaves 66 open: 25 / 18 / 14 / 9 per stage
    private const decimal MinDkk = 55_000m;
    private const decimal MaxDkk = 4_900_000m;

    private enum Bucket { Overdue, RestOfMonth, NextMonth, NextQuarter, Later }

    private sealed record OppSpec(bool Won, bool Lost, int MonthsBack, Bucket Bucket);

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
            string? industry = i == DemoCatalog.Companies.Count - 1 ? null : DemoCatalog.IndustryLabel(c.Segment); // the last account has no industry (empty-state case)
            list.Add(new DemoAccount(
                Key: "A" + (i + 1).ToString("000", CultureInfo.InvariantCulture),
                Name: c.Name,
                Website: $"www.{slug}.{DemoCatalog.Domain}",
                Phone: Phone(c.Country, 1, rng),
                Industry: industry,
                Street: street,
                PostalCode: DemoCatalog.PostalCode(c.Country, rng),
                City: c.City,
                Country: c.Country,
                Notes: $"Segment: {DemoCatalog.SegmentLabel(c.Segment)}. Fictitious demo account.",
                OwnerKey: owner.Key,
                CreatedAt: At(today.AddDays(-rng.Range(600, 720)), rng.Range(8, 15), rng.Range(0, 59))));
        }
        return list;
    }

    /// <summary>Always in the 555 01xx / 555 02xx range, which is reserved for fiction.</summary>
    private static string Phone(string country, int block, DemoRandom rng) =>
        $"{DemoCatalog.DialCode(country)} 555 0{block}{rng.Range(0, 99).ToString("00", CultureInfo.InvariantCulture)}";

    private static List<DemoContact> BuildContacts(IReadOnlyList<DemoAccount> accounts, DateOnly today, DemoRandom rng)
    {
        var extra = ContactTotal - 2 * accounts.Count;
        var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<DemoContact>();
        for (var i = 0; i < accounts.Count; i++)
        {
            var a = accounts[i];
            var count = 2 + (i % 2 == 0 && extra > 0 ? 1 : 0);
            if (count == 3) { extra--; }
            var (first, last) = DemoCatalog.Names[a.Country];
            var domain = a.Website["www.".Length..];
            for (var k = 0; k < count; k++)
            {
                var fn = rng.Pick(first);
                var noFirstName = list.Count == 4; // one contact without a first name (empty-state case)
                var ln = rng.Pick(last);
                var email = $"{DemoCatalog.Slug(fn, "")}.{DemoCatalog.Slug(ln, "")}@{domain}";
                for (var n = 2; !emails.Add(email); n++)
                {
                    email = $"{DemoCatalog.Slug(fn, "")}.{DemoCatalog.Slug(ln, "")}{n.ToString(CultureInfo.InvariantCulture)}@{domain}";
                }
                list.Add(new DemoContact(
                    Key: "C" + (list.Count + 1).ToString("000", CultureInfo.InvariantCulture),
                    AccountKey: a.Key,
                    FirstName: noFirstName ? null : fn,
                    LastName: ln,
                    JobTitle: rng.Pick(DemoCatalog.JobTitles),
                    Email: email,
                    Phone: Phone(a.Country, 1, rng),
                    Mobile: Phone(a.Country, 2, rng),
                    OwnerKey: a.OwnerKey,
                    CreatedAt: a.CreatedAt.AddDays(rng.Range(0, 200)).AddHours(rng.Range(0, 5))));
            }
        }
        return list;
    }

    // ---------- Opportunities ----------

    private static List<DemoOpportunity> BuildOpportunities(DateOnly today, DemoRandom rng, IReadOnlyList<DemoAccount> accounts, IReadOnlyList<DemoContact> contacts, IReadOnlyList<DemoFxRate> fx, bool edge)
    {
        // Closed deals. Won/lost counts follow a trend: the last 90 days win more often than the 90 before (the KPI trend shows "up").
        var closed = new List<OppSpec>();
        for (var back = 0; back < HistoryMonths; back++)
        {
            var recent = back < 3;
            var won = recent ? 3 : 2;
            var lost = recent || back < 6 ? 4 : new[] { 3, 4, 4 }[rng.Next(3)];
            closed.AddRange(Enumerable.Repeat(new OppSpec(true, false, back, Bucket.Overdue), won));
            closed.AddRange(Enumerable.Repeat(new OppSpec(false, true, back, Bucket.Overdue), lost));
        }
        // Top up with a few extra lost deals so the closed total (and with it the 66-deal open funnel) is fixed.
        while (closed.Count < MaxClosed) { closed.Add(new OppSpec(false, true, rng.Next(HistoryMonths), Bucket.Overdue)); }
        closed = closed.Take(MaxClosed).ToList(); // newest first, so a trim drops the oldest history
        var openCount = OpportunityTotal - closed.Count;

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
            var contactKey = rng.Chance(0.9) ? rng.Pick(accountContacts).Key : null;
            var currency = rng.Weighted(DemoCatalog.CurrenciesFor(account.Country));
            var lead = rng.Weighted(DemoCatalog.LeadSources);
            var key = "O" + (ordinal + 1).ToString("000", CultureInfo.InvariantCulture);
            return spec.Won || spec.Lost
                ? Closed(key, name, account, contactKey, currency, lead, spec, today, rng, fx)
                : Open(key, name, account, contactKey, currency, lead, spec.Bucket, openSlot, today, rng, fx);
        }

        // Closed deals are dealt to the owners in turn (oldest first within each round), so every owner has plenty of closed deals in the last 12 months.
        var ownerTurn = 0;
        foreach (var spec in closed.OrderByDescending(c => c.MonthsBack))
        {
            var owner = owners[ownerTurn++ % owners.Count];
            result.Add(Build(rng.Pick(accountsByOwner[owner]), spec, 0, result.Count));
        }
        for (var i = 0; i < openCount; i++)
        {
            result.Add(Build(rng.Pick(accounts), new OppSpec(false, false, 0, mix[i % mix.Length]), slots[i], result.Count));
        }

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

    private static DemoOpportunity Closed(string key, string name, DemoAccount account, string? contactKey, string currency, string lead, OppSpec spec, DateOnly today, DemoRandom rng, IReadOnlyList<DemoFxRate> fx)
    {
        var month = FirstOfMonth(today).AddMonths(-spec.MonthsBack);
        var maxDay = spec.MonthsBack == 0 ? today.Day : DateTime.DaysInMonth(month.Year, month.Month);
        // Won deals are front-loaded in the previous quarter and back-loaded earlier in this one, so month-to-date and quarter-to-date trends can differ.
        var quarterStartMonth = ((today.Month - 1) / 3) * 3 + 1;
        var inCurrentQuarter = month.Year == today.Year && month.Month >= quarterStartMonth;
        var day = rng.Range(1, maxDay);
        if (spec.Won && spec.MonthsBack > 0 && inCurrentQuarter) { day = Math.Max(day, Math.Min(maxDay, 16)); }
        var close = month.AddDays(day - 1);
        var closedAt = At(close, close == today ? 6 : rng.Range(7, 16), rng.Range(0, 59));
        var created = At(close.AddDays(-rng.Range(25, 160)), rng.Range(8, 16), rng.Range(0, 59));
        if (created < account.CreatedAt) { created = account.CreatedAt.AddDays(1); }
        return new DemoOpportunity(key, name, account.Key, contactKey, spec.Won ? DemoOpportunity.SlotWon : DemoOpportunity.SlotLost,
            AmountIn(currency, close, rng, fx), currency, close, lead,
            spec.Lost ? rng.Weighted(DemoCatalog.LostReasons) : null, closedAt, account.OwnerKey, created, closedAt);
    }

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

    private static List<DemoActivity> BuildActivities(DateOnly today, DemoRandom rng, IReadOnlyList<DemoAccount> accounts, IReadOnlyList<DemoContact> contacts, IReadOnlyList<DemoOpportunity> opps)
    {
        var list = new List<DemoActivity>();
        var accountByKey = accounts.ToDictionary(a => a.Key);
        var contactsByAccount = contacts.GroupBy(c => c.AccountKey).ToDictionary(g => g.Key, g => g.ToList());
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
            var contact = opp?.ContactKey is { } ck ? contactByKey[ck] : rng.Chance(0.8) ? rng.Pick(contactsByAccount[account.Key]) : null;
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
        // The demo persona (the Nordics manager) gets a full "My day": 2 overdue (1 and 5 days), 3 due today (two with times), 5 in the next 7 days.
        var persona = DemoMarker.PersonaKey;
        foreach (var d in new[] { -1, -5 }) { list.Add(OpenActivity(persona, today.AddDays(d), true)); }
        foreach (var h in new[] { 10, 14, 0 }) { list.Add(OpenActivity(persona, today, h > 0, h)); }
        foreach (var d in new[] { 1, 2, 4, 5, 7 }) { list.Add(OpenActivity(persona, today.AddDays(d), true)); }
        // Round-robin over the owners so every user has something overdue and something due today on their dashboard.
        for (var i = 0; i < 20; i++) { list.Add(OpenActivity(owners[i % owners.Count], today.AddDays(-rng.Range(1, 21)), rng.Chance(0.7))); }
        for (var i = 0; i < 12; i++) { list.Add(OpenActivity(owners[i % owners.Count], today, true, 8 + (i % 9))); }
        for (var i = 0; i < 24; i++) { list.Add(OpenActivity(rng.Pick(owners), today.AddDays(rng.Range(1, 7)), rng.Chance(0.7))); }
        for (var i = 0; i < 24; i++) { list.Add(OpenActivity(rng.Pick(owners), today.AddDays(rng.Range(8, 45)), rng.Chance(0.5))); }
        return list;
    }
}
