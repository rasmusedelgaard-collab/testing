using System.Globalization;
using Crm.Web.Data;
using Crm.Web.Domain;
using Crm.Web.Services;

namespace Crm.Web.Pages.Entity;

/// <summary>One line of a record timeline (UI redesign 4.10, B-7): an activity or an audit change.</summary>
public sealed record TimelineItem(
    string Icon,
    string Title,
    string? Href,
    bool BoldTitle,
    string When,
    string Hint,
    bool Overdue,
    string By,
    string? RelatedLabel,
    string? RelatedHref,
    string Description,
    bool Done,
    string? ActivityHref,
    DoneForm? Action);

public sealed record TimelineGroup(string Title, IReadOnlyList<TimelineItem> Items);

/// <summary>
/// Merges the already-loaded related activities and the audit history of one record into one timeline: Overdue, Upcoming,
/// then past activity and changes by month, newest first. Pure: no queries.
/// </summary>
public static class TimelineBuilder
{
    public const int DescriptionPreview = 180;

    public static int Count(IReadOnlyList<TimelineGroup> groups) => groups.Sum(g => g.Items.Count);

    private static DateOnly? ParseDate(string s) =>
        s.Length >= 10 && DateOnly.TryParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>"in 3 days", "tomorrow", "today", "2 days overdue" (ui-spec 6).</summary>
    public static string RelativeDays(string date, string today, bool open)
    {
        if (ParseDate(date) is not { } d || ParseDate(today) is not { } t)
        {
            return "";
        }
        var diff = d.DayNumber - t.DayNumber;
        return diff switch
        {
            0 => "today",
            1 => "tomorrow",
            > 1 => $"in {diff.ToString(CultureInfo.InvariantCulture)} days",
            -1 => open ? "1 day overdue" : "yesterday",
            _ => open ? $"{(-diff).ToString(CultureInfo.InvariantCulture)} days overdue" : $"{(-diff).ToString(CultureInfo.InvariantCulture)} days ago",
        };
    }

    public static string MonthTitle(string yearMonth) =>
        new DateOnly(int.Parse(yearMonth[..4], CultureInfo.InvariantCulture), int.Parse(yearMonth[5..7], CultureInfo.InvariantCulture), 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>One activity as a timeline line. selfHref is the page's own record: a related link to it is left out.</summary>
    public static TimelineItem ActivityItem(Row r, Formatter f, string today, string returnTo, string? selfHref = null)
    {
        var type = r.S("ActivityType");
        var done = r.Bool("IsDone");
        var due = r.S("DueDate");
        var time = r.S("DueTime");
        var isNote = type == "note";
        var rel = EntityViews.RelatedTo(r);
        var id = r.Int("Id").ToString(CultureInfo.InvariantCulture);
        var isOverdue = !done && !isNote && due.Length > 0 && string.CompareOrdinal(due, today) < 0;
        var hint = due.Length > 0 && !isNote ? RelativeDays(due, today, !done) : "";
        var when = due.Length > 0 ? f.Date(due) + (time.Length > 0 ? " " + time : "") : f.Date(r.S("CreatedAt"));
        var hideRelated = rel.Href is null || rel.Href == selfHref;
        return new TimelineItem(
            type is "call" or "meeting" or "task" or "note" ? type : "task",
            $"{EntityViews.TypeLabel.GetValueOrDefault(type) ?? type}: {r.S("Subject")}",
            $"/activities/{id}", true, when, hint, isOverdue, r.S("OwnerName"),
            hideRelated ? null : rel.Text, hideRelated ? null : rel.Href,
            r.S("Description"), done, $"/activities/{id}",
            isNote ? null : new DoneForm($"/activities/{id}/{(done ? "undone" : "done")}", r.S("Subject"), done, returnTo));
    }

    public static IReadOnlyList<TimelineGroup> Build(IEnumerable<Row> activities, IEnumerable<HistoryRow> history, Formatter f, string today, string returnTo, string selfEntity, int selfId)
    {
        var overdue = new List<(string Key, TimelineItem Item)>();
        var upcoming = new List<(string Key, TimelineItem Item)>();
        var past = new List<(string Key, TimelineItem Item)>();

        var selfHref = $"/{selfEntity}/{selfId.ToString(CultureInfo.InvariantCulture)}";
        foreach (var r in activities)
        {
            var item = ActivityItem(r, f, today, returnTo, selfHref);
            var type = r.S("ActivityType");
            var done = r.Bool("IsDone");
            var due = r.S("DueDate");
            var time = r.S("DueTime");
            var isNote = type == "note";
            // Past work sorts by its due date, or by creation for notes and undated items.
            var sortDate = due.Length > 0 && !isNote ? due + " " + (time.Length > 0 ? time : "00:00") : r.S("CreatedAt");
            if (item.Overdue)
            {
                overdue.Add((sortDate, item));
            }
            else if (!done && !isNote)
            {
                upcoming.Add((due.Length > 0 ? sortDate : "9999", item));
            }
            else
            {
                past.Add((sortDate, item));
            }
        }

        foreach (var h in history)
        {
            var text = EntityViews.DescribeHistory(h, f);
            var item = new TimelineItem("change", text, null, h.Field == "OwnerId", f.DateTime(h.At), "", false, h.By, null, null, "", false, null, null);
            past.Add((h.At, item));
        }

        var groups = new List<TimelineGroup>();
        if (overdue.Count > 0)
        {
            groups.Add(new TimelineGroup("Overdue", overdue.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Item).ToList()));
        }
        if (upcoming.Count > 0)
        {
            groups.Add(new TimelineGroup("Upcoming", upcoming.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Item).ToList()));
        }
        foreach (var month in past.GroupBy(x => x.Key.Length >= 7 ? x.Key[..7] : "0000-00").OrderByDescending(g => g.Key, StringComparer.Ordinal))
        {
            var title = month.Key == "0000-00" ? "Earlier" : MonthTitle(month.Key);
            groups.Add(new TimelineGroup(title, month.OrderByDescending(x => x.Key, StringComparer.Ordinal).Select(x => x.Item).ToList()));
        }
        return groups;
    }
}
