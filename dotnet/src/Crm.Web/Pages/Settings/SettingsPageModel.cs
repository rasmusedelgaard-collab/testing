using Crm.Web.Data;
using Crm.Web.Domain;
using Crm.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Crm.Web.Pages.Settings;

public sealed record ConfirmVm(string Title, string Message, string Action, IReadOnlyList<(string Name, string Value)> Fields, string Label, bool Danger, string Cancel);

/// <summary>Admin-only pages (AuthorizeFolder in Program.cs; fn_AdminWrite is the database-side backstop). One RunAsync per request.</summary>
public abstract class SettingsPageModel(IRlsDb rls, IRlsContextAccessor identity, BrandingCache branding) : PageModel
{
    public string SettingsNav { get; protected set; } = "";

    public Dictionary<string, string> Errors { get; protected set; } = [];

    public ConfirmVm? Confirm { get; protected set; }

    protected RlsContext Ctx => identity.Current;

    protected BrandingCache Branding => branding;

    protected Meta MetaOf() => new(Ctx.UserId, Guid.TryParse(RequestContextMiddleware.CorrelationId(HttpContext), out var g) ? g : Guid.NewGuid());

    protected Task<T> RunAsync<T>(Func<AdminRepo, CrmRepo, Task<T>> fn) => rls.RunAsync(tx => fn(new AdminRepo(tx), new CrmRepo(tx, Ctx)), HttpContext.RequestAborted);

    protected void Setup(string nav, string title)
    {
        SettingsNav = nav;
        ViewData["Title"] = title;
        ViewData["ActiveNav"] = "settings";
        ViewData["SettingsNav"] = nav;
    }

    protected void Flash(string message)
    {
        TempData["Flash"] = message;
        TempData["FlashKind"] = "success";
    }

    protected IActionResult SeeOther(string url)
    {
        Response.StatusCode = StatusCodes.Status303SeeOther;
        Response.Headers.Location = url;
        return new EmptyResult();
    }

    protected IActionResult Invalid()
    {
        Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return Page();
    }

    protected IActionResult ShowConfirm(string title, string message, string action, IReadOnlyList<(string, string)> fields, string label, bool danger, string cancel)
    {
        Confirm = new ConfirmVm(title, message, action, fields, label, danger, cancel);
        ViewData["Title"] = title;
        return Page();
    }

    public virtual IReadOnlyList<(string Field, string Message)> Summary => Errors.Select(e => (e.Key, e.Value)).ToList();
}
