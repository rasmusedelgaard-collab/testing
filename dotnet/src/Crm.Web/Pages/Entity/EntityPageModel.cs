using System.Globalization;
using Crm.Web.Data;
using Crm.Web.Domain;
using Crm.Web.Infrastructure;
using Crm.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Crm.Web.Pages.Entity;

public sealed record SaveOutcome(string Kind, FormEnv? Env, IReadOnlyDictionary<string, string> Errors, int Id = 0, bool Visible = false, string OwnerName = "");

/// <summary>Shared workflow for the generic entity pages (port of routes/crm.ts helpers). One RunAsync per request.</summary>
public abstract class EntityPageModel(IRlsDb rls, IRlsContextAccessor identity, TimeProvider time) : PageModel
{
    private static readonly (string Field, string Entity, string Message)[] RefFields =
    [
        ("AccountId", "accounts", "Choose an account from the list."),
        ("PrimaryContactId", "contacts", "Choose a contact from the list."),
        ("ContactId", "contacts", "Choose a contact from the list."),
        ("OpportunityId", "opportunities", "Choose an opportunity from the list."),
    ];

    private static readonly IReadOnlyDictionary<string, string[]> RefNeeded = new Dictionary<string, string[]>
    {
        ["accounts"] = [],
        ["contacts"] = ["accounts"],
        ["opportunities"] = ["accounts", "contacts"],
        ["activities"] = ["accounts", "contacts", "opportunities"],
    };

    public const string WriteBlocked = "You can't save this record with that owner. Choose an owner from your team.";

    public EntityDef Def { get; protected set; } = null!;

    public FormPage? Form { get; protected set; }

    protected RlsContext Ctx => identity.Current;

    protected TimeProvider Time => time;

    protected Meta MetaOf() => new(Ctx.UserId, Guid.TryParse(RequestContextMiddleware.CorrelationId(HttpContext), out var g) ? g : Guid.NewGuid());

    protected Task<T> RunAsync<T>(Func<CrmRepo, Task<T>> fn) => rls.RunAsync(tx => fn(new CrmRepo(tx, Ctx)), HttpContext.RequestAborted);

    protected void SetEntity(string entity)
    {
        Def = EntityDefinitions.Find(entity) ?? throw new NotFoundException();
        ViewData["ActiveNav"] = Def.Key;
    }

    protected void Flash(string message, string kind = "success")
    {
        TempData["Flash"] = message;
        TempData["FlashKind"] = kind;
    }

    protected static string Cap(string s) => EntityViews.Capitalise(s);

    protected string Today(Settings s) => TimeZones.TodayIn(s.Timezone, time).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The submitted form as raw strings (first value per key); nothing is model-bound.</summary>
    protected Dictionary<string, string> FormBody() =>
        Request.HasFormContentType
            ? Request.Form.ToDictionary(kv => kv.Key, kv => kv.Value.FirstOrDefault() ?? "", StringComparer.Ordinal)
            : [];

    protected async Task<FormEnv> FormEnvAsync(CrmRepo repo, Lookups lk, Row? current)
    {
        var need = RefNeeded[Def.Key];
        IReadOnlyList<RefOption> accounts = [], contacts = [], opportunities = [];
        foreach (var e in need)
        {
            var opts = await repo.RefOptionsAsync(e);
            if (e == "accounts") { accounts = opts; } else if (e == "contacts") { contacts = opts; } else { opportunities = opts; }
        }
        var owners = await repo.OwnersAsync();
        var currentOwnerId = current?.IntOrNull("OwnerId");
        var currentOwner = currentOwnerId is { } oid ? await repo.OwnerByIdAsync(oid) : null;
        return new FormEnv(lk, owners, accounts, contacts, opportunities, Ctx.Role, currentOwner, currentOwnerId);
    }

    /// <summary>Validates input, owner choice and referenced records (all visible under RLS) inside the same transaction.</summary>
    protected async Task<ValidationResult> PrepareAsync(CrmRepo repo, Lookups lk, FormEnv env, IReadOnlyDictionary<string, string> body, Row? current)
    {
        var result = EntityValidator.Validate(Def.Key, body, lk, current);
        var errors = result.Errors;
        var values = result.Values;
        if (!errors.ContainsKey("OwnerId"))
        {
            _ = EntityDefinitions.TryParseId(body.GetValueOrDefault("OwnerId")?.Trim(), out var ownerId);
            var inList = env.Owners.Any(x => x.Id == ownerId);
            var keepsCurrent = current is not null && current.Int("OwnerId") == ownerId;
            var admin = Ctx.Role == CrmRole.Admin;
            if (!inList && !(keepsCurrent && admin))
            {
                // A non-Admin cannot save a record that stays with an inactive (or out-of-team) owner: fn_RowWriteTarget rejects it.
                errors["OwnerId"] = admin ? "Choose an owner." : keepsCurrent && env.CurrentOwner is { Active: false } ? "Current owner is inactive: choose a new owner." : "Choose an owner from your team.";
            }
        }
        foreach (var (field, entity, message) in RefFields)
        {
            if (values.GetValueOrDefault(field) is int id && !errors.ContainsKey(field) && !await repo.RefExistsAsync(entity, id))
            {
                errors[field] = message;
            }
        }
        return result;
    }

    protected FormPage BuildForm(string title, string action, string cancel, IReadOnlyDictionary<string, string> values, IReadOnlyDictionary<string, string> errors, FormEnv env, string rowVer = "")
    {
        var fields = EntityViews.FormFields(Def.Key, values, errors, env);
        return new FormPage(title, action, cancel, fields, EntityViews.ErrorList(fields, errors), rowVer);
    }

    /// <summary>After a successful create/update: PRG; a hand-off to someone outside the user's visibility says so.</summary>
    protected IActionResult AfterSave(int id, bool visible, string ownerName)
    {
        if (visible)
        {
            Flash($"{Cap(Def.Singular)} saved.");
            return SeeOther($"{Def.Path}/{id}");
        }
        Flash($"Saved and assigned to {ownerName}.");
        return SeeOther(Def.Path);
    }

    protected IActionResult SeeOther(string url)
    {
        Response.StatusCode = StatusCodes.Status303SeeOther;
        Response.Headers.Location = url;
        return new EmptyResult();
    }

    protected static string Str(string? s) => s ?? "";

    protected IActionResult RenderForm(FormPage form)
    {
        Form = form;
        ViewData["Title"] = form.Title;
        Response.StatusCode = form.Errors.Count > 0 ? StatusCodes.Status422UnprocessableEntity : StatusCodes.Status200OK;
        return Page();
    }
}
