using Microsoft.Data.SqlClient;

namespace Crm.Web.Data;

/// <summary>Classifies errors that mean "the database is (re)starting or briefly unavailable" (port of transient.ts).</summary>
public static class Transient
{
    private static readonly HashSet<int> Numbers = [40613, 40197, 40501, 40540, 49918, 49919, 49920, 4221, 10928, 10929];
    private static readonly HashSet<int> ConnectionPhase = [-2, 20, 64, 233, 10053, 10054, 10060];

    public static bool IsTransientNumber(int number) => Numbers.Contains(number) || ConnectionPhase.Contains(number);

    public static bool IsTransient(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is SqlException sql)
            {
                foreach (SqlError err in sql.Errors)
                {
                    if (IsTransientNumber(err.Number))
                    {
                        return true;
                    }
                }
                if (IsTransientNumber(sql.Number))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>First SQL error number found, or null.</summary>
    public static int? SqlNumber(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is SqlException sql)
            {
                return sql.Errors.Count > 0 ? sql.Errors[0].Number : sql.Number;
            }
        }
        return null;
    }

    public static bool HasNumber(Exception? ex, int number)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is SqlException sql && (sql.Number == number || sql.Errors.Cast<SqlError>().Any(x => x.Number == number)))
            {
                return true;
            }
        }
        return false;
    }
}
