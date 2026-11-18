using Crm.Web.Auth;

namespace Crm.Web.Data;

/// <summary>Gives RlsDb the identity of the current request. Derived only from the authenticated principal, never from input.</summary>
public interface IRlsContextAccessor
{
    RlsContext Current { get; }
}

public sealed class HttpRlsContextAccessor(IHttpContextAccessor http) : IRlsContextAccessor
{
    public RlsContext Current
    {
        get
        {
            var user = http.HttpContext?.User ?? throw new InvalidOperationException("No HTTP context");
            return CrmPrincipal.ToRlsContext(user) ?? throw new InvalidOperationException("Not signed in");
        }
    }
}
