using Crm.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Crm.Web.Pages;

public enum ErrorKind { Generic, Waking, Conflict }

/// <summary>Port of errorPages.ts + error.ejs/waking.ejs/conflict.ejs: one handler maps exceptions and status codes.</summary>
[AllowAnonymous]
[IgnoreAntiforgeryToken]
public sealed class ErrorModel(ILogger<ErrorModel> log) : PageModel
{
    public ErrorKind Kind { get; private set; }
    public string PageTitle { get; private set; } = "Error";
    public string Heading { get; private set; } = "";
    public string Message { get; private set; } = "";
    public string Reference { get; private set; } = "";
    public string RecordPath { get; private set; } = "/";
    public bool IsPost { get; private set; }

    public void OnGet([FromRoute] int? code) => Render(code);

    public void OnPost([FromRoute] int? code) => Render(code);

    private void Render(int? code)
    {
        Reference = RequestContextMiddleware.CorrelationId(HttpContext);
        var original = HttpContext.Features.Get<IExceptionHandlerFeature>()?.Error;
        IsPost = !HttpMethods.IsGet(HttpContext.Request.Method);
        var status = code ?? 500;
        if (original is AppException { InnerException: { } inner })
        {
            // The mapped exception hides the driver error; keep the SQL error number and message (no row values) for diagnosis.
            log.LogWarning("request failed with {Status}: {Type} {Message}", ((AppException)original).Status, inner.GetType().Name, inner.Message);
        }

        if (original is BadHttpRequestException { StatusCode: 413 })
        {
            Set(413, "Too large", "This form is too large.", "Shorten your text and try again. Nothing was saved.");
            return;
        }
        switch (original)
        {
            case DbWakingException:
                Kind = ErrorKind.Waking;
                PageTitle = "Waking up the database";
                Response.StatusCode = 503;
                Response.Headers.RetryAfter = "5";
                return;
            case EditConflictException ec:
                Kind = ErrorKind.Conflict;
                PageTitle = "Edit conflict";
                RecordPath = Crm.Web.Auth.ReturnTo.Safe(ec.RecordPath);
                Response.StatusCode = 409;
                return;
            case AppException app:
                status = app.Status;
                if (status is 403 or 404)
                {
                    break;
                }
                Set(status, "Problem", status == 409 ? "That can't be done right now." : "Check your input.", app.Message);
                return;
            case not null:
                status = 500;
                break;
        }

        switch (status)
        {
            case 403:
                Set(403, "Access denied", "You don't have access to this page.", "If you think you should, ask your administrator.");
                break;
            case 404:
                Set(404, "Not found", "We can't find that page or record.", "It may have been deleted, or you may not have access.");
                break;
            case 413:
                Set(413, "Too large", "This form is too large.", "Shorten your text and try again. Nothing was saved.");
                break;
            case 429:
                Set(429, "Too many requests", "Too many requests.", "Wait a minute and try again.");
                break;
            default:
                Set(status is >= 400 and < 600 ? status : 500, "Error", "Something went wrong on our side.", "Try again in a moment.");
                break;
        }
    }

    private void Set(int status, string title, string heading, string message)
    {
        Response.StatusCode = status;
        PageTitle = title;
        Heading = heading;
        Message = message;
    }
}
