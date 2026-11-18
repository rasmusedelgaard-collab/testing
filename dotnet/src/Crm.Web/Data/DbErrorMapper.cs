using Crm.Web.Infrastructure;

namespace Crm.Web.Data;

/// <summary>Translates driver errors into application errors (overview 8, step 7; dotnet.md 6.6).</summary>
public static class DbErrorMapper
{
    public const int RlsBlock = 33504;
    public const int StaleContext = 51000;

    public static Exception Map(Exception ex)
    {
        if (ex is AppException)
        {
            return ex;
        }
        if (Transient.HasNumber(ex, RlsBlock))
        {
            return new ForbiddenException("You don't have access to this page.", ex);
        }
        if (Transient.HasNumber(ex, 547))
        {
            return new ConflictException("This record is still linked to other records, or links to a record that doesn't exist.", ex);
        }
        if (Transient.HasNumber(ex, 2627) || Transient.HasNumber(ex, 2601))
        {
            return new UniqueViolationException("A record with the same unique value already exists.", ex);
        }
        if (Transient.IsTransient(ex))
        {
            return new DbWakingException(ex);
        }
        return ex;
    }
}
