using Crm.Web.Domain;
using Crm.Web.Services;

namespace Crm.Tests.Unit;

/// <summary>Ports of domain.test.ts (validators, list query, audit) and the FX part of kpi.test.ts.</summary>
public class DomainTests
{
    private static Lookups Lk(string locale = "da-DK") => new(
        new Settings("CRM", locale, "Europe/Copenhagen", "DKK", ["DKK", "EUR"], "private", "#1D4ED8", 1, 1),
        [new Stage(1, "Qual", 1, 10, "open"), new Stage(6, "Lost", 6, 0, "lost")],
        new Dictionary<string, IReadOnlyList<PicklistValue>>
        {
            ["industry"] = [new(100, null, "Tech", true), new(101, null, "Old", false)],
            ["lost_reason"] = [new(300, null, "Price", true)],
            ["lead_source"] = [],
        });

    private static Dictionary<string, string> B(params (string, string)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2);

    private static ValidationResult V(string e, Dictionary<string, string> b, string locale = "da-DK", Row? current = null) => EntityValidator.Validate(e, b, Lk(locale), current);

    [Fact]
    public void Account_rules()
    {
        Assert.Equal("Enter an account name.", V("accounts", B(("OwnerId", "1"))).Errors["Name"]);
        Assert.Equal("Name must be 120 characters or fewer.", V("accounts", B(("Name", new string('x', 121)), ("OwnerId", "1"))).Errors["Name"]);
        Assert.Contains("web address", V("accounts", B(("Name", "a"), ("Website", "nope"), ("OwnerId", "1"))).Errors["Website"], StringComparison.Ordinal);
        Assert.Equal("Enter a valid phone number.", V("accounts", B(("Name", "a"), ("Phone", "abc"), ("OwnerId", "1"))).Errors["Phone"]);
        Assert.True(V("accounts", B(("Name", "a"), ("IndustryValueId", "101"), ("OwnerId", "1"))).Errors.ContainsKey("IndustryValueId"));
        var ok = V("accounts", B(("Name", " Acme "), ("Website", "acme.dk"), ("CountryCode", "dk"), ("IndustryValueId", "100"), ("OwnerId", "2")));
        Assert.Empty(ok.Errors);
        Assert.Equal("Acme", ok.Values["Name"]);
        Assert.Equal("DK", ok.Values["CountryCode"]);
        Assert.Equal(100, ok.Values["IndustryValueId"]);
        Assert.Equal(2, ok.Values["OwnerId"]);
        Assert.Null(ok.Values["City"]);
    }

    [Fact]
    public void Contact_rules()
    {
        Assert.Equal("Enter a last name.", V("contacts", B(("OwnerId", "1"))).Errors["LastName"]);
        Assert.Contains("name@example.com", V("contacts", B(("LastName", "x"), ("Email", "bad"), ("OwnerId", "1"))).Errors["Email"], StringComparison.Ordinal);
    }

    [Fact]
    public void Opportunity_rules()
    {
        var b = B(("Name", "o"), ("AccountId", "1"), ("StageId", "1"), ("Amount", "1.234,50"), ("CurrencyCode", "eur"), ("CloseDate", "2026-12-31"), ("OwnerId", "1"));
        var ok = V("opportunities", b);
        Assert.Equal(1234.5m, ok.Values["Amount"]);
        Assert.Equal("EUR", ok.Values["CurrencyCode"]);
        Assert.Equal(1234.5m, V("opportunities", With(b, ("Amount", "1,234.50")), "en-GB").Values["Amount"]);
        Assert.True(V("opportunities", With(b, ("StageId", "99"))).Errors.ContainsKey("StageId"));
        Assert.Equal("Choose why this was lost.", V("opportunities", With(b, ("StageId", "6"))).Errors["LostReasonValueId"]);
        Assert.Null(V("opportunities", With(b, ("StageId", "1"), ("LostReasonValueId", "300"))).Values["LostReasonValueId"]);
        var e = V("opportunities", With(b, ("Name", ""), ("AccountId", ""))).Errors;
        Assert.Equal("Enter an opportunity name.", e["Name"]);
        Assert.Equal("Choose an account.", e["AccountId"]);
        Assert.Equal("Enter an amount, for example 12,500.00.", V("opportunities", With(b, ("Amount", "6,8")), "en-GB").Errors["Amount"]);
        Assert.Equal("Enter an amount, for example 12.500,00.", V("opportunities", With(b, ("Amount", "12.5"))).Errors["Amount"]);
        Assert.Equal("Amount can't be negative.", V("opportunities", With(b, ("Amount", "-5"))).Errors["Amount"]);
        Assert.Equal("Choose a currency.", V("opportunities", With(b, ("CurrencyCode", "XXX"))).Errors["CurrencyCode"]);
        Assert.Equal("Enter a real date, for example 31.12.2026.", V("opportunities", With(b, ("CloseDate", "2026-02-30"))).Errors["CloseDate"]);
    }

