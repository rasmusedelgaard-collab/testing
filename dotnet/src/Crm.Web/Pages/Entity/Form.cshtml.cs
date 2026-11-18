using Crm.Web.Data;
using Crm.Web.Domain;
using Crm.Web.Infrastructure;
using Crm.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Crm.Web.Pages.Entity;

/// <summary>New (route /{entity}/new) and edit (route /{entity}/{id}/edit, added in Program.cs) share this page.</summary>
public sealed class FormModel(IRlsDb rls, IRlsContextAccessor identity, TimeProvider time) : EntityPageModel(rls, identity, time)
{
    public async Task<IActionResult> OnGetAsync(string entity, int? id)
    {
        SetEntity(entity);
        var def = Def;
        var query = Request.Query;
        // RunAsync: exclusive branches (the new-record branch returns before the edit branch runs)
        if (id is null)
        {
            var (lk, env) = await RunAsync(async repo =>
            {
                var l = await repo.LookupsAsync();
                return (l, await FormEnvAsync(repo, l, null));
            });
            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["OwnerId"] = Ctx.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["CurrencyCode"] = lk.Settings.BaseCurrency,
                ["ActivityType"] = "task",
            };
            if (def.Key == "opportunities")
            {
                values["StageId"] = lk.Stages.FirstOrDefault(s => s.Type == "open")?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            }
            foreach (var (q, field) in new[] { ("accountId", "AccountId"), ("contactId", "ContactId"), ("opportunityId", "OpportunityId") })
            {
                var v = query.TryGetValue(q, out var qv) ? qv.ToString() : "";
                if (EntityDefinitions.TryParseId(v, out _) && def.Columns.Any(c => c.Name == field))
                {
                    values[field] = v;
                }
            }
            return RenderForm(BuildForm($"New {def.Singular}", def.Path, def.Path, values, new Dictionary<string, string>(), env));
        }
        var edit = await RunAsync(async repo =>
        {
            var row = await repo.GetAsync(def.Key, id.Value);
            if (row is null)
            {
                return null;
            }
            var l = await repo.LookupsAsync();
            return new { Row = row, Lk = l, Env = await FormEnvAsync(repo, l, row) };
        }) ?? throw new NotFoundException();
        var f = new Formatter(edit.Lk.Settings.LocaleCode, edit.Lk.Settings.Timezone);
        var name = EntityViews.RecordName(def.Key, edit.Row);
        return RenderForm(BuildForm($"Edit {name}", $"{def.Path}/{id}", $"{def.Path}/{id}", EntityViews.FormValuesFromRow(def.Key, edit.Row, f), new Dictionary<string, string>(), edit.Env, edit.Row.S("RowVer")));
    }
}
