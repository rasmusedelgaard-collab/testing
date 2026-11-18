using System.Globalization;
using Crm.Web.Data;
using Crm.Web.Domain;
using Crm.Web.Services;

namespace Crm.Web.Pages.Entity;

public sealed record DoneForm(string Action, string Context, bool Checked, string ReturnTo);

public sealed record Cell(string Text, string? Href = null, bool Right = false, string? Note = null, DoneForm? DoneForm = null);

public sealed record Column(string Label, string? Sort = null);

public sealed record SelectOption(string Value, string Label, bool Selected = false);

public sealed record FormField(string Name, string Label, string Type, string Value, string? Error = null, bool Required = false, string? Help = null, IReadOnlyList<SelectOption>? Options = null, string? InputMode = null, string? Autocomplete = null, string? Group = null);

public sealed record FilterField(string Name, string Label, IReadOnlyList<SelectOption> Options, string Value);

public sealed record FormEnv(Lookups Lookups, IReadOnlyList<OwnerOption> Owners, IReadOnlyList<RefOption> Accounts, IReadOnlyList<RefOption> Contacts, IReadOnlyList<RefOption> Opportunities, CrmRole Role, OwnerOption? CurrentOwner, int? CurrentOwnerId);

public sealed record FormPage(string Title, string Action, string Cancel, IReadOnlyList<FormField> Fields, IReadOnlyList<(string Field, string Message)> Errors, string RowVer);

/// <summary>Pure view-model builders (port of routes/crmViews.ts): everything the templates need, already formatted.</summary>
public static class EntityViews
{
    public static readonly IReadOnlyDictionary<string, string> TypeLabel = new Dictionary<string, string> { ["call"] = "Call", ["meeting"] = "Meeting", ["task"] = "Task", ["note"] = "Note" };

    private static string Cap(string x) => x.Length == 0 ? x : char.ToUpperInvariant(x[0]) + x[1..];

    public static string Capitalise(string x) => Cap(x);

    public static string OwnerLabel(OwnerOption o, bool admin) => admin && o.TeamName is not null ? $"{o.Name} ({o.TeamName})" : o.Name;

    public static string RecordName(string entity, Row r) => entity == "contacts"
        ? string.Join(' ', new[] { r.S("FirstName"), r.S("LastName") }.Where(s => s.Length > 0))
        : r.S(EntityDefinitions.All[entity].LabelCol is "Subject" or "LastName" ? EntityDefinitions.All[entity].LabelCol : "Name");

    public static Cell RelatedTo(Row r)
    {
        if (r.Int("OpportunityId") != 0) { return new Cell(r.S("OpportunityName") is { Length: > 0 } n ? n : "Opportunity", $"/opportunities/{r.Int("OpportunityId")}"); }
        if (r.Int("ContactId") != 0) { return new Cell(r.S("ContactName") is { Length: > 0 } n ? n : "Contact", $"/contacts/{r.Int("ContactId")}"); }
        if (r.Int("AccountId") != 0) { return new Cell(r.S("AccountName") is { Length: > 0 } n ? n : "Account", $"/accounts/{r.Int("AccountId")}"); }
        return new Cell("(deleted record)");
    }

    public static IReadOnlyList<Column> ColumnsFor(string entity) => entity switch
    {
        "accounts" => [new("Name", "name"), new("Industry", "industry"), new("City", "city"), new("Owner", "owner"), new("Open pipeline")],
        "contacts" => [new("Name", "name"), new("Account", "account"), new("Title", "title"), new("Email", "email"), new("Phone"), new("Owner", "owner")],
        "opportunities" => [new("Name", "name"), new("Account", "account"), new("Stage", "stage"), new("Amount", "amount"), new("Close date", "close"), new("Owner", "owner")],
        _ => [new("Done"), new("Type", "type"), new("Subject", "subject"), new("Related to"), new("Due date", "due"), new("Owner", "owner")],
    };

