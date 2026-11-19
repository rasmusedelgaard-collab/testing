using System.Globalization;
using Crm.Web.Data;
using Crm.Web.Domain;
using Crm.Web.Infrastructure;
using Crm.Web.Pages.Entity;
using Microsoft.AspNetCore.Mvc;

namespace Crm.Web.Pages.Pipeline;

/// <summary>
/// POST /opportunities/{id}/stage (UI redesign B-5): move an opportunity to another stage without opening the form.
/// Same rules as the edit form: RLS decides what can be seen and written, the hidden rowVer gives optimistic concurrency (409 page),
/// the update writes the audit rows, antiforgery is validated by Razor Pages. A move to a Won or Lost stage is not a plain move:
/// it runs through the shared close rules (confirm page first, lost reason required, close date set to today when in the future),
/// the same code as "Mark as won/lost" on the record.
/// </summary>
public sealed class StageModel(IRlsDb rls, IRlsContextAccessor identity, TimeProvider time) : EntityPageModel(rls, identity, time)
{
    public ConfirmPage? Confirm { get; private set; }

    /// <summary>GET is not a route of this endpoint (as for the other action URLs).</summary>
    public IActionResult OnGet([FromRoute] int id) => throw new NotFoundException();

    public async Task<IActionResult> OnPostAsync([FromRoute] int id)
    {
        SetEntity("opportunities");
        ViewData["ActiveNav"] = "pipeline";
        var def = Def;
        var body = FormBody();
        var rowVer = body.GetValueOrDefault("rowVer") ?? "";
        var lostRaw = body.GetValueOrDefault("lostReason") ?? "";
        var confirmed = body.GetValueOrDefault("confirm") == "1";
        var back = BackTarget(body.GetValueOrDefault("returnTo"));
        var meta = MetaOf();
        if (!EntityDefinitions.TryParseId(body.GetValueOrDefault("stageId"), out var stageId))
        {
            Flash("Choose a stage from the list.", "error");
            return SeeOther(back);
        }
        var res = await RunAsync(async repo =>
        {
            try
            {
                return await MoveAsync(repo);
            }
            catch (ForbiddenException)
            {
                // The database refused the resulting row (33504, row-level security): say so on the board instead of showing a 403 page.
                return new StageResult("forbidden");
            }

            async Task<StageResult> MoveAsync(CrmRepo repo)
            {
                var row = await repo.GetAsync(def.Key, id);
                if (row is null) { return new StageResult("notfound"); }
                if (!CrmRepo.IsRowVer(rowVer)) { return new StageResult("conflict"); }
                var lk = await repo.LookupsAsync();
                var target = lk.Stages.FirstOrDefault(s => s.Id == stageId);
                if (target is null) { return new StageResult("badstage"); }
                var name = EntityViews.RecordName(def.Key, row);
                if (target.Type is "won" or "lost")
                {
                    var close = await CloseCoreAsync(repo, target.Type, id, rowVer, lostRaw, confirmed, meta, null);
                    return new StageResult(close.Kind, name, target.Name, close, target.Type);
                }
                if (row.Int("StageId") == target.Id) { return new StageResult("same", name, target.Name); }
                var values = def.Columns.ToDictionary(c => c.Name, c => ColumnValue(row, c), StringComparer.Ordinal);
                values["StageId"] = target.Id;
                values["LostReasonValueId"] = null; // moving back to an open stage clears a lost reason
                values["OwnerId"] = row.Int("OwnerId");
                var r = await repo.UpdateAsync(def.Key, id, values, rowVer, meta);
                return new StageResult(r == UpdateResult.Ok ? "ok" : r == UpdateResult.Conflict ? "conflict" : "notfound", name, target.Name);
            }
        });
        switch (res.Kind)
        {
            case "notfound": throw new NotFoundException();
            case "conflict": throw new EditConflictException($"{def.Path}/{id}");
            case "badstage":
                Flash("Choose a stage from the list.", "error");
                return SeeOther(back);
            case "forbidden":
                Flash(WriteBlocked, "error");
                return SeeOther(back);
            case "same":
                Flash($"{res.Name} is already in {res.Stage}.", "info");
                break;
            case "lostError":
                // The board has no reason field: the record has (Close this opportunity), with the same inline error.
                Flash("Choose why this was lost on the opportunity.", "error");
                return SeeOther($"{def.Path}/{id}");
            case "confirm":
                Confirm = res.Close!.Extra;
                ViewData["Title"] = Confirm!.Title;
                return Page();
            case "error":
                Flash(res.Close!.Message, "error");
                return SeeOther($"{def.Path}/{id}");
            default:
                if (res.Close is { } c)
                {
                    // Closed through the shared rules: same message and landing page as "Mark as won/lost" on the record.
                    Flash($"Opportunity marked as {res.Outcome}.{(c.Moved ? " Close date set to today." : "")}");
                    return SeeOther($"{def.Path}/{id}");
                }
                Flash($"Moved {res.Name} to {res.Stage}.");
                break;
        }
        TempData["MovedId"] = id.ToString(CultureInfo.InvariantCulture);
        return SeeOther($"{back}#opp-{id.ToString(CultureInfo.InvariantCulture)}");
    }

    private static string BackTarget(string? returnTo)
    {
        var safe = Crm.Web.Auth.ReturnTo.Safe(returnTo, "/pipeline");
        return safe == "/pipeline" || safe.StartsWith("/pipeline?", StringComparison.Ordinal) ? safe : "/pipeline";
    }

    private sealed record StageResult(string Kind, string Name = "", string Stage = "", CloseResult? Close = null, string Outcome = "");
}
