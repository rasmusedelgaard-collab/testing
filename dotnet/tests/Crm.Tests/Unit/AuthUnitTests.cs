using System.Security.Claims;
using Crm.Web.Auth;
using Crm.Web.Data;
using Crm.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Crm.Tests.Unit;

public class AuthUnitTests
{
    [Theory]
    [InlineData("/", "/")]
    [InlineData("/accounts?q=a&page=2", "/accounts?q=a&page=2")]
    [InlineData("//evil.example", "/")]
    [InlineData("https://evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("/ok\r\nSet-Cookie: x=1", "/")]
    [InlineData("", "/")]
    [InlineData(null, "/")]
    public void Return_to_is_relative_only(string? input, string expected) => Assert.Equal(expected, ReturnTo.Safe(input));

    [Fact]
    public void Return_to_rejects_overlong_values() => Assert.Equal("/", ReturnTo.Safe("/" + new string('a', 2000)));

    [Fact]
    public void Highest_app_role_wins_and_unknown_values_are_ignored()
    {
        Assert.Equal(CrmRole.Admin, Roles.Map(["CRM.User", "CRM.Admin", "x"]));
        Assert.Equal(CrmRole.Manager, Roles.Map(["CRM.User", "CRM.Manager"]));
        Assert.Equal(CrmRole.User, Roles.Map(["CRM.User"]));
        Assert.Null(Roles.Map(["Admin", "crm.admin"]));
        Assert.Null(Roles.Map([]));
    }

    private static Claim C(string t, string v) => new(t, v);

    [Fact]
    public void Groups_are_lower_cased()
    {
        var g = EntraSignIn.ExtractGroups([C("groups", "AAA-1"), C("groups", "bbb-2")]);
        Assert.False(g.Overage);
        Assert.Equal(["aaa-1", "bbb-2"], g.GroupIds);
    }

    [Fact]
    public void Overage_via_claim_names_or_hasgroups()
    {
        Assert.True(EntraSignIn.ExtractGroups([C("_claim_names", "{\"groups\":\"src1\"}")]).Overage);
        Assert.True(EntraSignIn.ExtractGroups([C("hasgroups", "true")]).Overage);
        Assert.False(EntraSignIn.ExtractGroups([C("hasgroups", "true"), C("groups", "a")]).Overage);
        Assert.False(EntraSignIn.ExtractGroups([]).Overage);
        Assert.True(EntraSignIn.ExtractGroups([C("_claim_names", "{\"groups\":\"x\"}"), C("groups", "a")]).Overage);
        Assert.False(EntraSignIn.ExtractGroups([C("_claim_names", "not json")]).Overage);
        Assert.Empty(EntraSignIn.ExtractGroups([C("hasgroups", "true")]).GroupIds);
    }

    [Theory]
    [InlineData(TeamStatus.Ok, 1, 7, 7)]
    [InlineData(TeamStatus.Ok, 2, 7, null)]
    [InlineData(TeamStatus.Multiple, 2, 7, null)]
    [InlineData(TeamStatus.Overage, 0, 7, null)]
    [InlineData(TeamStatus.None, 0, null, null)]
    public void Team_policy_fails_closed(TeamStatus status, int matches, int? team, int? expected) =>
        Assert.Equal(expected, EntraSignIn.ApplyTeamPolicy(new UpsertResult(1, team, status, matches), "oid", NullLogger.Instance));

    [Fact]
    public void Principal_round_trips_and_carries_only_the_documented_claims()
    {
        var u = new SessionUser(5, "AAAAAAAA-BBBB-4CCC-8DDD-EEEEEEEEEEEE", "Ann Lee", "ann@example.com", CrmRole.Manager, 3, 12345);
        var p = CrmPrincipal.Create(u);
        Assert.Equal("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", p.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal(new RlsContext(5, CrmRole.Manager, 3), CrmPrincipal.ToRlsContext(p));
        Assert.Equal(u with { Oid = u.Oid.ToLowerInvariant() }, CrmPrincipal.ToSessionUser(p));
        var noTeam = CrmPrincipal.Create(u with { TeamId = null, Email = null });
        Assert.Null(noTeam.FindFirst(CrmClaims.TeamId));
        Assert.Null(CrmPrincipal.ToRlsContext(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    [Fact]
    public void Rls_context_validation()
    {
        new RlsContext(1, CrmRole.User, null).Validate();
        Assert.Throws<ArgumentException>(() => new RlsContext(0, CrmRole.User, null).Validate());
        Assert.Throws<ArgumentException>(() => new RlsContext(1, (CrmRole)9, null).Validate());
        Assert.Throws<ArgumentException>(() => new RlsContext(1, CrmRole.User, 0).Validate());
    }

    [Fact]
    public void Attempt_limiter_locks_after_max_failures_and_resets_after_the_window()
    {
        var time = new FakeTimeProvider();
        var l = new AttemptLimiter(3, time);
        for (var i = 0; i < 2; i++) { l.RecordFailure("a"); }
        Assert.False(l.IsLocked("a"));
        l.RecordFailure("a");
        Assert.True(l.IsLocked("a"));
        Assert.False(l.IsLocked("b"));
        time.Advance(TimeSpan.FromMinutes(15));
        Assert.False(l.IsLocked("a"));
        l.RecordFailure("a");
        l.Reset("a");
        Assert.False(l.IsLocked("a"));
    }

    [Fact]
    public void Code_compare_is_exact_and_length_safe()
    {
        Assert.True(TestAccessGate.CodeMatches("abc", "abc"));
        Assert.False(TestAccessGate.CodeMatches("abc", "abcd"));
        Assert.False(TestAccessGate.CodeMatches("", "abc"));
    }

    [Fact]
    public void Gate_per_client_lock_global_cap_and_reset_rules()
    {
        var time = new FakeTimeProvider();
        var gate = new TestAccessGate("right-code", time, NullLogger<TestAccessGate>.Instance);
        for (var i = 0; i < 4; i++) { Assert.Equal(CodeAttempt.Wrong, gate.Submit("c1", "bad")); }
        Assert.Equal(CodeAttempt.WrongAndNowLocked, gate.Submit("c1", "bad"));
        Assert.Equal(CodeAttempt.Locked, gate.Submit("c1", "right-code")); // even the right code while locked
        Assert.Equal(CodeAttempt.Ok, gate.Submit("c2", "right-code")); // others unaffected
        time.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(CodeAttempt.Ok, gate.Submit("c1", "right-code"));
    }

    [Fact]
    public void Global_cap_locks_everyone_and_a_success_does_not_reset_it()
    {
        var gate = new TestAccessGate("right-code", new FakeTimeProvider(), NullLogger<TestAccessGate>.Instance);
        for (var i = 0; i < 50; i++)
        {
            gate.Submit("client" + i, "bad"); // one failure each: no per-client lock
        }
        Assert.True(gate.IsLocked("brand-new-client"));
        Assert.Equal(CodeAttempt.Locked, gate.Submit("brand-new-client", "right-code"));
    }

    [Fact]
    public void Ticket_round_trip_keeps_identity_only()
    {
        Assert.Equal("00000000-0000-4000-8000-00000000a001", TestUsers.SeedOids[0]);
        Assert.Equal(7, TestUsers.SeedOids.Count);
    }

    [Theory]
    [InlineData("#1D4ED8", "#1942B8")]
    [InlineData("#000000", "#000000")]
    [InlineData("#FFFFFF", "#D9D9D9")]
    [InlineData("nope", "#1942B8")]
    public void Darken(string input, string expected) => Assert.Equal(expected, Color.Darken(input));

    [Fact]
    public void Contrast_rules()
    {
        Assert.True(Color.AccentContrast("#1D4ED8") >= 4.5);
        Assert.True(Color.AccentContrast("#FFFF00") < 4.5);
        Assert.Equal(0, Color.ContrastWithWhite("zzz"));
        Assert.True(Color.IsHexColor("#abcdef"));
        Assert.False(Color.IsHexColor("#abcde"));
        Assert.False(Color.IsHexColor("#abcdef\n"));
    }
}