    public static IReadOnlyList<Cell> CellsFor(string entity, Row r, Formatter f, string today, string returnTo)
    {
        var path = EntityDefinitions.All[entity].Path;
        var link = $"{path}/{r.Int("Id")}";
        switch (entity)
        {
            case "accounts":
                return [new(r.S("Name"), link), new(r.S("IndustryLabel")), new(r.S("City")), new(r.S("OwnerName")), new(r.S("OpenPipeline") is { Length: > 0 } op ? op : "–", Right: true)];
            case "contacts":
                return [new(RecordName(entity, r), link), new(r.S("AccountName"), r.Int("AccountId") != 0 ? $"/accounts/{r.Int("AccountId")}" : null), new(r.S("JobTitle")), new(r.S("Email")), new(r.S("Phone")), new(r.S("OwnerName"))];
            case "opportunities":
                var past = r.S("StageType") == "open" && string.CompareOrdinal(r.S("CloseDate"), today) < 0;
                return [new(r.S("Name"), link), new(r.S("AccountName"), $"/accounts/{r.Int("AccountId")}"), new(r.S("StageName")),
                    new(f.Money(r.Dec("Amount"), r.S("CurrencyCode")), Right: true), new(f.Date(r.S("CloseDate")), Note: past ? "Past close date" : null), new(r.S("OwnerName"))];
            default:
                var done = r.Bool("IsDone");
                return [new(done ? "Done" : "Open", DoneForm: new DoneForm($"{path}/{r.Int("Id")}/{(done ? "undone" : "done")}", done ? $"{r.S("Subject")}, mark as not done" : r.S("Subject"), done, returnTo)),
                    new(TypeLabel.GetValueOrDefault(r.S("ActivityType")) ?? r.S("ActivityType")), new(r.S("Subject"), link), RelatedTo(r),
                    new(f.Date(r.S("DueDate")) + (r.S("DueTime").Length > 0 ? " " + r.S("DueTime") : "")), new(r.S("OwnerName"))];
        }
    }

    public static IReadOnlyList<(string Label, string Text, string? Href)> DetailRows(string entity, Row r, Formatter f)
    {
        static (string, string, string?) D(string l, string t, string? h = null) => (l, t, h);
        switch (entity)
        {
            case "accounts":
                return [D("Industry", r.S("IndustryLabel")), D("Website", r.S("Website")), D("Phone", r.S("Phone")), D("Street", r.S("Street")), D("Postcode", r.S("PostalCode")), D("City", r.S("City")), D("Country", r.S("CountryCode")), D("Notes", r.S("Notes"))];
            case "contacts":
                return [D("Account", r.S("AccountName"), r.Int("AccountId") != 0 ? $"/accounts/{r.Int("AccountId")}" : null), D("Job title", r.S("JobTitle")), D("Email", r.S("Email")), D("Phone", r.S("Phone")), D("Mobile", r.S("Mobile"))];
            case "opportunities":
                var amount = r.Dec("Amount");
                var rows = new List<(string, string, string?)>
                {
                    D("Account", r.S("AccountName"), $"/accounts/{r.Int("AccountId")}"),
                    D("Primary contact", r.S("PrimaryContactName"), r.Int("PrimaryContactId") != 0 ? $"/contacts/{r.Int("PrimaryContactId")}" : null),
                    D("Stage", r.S("StageName")), D("Probability", $"{r.S("Probability")}%"),
                    D("Amount", f.Money(amount, r.S("CurrencyCode"))),
                    D("Weighted amount", f.Money(Math.Round(amount * r.Dec("Probability")) / 100m, r.S("CurrencyCode"))),
                    D("Close date", f.Date(r.S("CloseDate"))), D("Lead source", r.S("LeadSourceLabel")),
                };
                if (r.S("StageType") == "lost") { rows.Add(D("Lost reason", r.S("LostReasonLabel"))); }
                return rows;
            default:
                var rel = RelatedTo(r);
                return [D("Type", TypeLabel.GetValueOrDefault(r.S("ActivityType")) ?? ""), D("Status", r.Bool("IsDone") ? "Done" : "Open"), D("Related to", rel.Text, rel.Href),
                    D("Due", f.Date(r.S("DueDate")) + (r.S("DueTime").Length > 0 ? " " + r.S("DueTime") : "")), D("Description", r.S("Description"))];
        }
    }

    private static readonly IReadOnlyDictionary<string, string> FieldLabel = new Dictionary<string, string> { ["OwnerId"] = "Owner", ["StageId"] = "Stage", ["CurrencyCode"] = "Currency", ["CloseDate"] = "Close date", ["Amount"] = "Amount" };

    /// <summary>One readable sentence per audit row, e.g. "Owner changed from Anna Teammate to Demo User".</summary>
    public static string DescribeHistory(HistoryRow h, Formatter f)
    {
        if (h.Action == "C") { return "Created"; }
        if (h.Action == "D") { return "Deleted"; }
        var label = FieldLabel.GetValueOrDefault(h.Field ?? "") ?? h.Field ?? "Field";
        string Show(string? v) => v is null ? "(empty)" : h.Field == "CloseDate" ? f.Date(v) : v;
        return $"{label} changed from {Show(h.OldValue)} to {Show(h.NewValue)}";
    }

    public static bool CanDelete(RlsContext user, Row r) =>
        user.Role == CrmRole.Admin || r.Int("OwnerId") == user.UserId || (user.Role == CrmRole.Manager && user.TeamId is not null && r.IntOrNull("TeamId") == user.TeamId);

