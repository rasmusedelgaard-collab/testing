using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Crm.Web.Auth;

public enum CodeAttempt { Ok, Wrong, Locked, WrongAndNowLocked }

/// <summary>
/// Test-access-code rules (ADR-0006): constant-time compare, per-client limiter (5 failures / 15 min, success resets)
/// and a global cap (50 failures / 15 min across all clients, success does not reset it).
/// </summary>
public sealed class TestAccessGate(string accessCode, TimeProvider time, ILogger<TestAccessGate> log)
{
    private const string Global = "all";
    private readonly AttemptLimiter _client = new(5, time);
    private readonly AttemptLimiter _global = new(50, time);

    public bool IsLocked(string clientKey) => _client.IsLocked(clientKey) || _global.IsLocked(Global);

    /// <summary>Constant-time compare that does not leak the length of the secret (both sides hashed first).</summary>
    public static bool CodeMatches(string given, string expected) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    public CodeAttempt Submit(string clientKey, string code)
    {
        if (accessCode.Length == 0)
        {
            return CodeAttempt.Wrong; // not configured (entra mode): an empty submission must never match an empty code
        }
        if (IsLocked(clientKey))
        {
            log.LogWarning("test access code attempt while locked out (client {Client})", clientKey);
            return CodeAttempt.Locked;
        }
        if (!CodeMatches(code, accessCode))
        {
            _client.RecordFailure(clientKey);
            _global.RecordFailure(Global);
            log.LogWarning("wrong test access code (client {Client})", clientKey);
            if (_global.IsLocked(Global))
            {
                log.LogWarning("test access code: global failed-attempt cap reached; sign-in locked for everyone");
            }
            return IsLocked(clientKey) ? CodeAttempt.WrongAndNowLocked : CodeAttempt.Wrong;
        }
        _client.Reset(clientKey); // the global counter is deliberately not reset by a success
        return CodeAttempt.Ok;
    }
}
