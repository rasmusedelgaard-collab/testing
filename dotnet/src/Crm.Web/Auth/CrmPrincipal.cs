using System.Globalization;
using System.Security.Claims;
using Crm.Web.Data;

namespace Crm.Web.Auth;

public static class CrmClaims
{
    public const string UserId = "crm:uid";
    public const string TeamId = "crm:tid";
    public const string AuthAt = "crm:authAt";
    public const string Name = "name";
    public const string Email = "email";
    public const string ActiveCheckedAtItem = "crm:activeCheckedAt";
}

/// <summary>What the session row stores (no tokens). The principal is rebuilt from it.</summary>
public sealed record SessionUser(int UserId, string Oid, string Name, string? Email, CrmRole Role, int? TeamId, long AuthAtMs);

public static class CrmPrincipal
{
    public const string AuthenticationType = "crm";

    public static ClaimsPrincipal Create(SessionUser u)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, u.Oid.ToLowerInvariant()),
            new(CrmClaims.UserId, u.UserId.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Role, u.Role.ToString()),
            new(CrmClaims.Name, u.Name),
            new(CrmClaims.AuthAt, u.AuthAtMs.ToString(CultureInfo.InvariantCulture)),
        };
        if (u.TeamId is { } t)
        {
            claims.Add(new Claim(CrmClaims.TeamId, t.ToString(CultureInfo.InvariantCulture)));
        }
        if (!string.IsNullOrEmpty(u.Email))
        {
            claims.Add(new Claim(CrmClaims.Email, u.Email));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationType, CrmClaims.Name, ClaimTypes.Role));
    }

    public static SessionUser? ToSessionUser(ClaimsPrincipal p)
    {
        if (p.Identity?.IsAuthenticated != true
            || !int.TryParse(p.FindFirstValue(CrmClaims.UserId), NumberStyles.None, CultureInfo.InvariantCulture, out var uid)
            || !Enum.TryParse<CrmRole>(p.FindFirstValue(ClaimTypes.Role), out var role)
            || !long.TryParse(p.FindFirstValue(CrmClaims.AuthAt), NumberStyles.None, CultureInfo.InvariantCulture, out var authAt))
        {
            return null;
        }
        int? team = int.TryParse(p.FindFirstValue(CrmClaims.TeamId), NumberStyles.None, CultureInfo.InvariantCulture, out var t) ? t : null;
        return new SessionUser(uid, p.FindFirstValue(ClaimTypes.NameIdentifier) ?? "", p.FindFirstValue(CrmClaims.Name) ?? "", p.FindFirstValue(CrmClaims.Email), role, team, authAt);
    }

    public static RlsContext? ToRlsContext(ClaimsPrincipal p)
    {
        var u = ToSessionUser(p);
        return u is null ? null : new RlsContext(u.UserId, u.Role, u.TeamId);
    }
}
