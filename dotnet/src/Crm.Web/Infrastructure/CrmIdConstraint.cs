using System.Globalization;

namespace Crm.Web.Infrastructure;

/// <summary>
/// Route constraint "crmid": a canonical positive int (^[1-9][0-9]{0,9}$, at most 2147483647), like the Node parseId.
/// The built-in "int" constraint also accepts "+5", "005" and surrounding blanks; those must be 404.
/// </summary>
public sealed class CrmIdConstraint : IRouteConstraint
{
    public bool Match(HttpContext? httpContext, IRouter? route, string routeKey, RouteValueDictionary values, RouteDirection routeDirection)
    {
        if (!values.TryGetValue(routeKey, out var v))
        {
            return false;
        }
        return IsCanonicalId(Convert.ToString(v, CultureInfo.InvariantCulture));
    }

    public static bool IsCanonicalId(string? s) =>
        s is { Length: >= 1 and <= 10 } && s[0] is >= '1' and <= '9' && s.All(char.IsAsciiDigit) && long.Parse(s, CultureInfo.InvariantCulture) <= int.MaxValue;
}
