using System.Globalization;
using System.Net;
using Crm.Web.Data;
using Microsoft.Data.SqlClient;

namespace Crm.Tests.Http;

/// <summary>Ports of the Node access-control tests (qaIncrement2 team-mode takeover, hidden-record 404s, mass assignment), over real SQL and RLS.</summary>
[Collection("sql")]
public sealed class RlsHttpTests(SqlServerFixture fx)
{
    private const string Admin = "00000000-0000-4000-8000-00000000a001";
    private const string UserA1 = "00000000-0000-4000-8000-00000000a003";
    private const string UserA2 = "00000000-0000-4000-8000-00000000a004";
    private const string ManagerB = "00000000-0000-4000-8000-00000000b001";
    private const string NoTeam = "00000000-0000-4000-8000-00000000c001";
    private const string DummyRowVer = "AAAAAAAAAAA=";

    private static string I(int n) => n.ToString(CultureInfo.InvariantCulture);

    private static string Unique(string p) => p + "-" + Guid.NewGuid().ToString("N")[..8];

    private async Task<(CrmFactory F, HttpClient C)> AsAsync(string oid)
    {
        var f = new CrmFactory(fx.AppDb);
        var c = HttpHelpers.NewClient(f);
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, await HttpHelpers.SignInAsync(c, oid));
        return (f, c);
    }

    private async Task<SqlConnection> OwnerAsync()
    {
        var c = new SqlConnection(SqlConnectionFactory.Build(fx.OwnerDb, "owner-rls", 1, false, 30));
        await c.OpenAsync();
        return c;
    }

    private async Task<int> UserIdAsync(string oid)
    {
        await using var c = await OwnerAsync();
        await using var cmd = new SqlCommand("SELECT UserId FROM crm.Users WHERE EntraOid = @o", c);
        cmd.Parameters.AddWithValue("@o", Guid.Parse(oid));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient c, string formPath, string postPath, params (string, string)[] fields) =>
        await c.PostAsync(postPath, HttpHelpers.Form([("_csrf", await HttpHelpers.TokenAsync(c, formPath)), .. fields]));

    private static int IdFrom(HttpResponseMessage r) => int.Parse(r.Headers.Location!.OriginalString.Split('/')[2], CultureInfo.InvariantCulture);

    private async Task<int> CreateAccountAsync(HttpClient c, string oid, params (string, string)[] extra)
    {
        var r = await PostAsync(c, "/accounts/new", "/accounts", [("Name", Unique("Acc")), ("OwnerId", I(await UserIdAsync(oid))), .. extra]);
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, r);
        return IdFrom(r);
    }

    private static async Task SetVisibilityAsync(HttpClient admin, string mode)
    {
        var r = await PostAsync(admin, "/settings/visibility", "/settings/visibility", ("mode", mode), ("confirm", "1"));
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, r);
    }

    // ---------------- team mode ----------------

    [SqlFact]
    public async Task Team_mode_a_user_edits_a_team_mates_record_cannot_delete_it_takes_it_over_then_deletes_it_and_history_shows_both()
    {
        var (fa, admin) = await AsAsync(Admin);
        using var g1 = fa;
        var (f1, a1) = await AsAsync(UserA1);
        using var g2 = f1;
        var (f2, a2) = await AsAsync(UserA2);
        using var g3 = f2;
        var (f3, mgrB) = await AsAsync(ManagerB);
        using var g4 = f3;
        var (f4, none) = await AsAsync(NoTeam);
        using var g5 = f4;
        var a1Id = await UserIdAsync(UserA1);
        var a2Id = await UserIdAsync(UserA2);
        var mine = await CreateAccountAsync(a1, UserA1); // created in private mode
        var otherTeam = await CreateAccountAsync(mgrB, ManagerB);
        var noTeamRec = await CreateAccountAsync(none, NoTeam);
        // private mode first: a team-mate cannot see it
        await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, await a2.GetAsync($"/accounts/{mine}"));
        await SetVisibilityAsync(admin, "team");
        try
        {
            await HttpHelpers.ExpectAsync(HttpStatusCode.OK, await a2.GetAsync($"/accounts/{mine}"));
            // cannot delete a team-mate's record: 403 on the confirmation page and on the POST
            await HttpHelpers.ExpectAsync(HttpStatusCode.Forbidden, await a2.GetAsync($"/accounts/{mine}/delete"));
            await HttpHelpers.ExpectAsync(HttpStatusCode.Forbidden, await PostAsync(a2, "/accounts/new", $"/accounts/{mine}/delete"));
            await HttpHelpers.ExpectAsync(HttpStatusCode.OK, await a2.GetAsync($"/accounts/{mine}"));

            // can edit it (owner unchanged)
            async Task<HttpResponseMessage> EditAsync(string name, int owner)
            {
                var edit = await HttpHelpers.ParseAsync(await a2.GetAsync($"/accounts/{mine}/edit"));
                return await a2.PostAsync($"/accounts/{mine}", HttpHelpers.Form(("_csrf", edit.QuerySelector("input[name=_csrf]")!.GetAttribute("value")!),
                    ("rowVer", edit.QuerySelector("input[name=rowVer]")!.GetAttribute("value")!), ("Name", name), ("OwnerId", I(owner))));
            }
            await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, await EditAsync("Edited by a team-mate", a1Id));
            // take it over via edit
            await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, await EditAsync("Edited by a team-mate", a2Id));
            var detail = await HttpHelpers.ParseAsync(await a2.GetAsync($"/accounts/{mine}"));
            var timeline = detail.QuerySelector("ol.timeline")!.TextContent;
            Assert.Contains("Owner changed from Test User A1 to Test User A2", timeline, StringComparison.Ordinal);
            Assert.Contains("Created", timeline, StringComparison.Ordinal);
            // now the owner: delete works
            await HttpHelpers.ExpectAsync(HttpStatusCode.OK, await a2.GetAsync($"/accounts/{mine}/delete"));
            await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, await PostAsync(a2, "/accounts/new", $"/accounts/{mine}/delete"));
            await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, await a2.GetAsync($"/accounts/{mine}"));

            // the other team and no-team records stay untouchable, even in team mode
            foreach (var id in new[] { otherTeam, noTeamRec })
            {
                await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, await a2.GetAsync($"/accounts/{id}"));
                await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, await a2.GetAsync($"/accounts/{id}/edit"));
                await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, await PostAsync(a2, "/accounts/new", $"/accounts/{id}/delete"));
                await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, await PostAsync(a2, "/accounts/new", $"/accounts/{id}", ("rowVer", DummyRowVer), ("Name", "x"), ("OwnerId", I(a2Id))));
            }
            var foreignOwner = await PostAsync(a2, "/accounts/new", "/accounts", ("Name", Unique("X")), ("OwnerId", I(await UserIdAsync(ManagerB))));
            await HttpHelpers.ExpectAsync(HttpStatusCode.UnprocessableEntity, foreignOwner);
            var noTeamOwner = await PostAsync(a2, "/accounts/new", "/accounts", ("Name", Unique("X")), ("OwnerId", I(await UserIdAsync(NoTeam))));
            await HttpHelpers.ExpectAsync(HttpStatusCode.UnprocessableEntity, noTeamOwner);
        }
        finally
        {
            await SetVisibilityAsync(admin, "private");
        }
    }

    // ---------------- hidden records ----------------

    private sealed record Rec(string Path, int Id, string[] ExtraPosts);

    private async Task<List<Rec>> CreateAllAsync(HttpClient c, string oid)
    {
        var me = I(await UserIdAsync(oid));
        var acc = await CreateAccountAsync(c, oid);
        var contact = await PostAsync(c, "/contacts/new", "/contacts", ("LastName", Unique("Last")), ("AccountId", I(acc)), ("OwnerId", me));
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, contact);
        var form = await HttpHelpers.ParseAsync(await c.GetAsync("/opportunities/new"));
        var stage = form.QuerySelectorAll("#f-StageId option").First().GetAttribute("value")!;
        var opp = await PostAsync(c, "/opportunities/new", "/opportunities", ("Name", Unique("Opp")), ("AccountId", I(acc)), ("StageId", stage), ("Amount", "10"), ("CurrencyCode", "DKK"), ("CloseDate", "2099-01-01"), ("OwnerId", me));
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, opp);
        var act = await PostAsync(c, "/activities/new", "/activities", ("ActivityType", "note"), ("Subject", Unique("Act")), ("AccountId", I(acc)), ("OwnerId", me));
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, act);
        return
        [
            new Rec("/accounts", acc, []),
            new Rec("/contacts", IdFrom(contact), []),
            new Rec("/opportunities", IdFrom(opp), ["won", "lost"]),
            new Rec("/activities", IdFrom(act), ["done", "undone"]),
        ];
    }

    [SqlTheory]
    [InlineData(UserA2)]   // same team, private mode
    [InlineData(ManagerB)] // other team
    [InlineData(NoTeam)]   // no team
    public async Task A_record_the_user_cannot_see_is_404_for_every_entity_and_every_verb(string intruderOid)
    {
        var (f1, owner) = await AsAsync(UserA1);
        using var g1 = f1;
        var (f2, intruder) = await AsAsync(intruderOid);
        using var g2 = f2;
        var me = I(await UserIdAsync(intruderOid));
        foreach (var rec in await CreateAllAsync(owner, UserA1))
        {
            var p = $"{rec.Path}/{rec.Id}";
            foreach (var get in new[] { p, p + "/edit", p + "/delete" })
            {
                await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, await intruder.GetAsync(get));
            }
            await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, await PostAsync(intruder, "/accounts/new", p + "/delete"));
            await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, await PostAsync(intruder, "/accounts/new", p, ("rowVer", DummyRowVer), ("Name", "x"), ("LastName", "x"), ("Subject", "x"), ("ActivityType", "note"), ("OwnerId", me)));
            foreach (var verb in rec.ExtraPosts)
            {
                await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, await PostAsync(intruder, "/accounts/new", $"{p}/{verb}", ("rowVer", DummyRowVer), ("confirm", "1"), ("lostReason", "1")));
            }
            // the owner still sees it, and nothing was changed by the attempts
            await HttpHelpers.ExpectAsync(HttpStatusCode.OK, await owner.GetAsync(p));
        }
    }

    // ---------------- mass assignment ----------------

    private async Task<(int Owner, int? Team, int CreatedBy)> RowAsync(int id)
    {
        await using var c = await OwnerAsync();
        await using var cmd = new SqlCommand("SELECT OwnerId, TeamId, CreatedBy FROM crm.Accounts WHERE AccountId = @i", c);
        cmd.Parameters.AddWithValue("@i", id);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        return (r.GetInt32(0), r.IsDBNull(1) ? null : r.GetInt32(1), r.GetInt32(2));
    }

    [SqlFact]
    public async Task Mass_assignment_of_team_created_by_and_id_is_ignored_on_create_and_update()
    {
        var (f, c) = await AsAsync(UserA1);
        using var guard = f;
        var me = await UserIdAsync(UserA1);
        var mgrB = await UserIdAsync(ManagerB);
        var (fb, mgrClient) = await AsAsync(ManagerB);
        using var guardB = fb;
        var teamB = await RowAsync(await CreateAccountAsync(mgrClient, ManagerB));
        Assert.NotNull(teamB.Team);
        var id = await CreateAccountAsync(c, UserA1, ("TeamId", I(teamB.Team!.Value)), ("CreatedBy", I(mgrB)), ("AccountId", "999999"), ("Id", "999999"), ("ModifiedBy", I(mgrB)), ("RowVer", "x"));
        Assert.NotEqual(999999, id);
        var created = await RowAsync(id);
        Assert.Equal(me, created.Owner);
        Assert.Equal(me, created.CreatedBy);
        Assert.NotEqual(teamB.Team, created.Team); // TeamId always derives from the owner

        var edit = await HttpHelpers.ParseAsync(await c.GetAsync($"/accounts/{id}/edit"));
        var r = await c.PostAsync($"/accounts/{id}", HttpHelpers.Form(("_csrf", edit.QuerySelector("input[name=_csrf]")!.GetAttribute("value")!),
            ("rowVer", edit.QuerySelector("input[name=rowVer]")!.GetAttribute("value")!), ("Name", Unique("Renamed")), ("OwnerId", I(me)),
            ("TeamId", I(teamB.Team.Value)), ("CreatedBy", I(mgrB)), ("AccountId", "999999"), ("Id", "999999")));
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, r);
        var after = await RowAsync(id);
        Assert.Equal(created, after);
        await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, await c.GetAsync("/accounts/999999"));
    }
}