    private static Dictionary<string, string> With(Dictionary<string, string> d, params (string, string)[] extra)
    {
        var c = new Dictionary<string, string>(d);
        foreach (var (k, v) in extra) { c[k] = v; }
        return c;
    }

    [Fact]
    public void Activity_rules()
    {
        var e = V("activities", B(("ActivityType", "x"), ("Subject", ""), ("OwnerId", "1"))).Errors;
        Assert.Equal("Choose a type.", e["ActivityType"]);
        Assert.Equal("Enter a subject.", e["Subject"]);
        var n = V("activities", B(("ActivityType", "note"), ("Subject", "s"), ("AccountId", "1"), ("DueDate", "2026-01-01"), ("OwnerId", "1")));
        Assert.Null(n.Values["DueDate"]);
        Assert.Null(n.Values["DueTime"]);
        Assert.Equal(false, n.Values["IsDone"]);
        Assert.Contains("real date", V("activities", B(("ActivityType", "call"), ("Subject", "s"), ("AccountId", "1"), ("DueDate", "2026-02-30"), ("OwnerId", "1"))).Errors["DueDate"], StringComparison.Ordinal);
        Assert.Equal(true, V("activities", B(("ActivityType", "call"), ("Subject", "s"), ("ContactId", "3"), ("IsDone", "on"), ("OwnerId", "1"))).Values["IsDone"]);
        Assert.Equal("Link this activity to an account, contact or opportunity.", V("activities", B(("ActivityType", "call"), ("Subject", "s"), ("OwnerId", "1"))).Errors["AccountId"]);
        Assert.Equal("Enter a due date for this time.", V("activities", B(("ActivityType", "call"), ("Subject", "s"), ("AccountId", "1"), ("DueTime", "14:30"), ("OwnerId", "1"))).Errors["DueDate"]);
        Assert.Equal("Enter a time like 14:30.", V("activities", B(("ActivityType", "call"), ("Subject", "s"), ("AccountId", "1"), ("DueTime", "25:00"), ("OwnerId", "1"))).Errors["DueTime"]);
    }

    [Fact]
    public void Unicode_digits_and_trailing_newlines_do_not_pass_id_fields()
    {
        Assert.True(V("accounts", B(("Name", "a"), ("OwnerId", "١"))).Errors.ContainsKey("OwnerId"));
        Assert.True(V("accounts", B(("Name", "a"), ("IndustryValueId", "٣"), ("OwnerId", "1"))).Errors.ContainsKey("IndustryValueId"));
        Assert.True(V("accounts", B(("Name", "a"), ("OwnerId", "0"))).Errors.ContainsKey("OwnerId"));
        Assert.True(V("accounts", B(("Name", "a"), ("OwnerId", "2147483648"))).Errors.ContainsKey("OwnerId"));
        Assert.True(V("accounts", B(("Name", "a"), ("Phone", "12345١"), ("OwnerId", "1"))).Errors.ContainsKey("Phone"));
    }

