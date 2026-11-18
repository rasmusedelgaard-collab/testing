using Crm.Web.Data;

namespace Crm.Web.Auth;

public static class Roles
{
    private static readonly (string Claim, CrmRole Role)[] AppRoles = [("CRM.Admin", CrmRole.Admin), ("CRM.Manager", CrmRole.Manager), ("CRM.User", CrmRole.User)];

    /// <summary>Highest app role wins. Unknown values are ignored. Null means "no CRM access".</summary>
    public static CrmRole? Map(IEnumerable<string> roles)
    {
        var present = roles.ToHashSet(StringComparer.Ordinal);
        foreach (var (claim, role) in AppRoles)
        {
            if (present.Contains(claim))
            {
                return role;
            }
        }
        return null;
    }
}
