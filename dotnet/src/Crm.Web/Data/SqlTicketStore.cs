using System.Security.Cryptography;
using System.Text.Json;
using Crm.Web.Auth;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Logging;

namespace Crm.Web.Data;

/// <summary>
/// ITicketStore over app.Sessions (ADR-0004/0007). The cookie carries only a protected random key; the row holds minimal
/// JSON (no tokens, no serialized ticket). Idle timeout is rolling (at most one write per minute); cleanup is
/// opportunistic after a response, never timer-driven (a timer would keep a serverless database awake).
/// </summary>
public sealed class SqlTicketStore(SystemDb db, TimeProvider time, SessionTimeouts options, ILogger<SqlTicketStore> log) : ITicketStore
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);
    private const int CleanupBatch = 1000;

    private sealed record Payload(int V, int UserId, string Oid, string Name, string? Email, string Role, int? TeamId, long AuthAt, long ActiveCheckedAt);
    private sealed record Row(string Data, DateTime ExpiresAt);

    private DateTime lastCleanup = time.GetUtcNow().UtcDateTime;
    private int cleaning;

    private DateTime IdleExpiry() => time.GetUtcNow().UtcDateTime.AddMinutes(options.IdleMinutes);

    private static DynamicParameters Sid(string key)
    {
        var p = new DynamicParameters();
        p.Add("sid", key, System.Data.DbType.AnsiString, size: 128);
        return p;
    }

    private static string Serialize(AuthenticationTicket ticket)
    {
        var u = CrmPrincipal.ToSessionUser(ticket.Principal) ?? throw new InvalidOperationException("Ticket has no CRM identity");
        var checkedAt = ticket.Properties.Items.TryGetValue(CrmClaims.ActiveCheckedAtItem, out var s) && long.TryParse(s, out var v) ? v : u.AuthAtMs;
        return JsonSerializer.Serialize(new Payload(1, u.UserId, u.Oid, u.Name, u.Email, u.Role.ToString(), u.TeamId, u.AuthAtMs, checkedAt));
    }

    private static AuthenticationTicket? Deserialize(string json, string scheme)
    {
        try
        {
            var p = JsonSerializer.Deserialize<Payload>(json);
            if (p is null || p.V != 1 || !Enum.TryParse<CrmRole>(p.Role, out var role))
            {
                return null;
            }
            var principal = CrmPrincipal.Create(new SessionUser(p.UserId, p.Oid, p.Name, p.Email, role, p.TeamId, p.AuthAt));
            var props = new AuthenticationProperties();
            props.Items[CrmClaims.ActiveCheckedAtItem] = p.ActiveCheckedAt.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new AuthenticationTicket(principal, props, scheme);
        }
        catch (JsonException)
        {
            return null; // corrupt row: treat as no session
        }
    }

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = Base64Url(RandomNumberGenerator.GetBytes(32));
        var p = Sid(key);
        p.Add("data", Serialize(ticket), System.Data.DbType.String, size: -1);
        p.Add("exp", IdleExpiry(), System.Data.DbType.DateTime2);
        await db.ExecuteAsync("INSERT INTO app.Sessions (Sid, Data, ExpiresAt) VALUES (@sid, @data, @exp)", p, CancellationToken.None).ConfigureAwait(false);
        return key;
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        var p = Sid(key);
        p.Add("data", Serialize(ticket), System.Data.DbType.String, size: -1);
        p.Add("exp", IdleExpiry(), System.Data.DbType.DateTime2);
        await db.ExecuteAsync("UPDATE app.Sessions SET Data = @data, ExpiresAt = @exp WHERE Sid = @sid", p, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        if (key.Length is 0 or > 128)
        {
            return null;
        }
        var now = time.GetUtcNow().UtcDateTime;
        var p = Sid(key);
        p.Add("now", now, System.Data.DbType.DateTime2);
        var row = await db.QuerySingleOrDefaultAsync<Row>("SELECT Data, ExpiresAt FROM app.Sessions WHERE Sid = @sid AND ExpiresAt > @now", p, CancellationToken.None).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }
        // Rolling idle timeout: ExpiresAt = lastTouch + idle, so a touch older than a minute is refreshed.
        if (row.ExpiresAt - TimeSpan.FromMinutes(options.IdleMinutes) < now - TouchInterval)
        {
            p.Add("exp", IdleExpiry(), System.Data.DbType.DateTime2);
            await db.ExecuteAsync("UPDATE app.Sessions SET ExpiresAt = @exp WHERE Sid = @sid", p, CancellationToken.None).ConfigureAwait(false);
        }
        return Deserialize(row.Data, CookieAuthenticationDefaults.AuthenticationScheme);
    }

    public Task RemoveAsync(string key) =>
        db.ExecuteAsync("DELETE FROM app.Sessions WHERE Sid = @sid", Sid(key), CancellationToken.None);

    /// <summary>Call after a response has been sent. At most once per 15 minutes per instance; errors are logged.</summary>
    public void MaybeCleanup()
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (now - lastCleanup < CleanupInterval || Interlocked.Exchange(ref cleaning, 1) == 1)
        {
            return;
        }
        lastCleanup = now;
        _ = Task.Run(async () =>
        {
            try
            {
                int n;
                do
                {
                    n = await db.ExecuteAsync("DELETE TOP (@batch) FROM app.Sessions WHERE ExpiresAt < @now", new { batch = CleanupBatch, now }, CancellationToken.None).ConfigureAwait(false);
                }
                while (n >= CleanupBatch);
            }
            catch (Exception e)
            {
                log.LogWarning("session cleanup failed ({Type})", e.GetType().Name);
            }
            finally
            {
                Interlocked.Exchange(ref cleaning, 0);
            }
        });
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record SessionTimeouts(int IdleMinutes, int AbsoluteHours);
