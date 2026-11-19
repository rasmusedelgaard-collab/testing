using System.Globalization;
using Crm.Web.Data;
using Crm.Web.Domain;
using Crm.Web.Services;

namespace Crm.Web.Pages.Entity;

public sealed record DoneForm(string Action, string Context, bool Checked, string ReturnTo);

/// <summary>
/// One table cell. Kind picks the rendering: text, owner (avatar + name), stage (chip with pips; N of Of, Aux = stage type), segment (Aux = code),
/// email (mailto link), type (activity type icon; Aux = type code), done (the mark-done icon button).
/// Role is the place of the cell in the phone card (ui-redesign 4.3): "name" (line 1), "k" (line 2, two key fields), "m" (line 3, chips and owner), "hide" (not shown on phones).
/// </summary>
public sealed record Cell(string Text, string? Href = null, bool Right = false, string? Note = null, DoneForm? DoneForm = null, string Kind = "text", string? Aux = null, int N = 0, int Of = 0, bool NoteDanger = false, string? Role = null);

public sealed record TdModel(Cell Cell, string Label);

public sealed record Column(string Label, string? Sort = null);

public sealed record SelectOption(string Value, string Label, bool Selected = false);

public sealed record FormField(string Name, string Label, string Type, string Value, string? Error = null, bool Required = false, string? Help = null, IReadOnlyList<SelectOption>? Options = null, string? InputMode = null, string? Autocomplete = null, string? Group = null);

public sealed record FilterField(string Name, string Label, IReadOnlyList<SelectOption> Options, string Value);

public sealed record FormEnv(Lookups Lookups, IReadOnlyList<OwnerOption> Owners, IReadOnlyList<RefOption> Accounts, IReadOnlyList<RefOption> Contacts, IReadOnlyList<RefOption> Opportunities, CrmRole Role, OwnerOption? CurrentOwner, int? CurrentOwnerId);

/// <summary>A visual group of a form (h2, or a real fieldset with a legend). Each row is one field or a pair that shares a line.</summary>
public sealed record FormSection(string Title, bool Fieldset, IReadOnlyList<IReadOnlyList<FormField>> Rows);

