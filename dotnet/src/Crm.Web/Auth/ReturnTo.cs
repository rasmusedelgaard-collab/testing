namespace Crm.Web.Auth;

public static class ReturnTo
{
    /// <summary>Only relative paths starting with a single "/" are accepted (open-redirect guard). Port of safeReturnTo.</summary>
    public static string Safe(string? value, string fallback = "/")
    {
        if (string.IsNullOrEmpty(value) || value.Length > 2000)
        {
            return fallback;
        }
        if (!value.StartsWith('/') || value.StartsWith("//", StringComparison.Ordinal) || value.Contains('\\', StringComparison.Ordinal))
        {
            return fallback;
        }
        return value.Any(c => c < 32) ? fallback : value;
    }
}