    private static List<SelectOption> Opt(IEnumerable<RefOption> o) => o.Select(x => new SelectOption(x.Id.ToString(CultureInfo.InvariantCulture), x.Label)).ToList();

    private static readonly SelectOption None = new("", "— none —");

    public static IReadOnlyList<FilterField> FilterFields(string entity, ListQuery q, Lookups lk, IReadOnlyList<OwnerOption> owners, bool admin)
    {
        FilterField F(string name, string label, IEnumerable<SelectOption> options) => new(name, label, [new SelectOption("", "All"), .. options], q.Filter(name));
        var ownerF = F("owner", "Owner", owners.Select(o => new SelectOption(o.Id.ToString(CultureInfo.InvariantCulture), OwnerLabel(o, admin))));
        IEnumerable<SelectOption> Picks(string code) => lk.List(code).Select(p => new SelectOption(p.Id.ToString(CultureInfo.InvariantCulture), p.Label));
        string Or(string k, string d) => q.Filters.TryGetValue(k, out var v) ? v : d;
        switch (entity)
        {
            case "accounts": return [ownerF, F("industry", "Industry", Picks("industry"))];
            case "contacts": return [ownerF];
            case "opportunities":
                return [F("stage", "Stage", lk.Stages.Select(st => new SelectOption(st.Id.ToString(CultureInfo.InvariantCulture), st.Name))), ownerF,
                    new("status", "Status", new[] { "open", "won", "lost", "closed", "all" }.Select(v => new SelectOption(v, Cap(v))).ToList(), Or("status", "open"))];
            default:
                return [F("type", "Type", [.. TypeLabel.Select(kv => new SelectOption(kv.Key, kv.Value)), new SelectOption("actionable", "Call, meeting or task")]), ownerF,
                    new("status", "Status", new[] { "open", "done", "all" }.Select(v => new SelectOption(v, Cap(v))).ToList(), Or("status", "open")),
                    new("due", "Due", [new("any", "Any"), new("due", "Due or overdue"), new("overdue", "Overdue"), new("today", "Today"), new("week", "Next 7 days")], Or("due", "any"))];
        }
    }

