namespace Crm.Web.Data;

public enum CrmRole { Admin, Manager, User }

public sealed record RlsContext(int UserId, CrmRole Role, int? TeamId)
{
    public void Validate()
    {
        if (UserId <= 0)
        {
            throw new ArgumentException("UserId must be positive", nameof(UserId));
        }
        if (!Enum.IsDefined(Role))
        {
            throw new ArgumentException("Invalid role", nameof(Role));
        }
        if (TeamId is <= 0)
        {
            throw new ArgumentException("TeamId must be positive or null", nameof(TeamId));
        }
    }
}