    [Fact]
    public void An_unchanged_inactive_picklist_value_stays_valid_when_editing()
    {
        var current = new Row(new Dictionary<string, object?> { ["IndustryValueId"] = 101, ["OwnerId"] = 1 });
        Assert.Empty(V("accounts", B(("Name", "a"), ("IndustryValueId", "101"), ("OwnerId", "1")), current: current).Errors);
    }

    [Fact]
    public void List_query_applies_defaults_and_whitelists()
    {
        var def = EntityDefinitions.All["opportunities"];
        var q = EntityDefinitions.ParseListQuery(def, _ => null, "2026-11-18");
        Assert.Equal((1, 25, "close", "asc", "open"), (q.Page, q.PageSize, q.Sort, q.Dir, q.Filters["status"]));
        var vals = new Dictionary<string, string> { ["sort"] = "x", ["dir"] = "desc", ["status"] = "bogus", ["stage"] = "3", ["from"] = "2026-01-01", ["to"] = "nope", ["pageSize"] = "50", ["page"] = "2", ["q"] = " hi " };
        var q2 = EntityDefinitions.ParseListQuery(def, k => vals.GetValueOrDefault(k), "t");
        Assert.Equal(("close", "desc", 50, 2, "hi"), (q2.Sort, q2.Dir, q2.PageSize, q2.Page, q2.Q));
        Assert.Equal("open", q2.Filters["status"]);
        Assert.Equal("3", q2.Filters["stage"]);
        Assert.Equal("2026-01-01", q2.Filters["from"]);
        Assert.False(q2.Filters.ContainsKey("to"));
    }

    [Fact]
    public void List_query_sort_keys_match_exactly()
    {
        var def = EntityDefinitions.All["accounts"];
        foreach (var bad in new[] { "Name", "NAME", "name ", "constructor", "__proto__", "e.Name", "name;--" })
        {
            Assert.Equal("name", EntityDefinitions.ParseListQuery(def, k => k == "sort" ? bad : null, "t").Sort);
        }
        Assert.Equal("industry", EntityDefinitions.ParseListQuery(def, k => k == "sort" ? "industry" : null, "t").Sort);
        Assert.Equal(1, EntityDefinitions.ParseListQuery(def, k => k == "page" ? "٣" : null, "t").Page);
        Assert.Equal(25, EntityDefinitions.ParseListQuery(def, k => k == "pageSize" ? "7" : null, "t").PageSize);
    }

    [Fact]
    public void Filter_clauses_only_come_from_the_whitelist()
    {
        Assert.Null(Crm.Web.Data.CrmSql.FilterClause("accounts", "evil", "x"));
        Assert.Equal("e.OwnerId = @f_owner", Crm.Web.Data.CrmSql.FilterClause("accounts", "owner", "3"));
        Assert.Equal("s.StageType IN ('won', 'lost')", Crm.Web.Data.CrmSql.FilterClause("opportunities", "status", "closed"));
        Assert.Null(Crm.Web.Data.CrmSql.FilterClause("opportunities", "status", "all"));
        Assert.Contains("DATEADD(day, 7", Crm.Web.Data.CrmSql.FilterClause("activities", "due", "week"), StringComparison.Ordinal);
    }

