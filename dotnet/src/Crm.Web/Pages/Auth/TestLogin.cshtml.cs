using Crm.Web.Auth;
using Crm.Web.Config;
using Crm.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Crm.Web.Pages.Auth;

/// <summary>Test-access-code mode only (ADR-0006). The page is not routable in entra mode.</summary>
[AllowAnonymous]
public sealed class TestLoginModel(CrmConfig config, TestAccessGate gate, TestAccessCookie cookie, TestUserRepo users, ILogger<TestLoginModel> log) : PageModel
{
    public bool ShowPicker { get; private set; }
    public bool Locked { get; private set; }
    public string? Error { get; private set; }
    public string ReturnTo { get; private set; } = "/";
    public IReadOnlyList<TestUserRow> Users { get; private set; } = [];

    public static string ClientKey(HttpContext c)
    {
        var ip = c.Connection.RemoteIpAddress?.ToString();
        // Never one shared "unknown" bucket: key on the connection so one anonymous client cannot lock everyone out.
        return ip ?? "conn:" + c.Connection.Id;
    }

    public async Task<IActionResult> OnGetAsync(string? returnTo, CancellationToken ct)
    {
        if (!config.IsTestAccess)
        {
            return NotFound();
        }
        ReturnTo = Crm.Web.Auth.ReturnTo.Safe(returnTo);
        if (cookie.IsVerified(HttpContext))
        {
            await LoadPickerAsync(ct);
            return Page();
        }
        Locked = gate.IsLocked(ClientKey(HttpContext));
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? code, string? returnTo, CancellationToken ct)
    {
        if (!config.IsTestAccess)
        {
            return NotFound();
        }
        ReturnTo = Crm.Web.Auth.ReturnTo.Safe(returnTo);
        switch (gate.Submit(ClientKey(HttpContext), code ?? ""))
        {
            case CodeAttempt.Locked:
                Locked = true;
                Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return Page();
            case CodeAttempt.WrongAndNowLocked:
                Locked = true;
                Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return Page();
            case CodeAttempt.Wrong:
                Error = "That access code is not correct.";
                Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Page();
            default:
                cookie.Issue(HttpContext);
                await LoadPickerAsync(ct);
                log.LogDebug("test access code accepted");
                return Page();
        }
    }

    private async Task LoadPickerAsync(CancellationToken ct)
    {
        ShowPicker = true;
        Users = await users.ListAsync(ct);
    }
}
