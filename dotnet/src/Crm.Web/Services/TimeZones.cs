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