    [Fact]
    public void Audit_rows()
    {
        var acc = EntityDefinitions.All["accounts"];
        var c = Audit.Create(acc, 7, "Acme", 3, 1);
        Assert.Equal(("C", "account", 7, "Acme", 3, 1), (c.Action, c.EntityType, c.EntityId, c.NewValue, c.OwnerId, c.TeamId));
        var d = Audit.Delete(EntityDefinitions.All["opportunities"], 9, "Deal", 4, null);
        Assert.Equal(("D", "Deal", (int?)null), (d.Action, d.OldValue, d.TeamId));
        var opp = EntityDefinitions.All["opportunities"];
        var rows = Audit.Update(opp, 1,
            new Dictionary<string, object?> { ["Amount"] = 10.50m, ["StageId"] = 1, ["CurrencyCode"] = "DKK", ["CloseDate"] = "2026-01-01", ["OwnerId"] = 3 },
            new Dictionary<string, object?> { ["Amount"] = 11.00m, ["StageId"] = 1, ["CurrencyCode"] = "EUR", ["CloseDate"] = "2026-01-01", ["OwnerId"] = 3 }, 3, 1);
        Assert.Equal([("Amount", "10.5", "11"), ("CurrencyCode", "DKK", "EUR")], rows.Select(r => (r.FieldName!, r.OldValue!, r.NewValue!)));
        Assert.Empty(Audit.Update(acc, 1, new Dictionary<string, object?> { ["OwnerId"] = 1 }, new Dictionary<string, object?> { ["OwnerId"] = 1 }, 1, null));
        Assert.Equal(4000, Audit.Text(new string('x', 5000))!.Length);
        Assert.Equal("1", Audit.Text(true));
        Assert.Null(Audit.Text(null));
    }

    // ---- FX (ADR-0005)
    private static readonly Converter Conv = new("DKK",
    [
        new("EUR", "2026-09", 7.4m), new("EUR", "2026-11", 7.5m), new("USD", "2026-10", 6.9m), new("DKK", "2026-11", 99m),
    ]);

    [Fact]
    public void Base_currency_is_exactly_one_and_never_flagged()
    {
        Assert.Equal(new Conversion(true, 123.45m, false), Conv.Convert(123.45m, "DKK", "2026-11"));
        Assert.Equal(new Conversion(true, 0.3m, false), Conv.Convert(0.3m, "DKK", "1999-01"));
    }

    [Fact]
    public void Exact_month_rate_then_latest_earlier_with_flag_never_later_never_guessed()
    {
        Assert.Equal(new Conversion(true, 750m, false), Conv.Convert(100m, "EUR", "2026-11"));
        Assert.Equal(new Conversion(true, 740m, true), Conv.Convert(100m, "EUR", "2026-10"));
        Assert.Equal(new Conversion(true, 750m, true), Conv.Convert(100m, "EUR", "2027-03"));
        Assert.False(Conv.Convert(100m, "EUR", "2026-08").Ok);
        Assert.False(Conv.Convert(100m, "USD", "2026-09").Ok);
        Assert.False(Conv.Convert(100m, "SEK", "2026-11").Ok);
        Assert.False(new Converter("DKK", [new("EUR", "2026-11", 0m)]).Convert(1m, "EUR", "2026-11").Ok);
    }

    [Fact]
    public void Convert_rows_sums_excludes_and_reports_fallbacks_with_exact_notes()
    {
        var rows = new[] { ("DKK", 1, 100m, "2026-11"), ("EUR", 1, 10m, "2026-10"), ("SEK", 3, 50m, "2026-11"), ("SEK", 2, 5m, "2026-11") };
        var c = Conv.ConvertRows(rows, r => (r.Item1, r.Item2, r.Item3), r => r.Item4);
        Assert.Equal(174m, c.Total);
        Assert.Equal([("SEK", 5)], c.Excluded);
        Assert.Equal(["EUR"], c.FallbackCurrencies);
        Assert.Equal("5 opportunities in SEK not included: no FX rate", Converter.ExclusionNote(c));
        Assert.Equal("Uses latest rate for 1 currency", Converter.FallbackNote(c));
        Assert.Equal("1 opportunity in SEK not included: no FX rate", Converter.ExclusionNote(new Converted(0, [("SEK", 1)], [])));
        Assert.Equal(2.5m, Conv.ConvertRows([("EUR", 1, 0.333m, "2026-11")], r => (r.Item1, r.Item2, r.Item3), r => r.Item4).Total);
        Assert.Equal("", Converter.ExclusionNote(Conv.ConvertRows(Array.Empty<(string, int, decimal, string)>(), r => (r.Item1, r.Item2, r.Item3), r => r.Item4)));
    }
}
