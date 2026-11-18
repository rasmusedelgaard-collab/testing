using Crm.Web.Data;
using Crm.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Crm.Web.Pages.Entity;

public sealed class DeleteModel(IRlsDb rls, IRlsContextAccessor identity, TimeProvider time) : EntityPageModel(rls, identity, time)
{
    private static readonly IReadOnlyDictionary<string, string> FkMessage = new Dictionary<string, string>
    {
        ["accounts"] = "This account still has linked opportunities or records you can't see. Move or delete them first.",
        ["contacts"] = "This contact is still linked to records you can't see. Move or delete them first.",
        ["opportunities"] = "This opportunity is still linked to records you can't see. Move or delete them first.",
        ["activities"] = "This activity cannot be deleted right now.",
    };

    public int Id { get; private set; }

    public string RecordName { get; private set; } = "";

    public string Notice { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync([FromRoute] string entity, [FromRoute] int id)
    {
        SetEntity(entity);
        var row = await RunAsync(repo => repo.GetAsync(Def.Key, id)) ?? throw new NotFoundException();
        if (!EntityViews.CanDelete(Ctx, row))
        {
            throw new ForbiddenException();
        }
        Id = id;
        RecordName = EntityViews.RecordName(Def.Key, row);
        Notice = Def.Key switch
        {
            "accounts" => "This also removes the link between this account and its contacts and activities. The records themselves are kept.",
            "contacts" => "This also removes the link between this contact and its opportunities and activities.",
            "opportunities" => "This also removes the link between this opportunity and its activities.",
            _ => "",
        };
        ViewData["Title"] = $"Delete {RecordName}";
        return Page();
    }

    public async Task<IActionResult> OnPostAsync([FromRoute] string entity, [FromRoute] int id)
    {
        SetEntity(entity);
        var meta = MetaOf();
        var def = Def;
        string outcome;
        try
        {
            outcome = await RunAsync(async repo =>
            {
                var row = await repo.GetAsync(def.Key, id);
                if (row is null) { return "missing"; }
                if (!EntityViews.CanDelete(Ctx, row)) { return "forbidden"; } // e.g. a team-mate's record in team mode (as Node: 403)
                return await repo.RemoveAsync(def.Key, id, meta) ? "ok" : "missing";
            });
        }
        catch (ConflictException e) when (e is not EditConflictException)
        {
            throw new ConflictException(FkMessage[def.Key], e);
        }
        if (outcome == "forbidden")
        {
            throw new ForbiddenException();
        }
        if (outcome != "ok")
        {
            throw new NotFoundException();
        }
        Flash($"{Cap(def.Singular)} deleted.");
        return SeeOther(def.Path);
    }
}
