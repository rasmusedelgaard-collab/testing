using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Core.Infrastructure;

namespace Crm.Web.Infrastructure;

/// <summary>Razor Pages answer a failed antiforgery check with 400; this app answers 403 with the error page, as the Node app did.</summary>
public sealed class AntiforgeryFailureFilter : IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is IAntiforgeryValidationFailedResult)
        {
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
        }
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }
}
