using System.Globalization;
using System.Text.RegularExpressions;
using Crm.Web.Services;

namespace Crm.Web.Domain;

public sealed record ValidationResult(Dictionary<string, object?> Values, Dictionary<string, string> Errors)
{
    public bool Ok => Errors.Count == 0;
}

/// <summary>Port of domain/validators.ts: plain functions with identical messages (no DataAnnotations).</summary>
public static partial class EntityValidator
{
    private const string ChooseFromList = "Choose a value from the list.";
    private const string PhoneMsg = "Enter a valid phone number.";

    [GeneratedRegex(@"^[^\s@]+@[^\s@]+\.[^\s@]{2,}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"^[0-9+()\-./\s]{5,40}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Phone();

    [GeneratedRegex(@"^(https?://)?[^\s/$.?#]+(\.[^\s/$.?#]+)+(/\S*)?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Website();

    [GeneratedRegex(@"^([01][0-9]|2[0-3]):[0-5][0-9]\z", RegexOptions.CultureInvariant)]
    private static partial Regex DueTime();

    [GeneratedRegex(@"^[A-Za-z]{2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Country();

    private sealed class Ctx(IReadOnlyDictionary<string, string> body)
    {
        public Dictionary<string, object?> Values { get; } = [];
        public Dictionary<string, string> Errors { get; } = [];

        public string Raw(string k) => (body.TryGetValue(k, out var v) ? v : "").Trim();

        private void Fail(string k, string msg) => Errors.TryAdd(k, msg);

        public void Required(string k, int max, string msgReq, string msgMax)
        {
            var v = Raw(k);
            if (v.Length == 0) { Fail(k, msgReq); }
            else if (v.Length > max) { Fail(k, msgMax); }
            else { Values[k] = v; }
        }

        public void Optional(string k, int max, string msgMax, Regex? pattern = null, string? msgPattern = null)
        {
            var v = Raw(k);
            if (v.Length > max) { Fail(k, msgMax); }
            else if (v.Length > 0 && pattern is not null && !pattern.IsMatch(v)) { Fail(k, msgPattern ?? "Invalid value"); }
            else { Values[k] = v.Length == 0 ? null : v; }
        }

        public void ReqInt(string k, string msg)
        {
            if (EntityDefinitions.TryParseId(Raw(k), out var id)) { Values[k] = id; } else { Fail(k, msg); }
        }

        public void OptInt(string k, string msg)
        {
            var v = Raw(k);
            if (v.Length == 0) { Values[k] = null; }
            else if (EntityDefinitions.TryParseId(v, out var id)) { Values[k] = id; }
            else { Fail(k, msg); }
        }

        public void ReqDate(string k, string msgReq, string msgBad)
        {
            var v = Raw(k);
            if (v.Length == 0) { Fail(k, msgReq); }
            else if (!NumberParser.IsRealDate(v)) { Fail(k, msgBad); }
            else { Values[k] = v; }
        }

        public void OptDate(string k, string msgBad)
        {
            var v = Raw(k);
            if (v.Length == 0) { Values[k] = null; }
            else if (!NumberParser.IsRealDate(v)) { Fail(k, msgBad); }
            else { Values[k] = v; }
        }
    }

    /// <summary>
    /// Field rules plus cross-field and lookup rules. Owner membership is checked by the page. <paramref name="current"/> is the
    /// stored record when editing: an unchanged deactivated picklist value or disabled currency stays valid; NEW selections must be active/enabled.
    /// </summary>
    public static ValidationResult Validate(string entity, IReadOnlyDictionary<string, string> body, Lookups lookups, Row? current = null)
    {
        var c = new Ctx(body);
        var s = lookups.Settings;
        var currencies = s.EnabledCurrencies.ToList();
        if (current?.S("CurrencyCode") is { Length: > 0 } cur)
        {
            currencies.Add(cur);
        }
        c.ReqInt("OwnerId", "Choose an owner.");
        switch (entity)
        {
            case "accounts":
                c.Required("Name", 120, "Enter an account name.", "Name must be 120 characters or fewer.");
                c.Optional("Website", 255, "Web address is too long.", Website(), "Enter a web address like example.com.");
                c.Optional("Phone", 40, PhoneMsg, Phone(), PhoneMsg);
                c.OptInt("IndustryValueId", ChooseFromList);
                c.Optional("Street", 200, "Street must be 200 characters or fewer.");
                c.Optional("PostalCode", 20, "Postcode must be 20 characters or fewer.");
                c.Optional("City", 100, "City must be 100 characters or fewer.");
                c.Optional("CountryCode", 2, "Enter a two-letter country code.", Country(), "Enter a two-letter country code.");
                if (c.Values.TryGetValue("CountryCode", out var cc) && cc is string ccs)
                {
                    c.Values["CountryCode"] = ccs.ToUpperInvariant();
                }
                c.Optional("Notes", 2000, "Notes must be 2000 characters or fewer.");
                break;
            case "contacts":
                c.OptInt("AccountId", "Choose an account from the list.");
                c.Optional("FirstName", 80, "First name must be 80 characters or fewer.");
                c.Required("LastName", 80, "Enter a last name.", "Last name must be 80 characters or fewer.");
                c.Optional("JobTitle", 120, "Job title must be 120 characters or fewer.");
                c.Optional("Email", 254, "Enter an email address like name@example.com.", Email(), "Enter an email address like name@example.com.");
                c.Optional("Phone", 40, PhoneMsg, Phone(), PhoneMsg);
                c.Optional("Mobile", 40, PhoneMsg, Phone(), PhoneMsg);
                break;
            case "opportunities":
                c.Required("Name", 120, "Enter an opportunity name.", "Name must be 120 characters or fewer.");
                c.ReqInt("AccountId", "Choose an account.");
                c.OptInt("PrimaryContactId", "Choose a contact from the list.");
                c.ReqInt("StageId", "Choose a stage.");
                var example = $"Enter an amount, for example {NumberParser.AmountExample(s.LocaleCode)}.";
                var amountRaw = c.Raw("Amount");
                var parsed = NumberParser.ParseAmount(amountRaw, s.LocaleCode);
                if (amountRaw.Length == 0) { c.Errors.TryAdd("Amount", example); }
                else if (parsed.Reason == ParseFailure.Negative) { c.Errors.TryAdd("Amount", "Amount can't be negative."); }
                else if (!parsed.Ok) { c.Errors.TryAdd("Amount", example); }
                else { c.Values["Amount"] = parsed.Value; }
                var code = c.Raw("CurrencyCode").ToUpperInvariant();
                if (currencies.Contains(code)) { c.Values["CurrencyCode"] = code; } else { c.Errors.TryAdd("CurrencyCode", "Choose a currency."); }
                c.ReqDate("CloseDate", "Enter a close date.", "Enter a real date, for example 31.12.2026.");
                c.OptInt("LeadSourceValueId", ChooseFromList);
                c.OptInt("LostReasonValueId", ChooseFromList);
                break;
            case "activities":
                var type = c.Raw("ActivityType");
                if (type is "call" or "meeting" or "task" or "note") { c.Values["ActivityType"] = type; } else { c.Errors.TryAdd("ActivityType", "Choose a type."); }
                c.Required("Subject", 160, "Enter a subject.", "Subject must be 160 characters or fewer.");
                c.Optional("Description", 4000, "Description must be 4000 characters or fewer.");
                c.OptInt("AccountId", "Choose an account from the list.");
                c.OptInt("ContactId", "Choose a contact from the list.");
                c.OptInt("OpportunityId", "Choose an opportunity from the list.");
                c.OptDate("DueDate", "Enter a real date, for example 31.12.2026.");
                c.Optional("DueTime", 5, "Enter a time like 14:30.", DueTime(), "Enter a time like 14:30.");
                c.Values["IsDone"] = c.Raw("IsDone") is "on" or "true" or "1";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(entity));
        }
        if (c.Errors.Count > 0)
        {
            return new ValidationResult([], c.Errors);
        }

        var v = c.Values;
        var errors = c.Errors;
        bool ActiveIn(string field, string code, object? id) =>
            id is null
            || (current is not null && id is int n0 && current.IntOrNull(field) == n0 && lookups.List(code).Any(p => p.Id == n0))
            || lookups.List(code).Any(p => p.Active && id is int n1 && p.Id == n1);
        void Check(string field, string code)
        {
            if (!ActiveIn(field, code, v.GetValueOrDefault(field)))
            {
                errors[field] = ChooseFromList;
            }
        }
        if (entity == "accounts")
        {
            Check("IndustryValueId", "industry");
        }
        if (entity == "opportunities")
        {
            Check("LeadSourceValueId", "lead_source");
            var stage = lookups.Stages.FirstOrDefault(st => v["StageId"] is int sid && st.Id == sid);
            if (stage is null)
            {
                errors["StageId"] = "Choose a stage.";
            }
            if (stage?.Type == "lost")
            {
                if (v["LostReasonValueId"] is null) { errors["LostReasonValueId"] = "Choose why this was lost."; }
                else { Check("LostReasonValueId", "lost_reason"); }
            }
            else
            {
                v["LostReasonValueId"] = null;
            }
        }
        if (entity == "activities")
        {
            if (v["AccountId"] is null && v["ContactId"] is null && v["OpportunityId"] is null)
            {
                errors["AccountId"] = "Link this activity to an account, contact or opportunity.";
            }
            if ((string)v["ActivityType"]! == "note")
            {
                v["DueDate"] = null;
                v["DueTime"] = null;
            }
            else if (v["DueTime"] is not null && v["DueDate"] is null)
            {
                errors["DueDate"] = "Enter a due date for this time.";
            }
        }
        return new ValidationResult(v, errors);
    }

    public static string Invariant(decimal d) => d.ToString(CultureInfo.InvariantCulture);
}
