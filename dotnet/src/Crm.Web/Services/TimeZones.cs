namespace Crm.Web.Services;

/// <summary>IANA time zone lookup that falls back to UTC for an unknown zone (as the Node app does).</summary>
public static class TimeZones
{
    public static TimeZoneInfo Find(string? id)
    {
        if (!string.IsNullOrEmpty(id))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // fall through
            }
        }
        return TimeZoneInfo.Utc;
    }

    private static readonly Lazy<IReadOnlyList<string>> ListedZones = new(BuildList);

    /// <summary>IANA zone ids offered on the locale page (the Node app used Intl.supportedValuesOf). Windows ids are mapped to IANA ids.</summary>
    public static IReadOnlyList<string> Listed => ListedZones.Value;

    private static IReadOnlyList<string> BuildList()
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal) { "UTC", "Europe/Copenhagen" };
        foreach (var z in TimeZoneInfo.GetSystemTimeZones())
        {
            if (z.Id.Contains('/', StringComparison.Ordinal))
            {
                ids.Add(z.Id);
            }
            else if (TimeZoneInfo.TryConvertWindowsIdToIanaId(z.Id, out var iana) && iana is not null)
            {
                ids.Add(iana);
            }
        }
        return ids.Where(IsValid).ToList();
    }

    public static bool IsValid(string id)
    {
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>Today's calendar date in the zone, yyyy-MM-dd.</summary>
    public static DateOnly TodayIn(string? timezone, TimeProvider time) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), Find(timezone)).DateTime);
}