public sealed record FormPage(string Title, string Action, string Cancel, IReadOnlyList<FormField> Fields, IReadOnlyList<(string Field, string Message)> Errors, string RowVer, string Entity = "")
{
    public IReadOnlyList<FormSection> Sections => EntityViews.Sections(Entity, Fields);
}

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

    /// <summary>Position (1-based) of a stage among the open stages in Settings order, and the number of open stages. 0 for won and lost.</summary>
    public static (int Index, int Count) OpenPosition(IReadOnlyList<Stage>? stages, int stageId)
    {
        var open = (stages ?? []).Where(s => s.Type == "open").OrderBy(s => s.SortOrder).ToList();
        return (open.FindIndex(s => s.Id == stageId) + 1, open.Count);
    }

    public static IReadOnlyList<Cell> CellsFor(string entity, Row r, Formatter f, string today, string returnTo, IReadOnlyList<Stage>? stages = null)
    {
        var path = EntityDefinitions.All[entity].Path;
        var link = $"{path}/{r.Int("Id")}";
        Cell Owner() => new(r.S("OwnerName"), Kind: "owner", Role: "m");
        switch (entity)
        {
            case "accounts":
                return [new(r.S("Name"), link, Role: "name"), Ui.SegmentOf(r.S("IndustryCode")) is not null ? new(r.S("IndustryLabel"), Kind: "segment", Aux: r.S("IndustryCode"), Role: "m") : new(r.S("IndustryLabel"), Role: "m"), new(r.S("City"), Role: "k"), Owner(), new(r.S("OpenPipeline") is { Length: > 0 } op ? op : "–", Right: true, Role: "k")];
            case "contacts":
                return [new(RecordName(entity, r), link, Role: "name"), new(r.S("AccountName"), r.Int("AccountId") != 0 ? $"/accounts/{r.Int("AccountId")}" : null, Role: "k"), new(r.S("JobTitle"), Role: "k"),
                    r.S("Email").Length > 0 ? new(r.S("Email"), $"mailto:{r.S("Email")}", Kind: "email", Role: "hide") : new("", Role: "hide"), new(r.S("Phone"), Role: "hide"), Owner()];
            case "opportunities":
                var past = r.S("StageType") == "open" && string.CompareOrdinal(r.S("CloseDate"), today) < 0;
                var (idx, cnt) = OpenPosition(stages, r.Int("StageId"));
                return [new(r.S("Name"), link, Role: "name"), new(r.S("AccountName"), $"/accounts/{r.Int("AccountId")}", Role: "k"), new(r.S("StageName"), Kind: "stage", Aux: r.S("StageType"), N: idx, Of: cnt, Role: "m"),
                    new(f.Money(r.Dec("Amount"), r.S("CurrencyCode")), Right: true, Role: "k"), new(f.Date(r.S("CloseDate")), Note: past ? "Past close date" : null, Role: "m"), Owner()];
            default:
                var done = r.Bool("IsDone");
                var overdue = !done && r.S("ActivityType") != "note" && r.S("DueDate").Length > 0 && string.CompareOrdinal(r.S("DueDate"), today) < 0;
                return [new(done ? "Done" : "Open", DoneForm: new DoneForm($"{path}/{r.Int("Id")}/{(done ? "undone" : "done")}", r.S("Subject"), done, returnTo), Kind: "done", Role: "m"),
                    new(TypeLabel.GetValueOrDefault(r.S("ActivityType")) ?? r.S("ActivityType"), Kind: "type", Aux: r.S("ActivityType"), Role: "m"), new(r.S("Subject"), link, Role: "name"), RelatedTo(r) with { Role = "k" },
                    new(f.Date(r.S("DueDate")) + (r.S("DueTime").Length > 0 ? " " + r.S("DueTime") : ""), Note: overdue ? "Overdue" : null, NoteDanger: overdue, Role: "k"), Owner()];
        }
    }

    /// <summary>The Details panel (label, text, link). Money, stage and dates of an opportunity are in the facts strip instead.</summary>
    public static IReadOnlyList<(string Label, string Text, string? Href)> DetailRows(string entity, Row r, Formatter f)
    {
        static (string, string, string?) D(string l, string t, string? h = null) => (l, t, h);
        switch (entity)
        {
            case "accounts":
                var site = r.S("Website");
                return [D("Industry", r.S("IndustryLabel")), D("Website", site, site.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || site.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? site : null), D("Phone", r.S("Phone")), D("Street", r.S("Street")), D("Postcode", r.S("PostalCode")), D("City", r.S("City")), D("Country", r.S("CountryCode")), D("Notes", r.S("Notes"))];
            case "contacts":
                return [D("Account", r.S("AccountName"), r.Int("AccountId") != 0 ? $"/accounts/{r.Int("AccountId")}" : null), D("Job title", r.S("JobTitle")), D("Email", r.S("Email"), r.S("Email").Length > 0 ? $"mailto:{r.S("Email")}" : null), D("Phone", r.S("Phone"), r.S("Phone").Length > 0 ? $"tel:{r.S("Phone")}" : null), D("Mobile", r.S("Mobile"), r.S("Mobile").Length > 0 ? $"tel:{r.S("Mobile")}" : null)];
            case "opportunities":
                var rows = new List<(string, string, string?)>
                {
                    D("Account", r.S("AccountName"), $"/accounts/{r.Int("AccountId")}"),
                    D("Primary contact", r.S("PrimaryContactName"), r.Int("PrimaryContactId") != 0 ? $"/contacts/{r.Int("PrimaryContactId")}" : null),
                    D("Lead source", r.S("LeadSourceLabel")), D("Currency", r.S("CurrencyCode").Trim()),
                };
                if (r.S("StageType") == "lost") { rows.Add(D("Lost reason", r.S("LostReasonLabel"))); }
                return rows;
            default:
                var rel = RelatedTo(r);
                return [D("Type", TypeLabel.GetValueOrDefault(r.S("ActivityType")) ?? ""), D("Status", r.Bool("IsDone") ? "Done" : "Open"), D("Related to", rel.Text, rel.Href),
                    D("Due", f.Date(r.S("DueDate")) + (r.S("DueTime").Length > 0 ? " " + r.S("DueTime") : ""))];
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

    private static readonly IReadOnlyDictionary<string, (string Title, bool Fieldset, string[][] Rows)[]> Layouts = new Dictionary<string, (string, bool, string[][])[]>
    {
        ["accounts"] = [("Company", false, [["Name"], ["Website"], ["Phone"], ["IndustryValueId"]]), ("Address", false, [["Street"], ["PostalCode", "City"], ["CountryCode"]]), ("Notes and ownership", false, [["Notes"], ["OwnerId"]])],
        ["contacts"] = [("Person", false, [["FirstName", "LastName"], ["JobTitle"]]), ("Contact details", false, [["Email"], ["Phone"], ["Mobile"]]), ("Company and ownership", false, [["AccountId"], ["OwnerId"]])],
        ["opportunities"] = [("Basics", false, [["Name"], ["AccountId"], ["PrimaryContactId"]]), ("Value and timing", false, [["StageId"], ["Amount", "CurrencyCode"], ["CloseDate"]]), ("Source and ownership", false, [["LeadSourceValueId"], ["LostReasonValueId"], ["OwnerId"]])],
        ["activities"] = [("Activity", false, [["ActivityType"], ["Subject"]]), ("Related to (link at least one)", true, [["AccountId"], ["ContactId"], ["OpportunityId"]]), ("Timing", false, [["DueDate", "DueTime"], ["IsDone"]]), ("Details", false, [["Description"], ["OwnerId"]])],
    };

    /// <summary>Groups the flat field list into the sections of ui-redesign 5.5. Unknown fields (should not happen) land in a final unnamed group.</summary>
    public static IReadOnlyList<FormSection> Sections(string entity, IReadOnlyList<FormField> fields)
    {
        if (!Layouts.TryGetValue(entity, out var layout))
        {
            return [new FormSection("", false, fields.Select(x => (IReadOnlyList<FormField>)[x]).ToList())];
        }
        var byName = fields.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<FormSection>();
        foreach (var (title, fieldset, rows) in layout)
        {
            var built = rows.Select(r => (IReadOnlyList<FormField>)r.Where(byName.ContainsKey).Select(n => { used.Add(n); return byName[n]; }).ToList()).Where(r => r.Count > 0).ToList();
            if (built.Count > 0) { result.Add(new FormSection(title, fieldset, built)); }
        }
        var rest = fields.Where(x => !used.Contains(x.Name)).Select(x => (IReadOnlyList<FormField>)[x]).ToList();
        if (rest.Count > 0) { result.Add(new FormSection("", false, rest)); }
        return result;
    }

    public static IReadOnlyList<(string Field, string Message)> ErrorList(IReadOnlyList<FormField> fields, IReadOnlyDictionary<string, string> errors)
    {
        var order = fields.Select(x => x.Name).ToList();
        return errors.OrderBy(e => order.IndexOf(e.Key)).Select(e => (e.Key, e.Value)).ToList();
    }

    /// <summary>Chips for the filters that differ from their default, each linking to the same list without that filter (and back on page 1).</summary>
    public static IReadOnlyList<(string Label, string Href)> ActiveFilters(EntityDef def, ListQuery q, IReadOnlyList<FilterField> fields)
    {
        var chips = new List<(string, string)>();
        if (q.Q.Length > 0)
        {
            chips.Add(($"Search: {q.Q}", PageHref(def.Path, q, new Dictionary<string, string?> { ["q"] = "", ["page"] = "1" })));
        }
        foreach (var f in fields)
        {
            var spec = def.Filters.FirstOrDefault(x => x.Key == f.Name);
            if (f.Value.Length == 0 || f.Value == spec?.Default || (f.Name == "due" && f.Value == "any"))
            {
                continue;
            }
            var shown = f.Options.FirstOrDefault(o => o.Value == f.Value)?.Label ?? f.Value;
            chips.Add(($"{f.Label}: {shown}", PageHref(def.Path, q, new Dictionary<string, string?> { [f.Name] = "", ["page"] = "1" })));
        }
        return chips;
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
