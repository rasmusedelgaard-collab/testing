using Crm.Web.Domain;

namespace Crm.Web.Infrastructure;

/// <summary>Route constraint "crmentity": accounts | contacts | opportunities | activities (exact, case-sensitive).</summary>
public sealed class CrmEntityConstraint : IRouteConstraint
{
    public bool Match(HttpContext? httpContext, IRouter? route, string routeKey, RouteValueDictionary values, RouteDirection routeDirection) =>
        values.TryGetValue(routeKey, out var v) && v is string s && EntityDefinitions.All.ContainsKey(s);
}