    /// <summary>Raw form strings for an existing record (what the user would see in the inputs).</summary>
    public static Dictionary<string, string> FormValuesFromRow(string entity, Row r, Formatter f)
    {
        var o = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in EntityDefinitions.All[entity].Columns)
        {
            o[c.Name] = c.Type == ColType.Money && r[c.Name] is not null ? f.AmountInput(r.Dec(c.Name)) : c.Type == ColType.Bool ? (r.Bool(c.Name) ? "on" : "") : r.S(c.Name);
        }
        o["OwnerId"] = r.S("OwnerId");
        return o;
    }

    public static IReadOnlyList<FormField> FormFields(string entity, IReadOnlyDictionary<string, string> values, IReadOnlyDictionary<string, string> errors, FormEnv env)
    {
        var admin = env.Role == CrmRole.Admin;
        string V(string n) => values.GetValueOrDefault(n) ?? "";
        FormField Base(string name, string label, string type, bool required = false, string? help = null, IReadOnlyList<SelectOption>? options = null, string? inputMode = null, string? autocomplete = null, string? group = null, string? value = null) =>
            new(name, label, type, value ?? V(name), errors.GetValueOrDefault(name), required, help, options, inputMode, autocomplete, group);
        string Req(string label) => $"{label} (required)";
        FormField Select(string name, string label, IEnumerable<SelectOption> options, bool required = false, string? group = null)
        {
            var v = V(name);
            return Base(name, label, "select", required, options: new[] { None }.Concat(options).Select(o => o with { Selected = o.Value == v }).ToList(), group: group);
        }
        IEnumerable<SelectOption> Picks(string code, string current) => env.Lookups.List(code).Where(p => p.Active || p.Id.ToString(CultureInfo.InvariantCulture) == current)
            .Select(p => new SelectOption(p.Id.ToString(CultureInfo.InvariantCulture), p.Active ? p.Label : $"{p.Label} (inactive)"));

        var ownerOptions = env.Owners.Select(o => new SelectOption(o.Id.ToString(CultureInfo.InvariantCulture), OwnerLabel(o, admin))).ToList();
        if (env.CurrentOwner is { } co && !env.Owners.Any(o => o.Id == co.Id))
        {
            ownerOptions.Add(new SelectOption(co.Id.ToString(CultureInfo.InvariantCulture), $"{co.Name} ({(co.Active ? "other team" : "inactive")})"));
        }
        var ownerValue = V("OwnerId");
        var owner = Base("OwnerId", Req("Owner"), "select", required: true, options: ownerOptions.Select(o => o with { Selected = o.Value == ownerValue }).ToList(),
            help: admin ? null : env.CurrentOwner is { Active: false } ? "Current owner is inactive: choose a new owner." : "You can choose people in your team.");

        switch (entity)
        {
            case "accounts":
                return [Base("Name", Req("Name"), "text", true), Base("Website", "Website", "text", autocomplete: "url"), Base("Phone", "Phone", "tel", autocomplete: "tel"),
                    Select("IndustryValueId", "Industry", Picks("industry", V("IndustryValueId"))), Base("Street", "Street", "text"), Base("PostalCode", "Postcode", "text"), Base("City", "City", "text"),
                    Base("CountryCode", "Country code (two letters)", "text"), Base("Notes", "Notes", "textarea"), owner];
            case "contacts":
                return [Base("FirstName", "First name", "text"), Base("LastName", Req("Last name"), "text", true), Select("AccountId", "Account", Opt(env.Accounts)),
                    Base("JobTitle", "Job title", "text"), Base("Email", "Email", "email", autocomplete: "email"), Base("Phone", "Phone", "tel"), Base("Mobile", "Mobile", "tel"), owner];
            case "opportunities":
                var stageValue = V("StageId");
                var enabled = env.Lookups.Settings.EnabledCurrencies;
                var cur = V("CurrencyCode");
                var currencies = enabled.Concat(cur.Length > 0 && !enabled.Contains(cur) ? new[] { cur } : Array.Empty<string>()).ToList();
                return [
                    Base("Name", Req("Name"), "text", true),
                    Select("AccountId", Req("Account"), Opt(env.Accounts), required: true),
                    Select("PrimaryContactId", "Primary contact", Opt(env.Contacts)),
                    Base("StageId", Req("Stage"), "select", true, options: env.Lookups.Stages.Select(st => new SelectOption(st.Id.ToString(CultureInfo.InvariantCulture), $"{st.Name} ({st.Probability}%)", st.Id.ToString(CultureInfo.InvariantCulture) == stageValue)).ToList()),
                    Base("Amount", Req("Amount"), "text", true, inputMode: "decimal"),
                    Base("CurrencyCode", Req("Currency"), "select", true, options: currencies.Select(c => new SelectOption(c, enabled.Contains(c) ? c : $"{c} (disabled)", c == cur)).ToList()),
                    Base("CloseDate", Req("Close date"), "date", true),
                    Select("LeadSourceValueId", "Lead source", Picks("lead_source", V("LeadSourceValueId"))),
                    Select("LostReasonValueId", "Lost reason (required if the stage is Closed lost)", Picks("lost_reason", V("LostReasonValueId"))),
                    owner];
            default:
                const string related = "Related to (link at least one)";
                return [
                    Base("ActivityType", Req("Type"), "radio", true, options: TypeLabel.Select(kv => new SelectOption(kv.Key, kv.Value, kv.Key == V("ActivityType"))).ToList()),
                    Base("Subject", Req("Subject"), "text", true),
                    Select("AccountId", "Related account", Opt(env.Accounts), group: related),
                    Select("ContactId", "Related contact", Opt(env.Contacts), group: related),
                    Select("OpportunityId", "Related opportunity", Opt(env.Opportunities), group: related),
                    Base("DueDate", "Due date (not used for notes)", "date"), Base("DueTime", "Due time", "time"),
                    Base("IsDone", "Done", "checkbox"), Base("Description", "Description", "textarea"), owner];
        }
    }

    public static IReadOnlyList<(string Field, string Message)> ErrorList(IReadOnlyList<FormField> fields, IReadOnlyDictionary<string, string> errors)
    {
        var order = fields.Select(x => x.Name).ToList();
        return errors.OrderBy(e => order.IndexOf(e.Key)).Select(e => (e.Key, e.Value)).ToList();
    }

    public static string PageHref(string basePath, ListQuery q, IReadOnlyDictionary<string, string?> over)
    {
        var all = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["q"] = q.Q,
            ["sort"] = q.Sort,
            ["dir"] = q.Dir,
            ["pageSize"] = q.PageSize.ToString(CultureInfo.InvariantCulture),
            ["page"] = q.Page.ToString(CultureInfo.InvariantCulture),
        };
        foreach (var kv in q.Filters) { all[kv.Key] = kv.Value; }
        foreach (var kv in over) { all[kv.Key] = kv.Value; }
        var qs = string.Join('&', all.Where(kv => !string.IsNullOrEmpty(kv.Value)).Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));
        return qs.Length > 0 ? $"{basePath}?{qs}" : basePath;
    }
}
