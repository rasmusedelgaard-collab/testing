using System.Reflection;
using System.Security.Claims;
using Crm.Web.Auth;
using Crm.Web.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Tests.Http;

/// <summary>
/// Renders a full Razor Page (with _ViewStart, the layouts and every partial) from an in-memory page model, without a database.
/// Page models are built without their services and filled through their private setters, so only the views run.
/// </summary>
public static class PageRenderHarness
{
    public static ClaimsPrincipal User(CrmRole role = CrmRole.Manager, string name = "Mette Holm") =>
        CrmPrincipal.Create(new SessionUser(7, "00000000-0000-4000-8000-00000000a002", name, "mette@example.test", role, 3, 1));

    public static T Model<T>(params (string Property, object? Value)[] values) where T : PageModel
    {
        var ctor = typeof(T).GetConstructors().Single();
        var model = (T)ctor.Invoke(new object?[ctor.GetParameters().Length]);
        foreach (var (name, value) in values)
        {
            var prop = typeof(T).GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic) ?? typeof(T).BaseType!.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException(name);
            prop.SetValue(model, value);
        }
        return model;
    }

    public static async Task<AngleSharp.Html.Dom.IHtmlDocument> RenderAsync<T>(CrmFactory factory, string pageName, T model, ClaimsPrincipal? user = null, IDictionary<string, object?>? viewData = null, Action<IDictionary<string, object?>>? tempData = null, string? dumpSuffix = null)
        where T : PageModel
    {
        using var scope = factory.Services.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        if (user is not null) { http.User = user; }
        http.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(), "test")); // endpoint routing: lets asp-page / asp-antiforgery links resolve
        var descriptor = new Microsoft.AspNetCore.Mvc.RazorPages.CompiledPageActionDescriptor
        {
            RelativePath = $"/Pages{pageName}.cshtml",
            ViewEnginePath = pageName,
            RouteValues = new Dictionary<string, string?> { ["page"] = pageName },
        };
        var actionContext = new ActionContext(http, new RouteData(), descriptor);
        var engine = scope.ServiceProvider.GetRequiredService<IRazorViewEngine>();
        var found = engine.GetView(null, $"/Pages{pageName}.cshtml", isMainPage: true);
        Assert.True(found.Success, string.Join(", ", found.SearchedLocations ?? []));
        var vd = new ViewDataDictionary<T>(new EmptyModelMetadataProvider(), new ModelStateDictionary()) { Model = model };
        foreach (var (k, v) in viewData ?? new Dictionary<string, object?>()) { vd[k] = v; }
        var temp = new TempDataDictionary(http, scope.ServiceProvider.GetRequiredService<ITempDataProvider>());
        tempData?.Invoke(temp);
        await using var writer = new StringWriter();
        var pageContext = new PageContext(actionContext) { ViewData = vd };
        model.PageContext = pageContext;
        ((Page)((RazorView)found.View).RazorPage).PageContext = pageContext;
        await found.View.RenderAsync(new ViewContext(actionContext, found.View, vd, temp, writer, new HtmlHelperOptions()));
        var html = writer.ToString();
        if (Environment.GetEnvironmentVariable("CRM_RENDER_DUMP") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, pageName.Trim('/').Replace('/', '_') + "-" + typeof(T).Name + "-" + (dumpSuffix ?? "") + ".html"), html);
        }
        return new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
    }
}
