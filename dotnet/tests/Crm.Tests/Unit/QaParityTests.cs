using Crm.Web.Data;
using Crm.Web.Domain;
using Crm.Web.Pages.Entity;
using Crm.Web.Services;

namespace Crm.Tests.Unit;

/// <summary>
/// QA parity tests for rules the Node suite covered over HTTP/SQL (qaIncrement2 takeover-then-delete, qaFinalKpi window and FX month rules)
/// that the .NET suite only exercised indirectly. Pure functions, so they also run without SQL Server.
/// </summary>
public class QaParityTests
{
    private static Row Rec(int owner, int? team) => new(new Dictionary<string, object?> { ["OwnerId"] = owner, ["TeamId"] = team });

    [Theory]
    // role, userId, userTeam, recordOwner, recordTeam, canDelete
    [InlineData(CrmRole.Admin, 1, null, 9, 7, true)]       // Admin: everything
    [InlineData(CrmRole.Manager, 2, 5, 9, 5, true)]        // Manager: own team
    [InlineData(CrmRole.Manager, 2, 5, 9, 6, false)]       // Manager: other team
    [InlineData(CrmRole.Manager, 2, null, 9, null, false)] // Manager without a team never matches a team-less record
    [InlineData(CrmRole.Manager, 2, 5, 2, 6, true)]        // Manager: own record in any team
    [InlineData(CrmRole.User, 3, 5, 3, 5, true)]           // User: own record
    [InlineData(CrmRole.User, 3, 5, 4, 5, false)]          // User: team mate's record (team mode = edit, not delete)
    [InlineData(CrmRole.User, 3, null, 4, null, false)]    // User without team: someone else's team-less record
    public void Delete_is_for_admin_manager_of_the_team_or_the_owner(CrmRole role, int uid, int? team, int owner, int? recTeam, bool expected) =>
        Assert.Equal(expected, EntityViews.CanDelete(new RlsContext(uid, role, team), Rec(owner, recTeam)));

    [Fact]
    public void Takeover_then_delete_a_user_who_takes_a_team_mates_record_may_delete_it()
    {
        var me = new RlsContext(3, CrmRole.User, 5);
        Assert.False(EntityViews.CanDelete(me, Rec(4, 5)));
        Assert.True(EntityViews.CanDelete(me, Rec(3, 5))); // after edit with OwnerId = me
    }

    [Fact]
    public void Win_rate_window_is_90_days_inclusive_of_today()
    {
        var w = Kpi.WinRateRange("2026-11-18");
        Assert.Equal("2026-08-21", w.From); // 89 days ago is in, 90 days ago (2026-08-20) is out
        Assert.Equal("2026-11-18", w.To);
        Assert.True(string.CompareOrdinal("2026-08-20", w.From) < 0);
    }

    [Fact]
    public void Won_deals_use_their_own_close_month_rate_never_a_later_one_and_open_deals_the_current_month()
    {
        var conv = new Converter("DKK", [new("EUR", "2026-09", 7.40m), new("EUR", "2026-11", 7.60m)]);
        var stages = new List<Stage> { new(1, "Lead", 1, 50, "open") };
        AggRow Won(string month) => new("won", null, null, null, null, month, null, "EUR", 1, 100m);
        // October has no rate: falls back to September (earlier), not to November (later).
        var tiles = Kpi.ComputeTiles("2026-11-18", 1, stages, conv, [new("open", 1, null, null, null, null, null, "EUR", 1, 100m)], [Won("2026-10"), Won("2026-11")], []);
        Assert.Equal(740m + 760m, tiles.Single(t => t.Key == "won-quarter").Value);
        Assert.Equal(760m, tiles.Single(t => t.Key == "won-month").Value);
        Assert.Equal(760m, tiles.Single(t => t.Key == "open").Value);   // open pipeline at the current month
        Assert.Equal(380m, tiles.Single(t => t.Key == "weighted").Value);
        Assert.Contains("Uses latest rate for 1 currency", tiles.Single(t => t.Key == "won-quarter").Notes);
    }

    [Fact]
    public void A_missing_rate_is_excluded_with_the_exact_note_never_converted_one_to_one()
    {
        var conv = new Converter("DKK", [new("EUR", "2026-11", 7.5m)]);
        var c = conv.ConvertRows([("SEK", 3, 1000m), ("EUR", 1, 10m), ("DKK", 1, 5m)], r => (r.Item1, r.Item2, r.Item3), _ => "2026-11");
        Assert.Equal(80m, c.Total);
        Assert.Equal("3 opportunities in SEK not included: no FX rate", Converter.ExclusionNote(c));
    }
}
