namespace Crm.Web.Services.Demo;

/// <summary>
/// Demo-data conventions. Rows are tracked, never guessed: the seeder writes one 'C' audit row per record (and per FX rate) with the run's
/// CorrelationId, and the run id is kept in the Settings key <see cref="RunIdKey"/>. A reset deletes exactly those records. The whole set is
/// marked by <see cref="MarkerKey"/>, and only a database marked <see cref="TestEnvKey"/>=<see cref="TestEnvValue"/> (written by seed-test) is seeded at all.
/// The demo users live in the reserved test-oid range (00000000-0000-4000-8000-...), so the test-login picker lists them.
/// </summary>
public static class DemoMarker
{
    public const string MarkerKey = "demo.seeded";
    public const string RunIdKey = "demo.runId";
    public const string TestEnvKey = "environment";
    public const string TestEnvValue = "test";
    public const string InactiveKey = "peter";
    public const string NoTeamKey = "lise";
    public const string PersonaKey = "lars";
    public const string AdminKey = "admin";
    public const int SchemaVersion = 1;
}

public sealed record DemoTeam(string Key, string Name, Guid GroupId);

public sealed record DemoUser(string Key, Guid Oid, string Name, string Email, string Role, string? TeamKey, bool Active = true);

public sealed record DemoAccount(string Key, string Name, string Website, string? Phone, string? Industry, string Street, string PostalCode, string City, string Country, string Notes, string OwnerKey, DateTime CreatedAt);

public sealed record DemoContact(string Key, string? AccountKey, string? FirstName, string LastName, string JobTitle, string Email, string? Phone, string? Mobile, string OwnerKey, DateTime CreatedAt);

/// <param name="StageSlot">0-3 = open stage by rank, <see cref="DemoOpportunity.SlotWon"/>, <see cref="DemoOpportunity.SlotLost"/>. The seeder maps slots to the stages that exist.</param>
public sealed record DemoOpportunity(string Key, string Name, string AccountKey, string? ContactKey, int StageSlot, decimal Amount, string Currency, DateOnly CloseDate, string LeadSource, string? LostReason, DateTime? ClosedAt, string OwnerKey, DateTime CreatedAt, DateTime ModifiedAt)
{
    /// <summary>History for the record timeline: each stage change in order, and optionally who owned the deal before.</summary>
    public IReadOnlyList<DemoStageChange> StageChanges { get; init; } = [];

    public string? PreviousOwnerKey { get; init; }

    public DateTime? OwnerChangedAt { get; init; }

    public const int SlotWon = 4;
    public const int SlotLost = 5;

    public bool IsOpen => StageSlot < SlotWon;
}

public sealed record DemoStageChange(int FromSlot, int ToSlot, DateTime At);

public sealed record DemoActivity(string Type, string Subject, string? Description, string AccountKey, string? ContactKey, string? OpportunityKey, DateOnly? DueDate, TimeOnly? DueTime, bool IsDone, DateTime? DoneAt, string OwnerKey, DateTime CreatedAt, DateTime ModifiedAt);

public sealed record DemoFxRate(string Currency, DateOnly Month, decimal Rate);

public sealed record DemoDataset(
    DateOnly Today,
    IReadOnlyList<DemoTeam> Teams,
    IReadOnlyList<DemoUser> Users,
    IReadOnlyList<DemoAccount> Accounts,
    IReadOnlyList<DemoContact> Contacts,
    IReadOnlyList<DemoOpportunity> Opportunities,
    IReadOnlyList<DemoActivity> Activities,
    IReadOnlyList<DemoFxRate> FxRates);

/// <param name="FxEdge">QA variant: the current month's SEK rate is missing and two open deals are in NOK, which has no rates at all (FX notes appear on the dashboard).</param>
/// <param name="DemoSettings">Also set the demo settings: visibility team, locale da-DK, enabled currencies DKK, EUR, SEK, USD (plus NOK with <paramref name="FxEdge"/>). Industry values are never deactivated.</param>
/// <param name="PurgeLegacy">One-time clean-up of demo data written by the first seeder version (no run id recorded): deletes it by the old definition and removes the marker. Seeds nothing.</param>
public sealed record DemoOptions(bool FxEdge = false, bool DemoSettings = false, bool PurgeLegacy = false);

/// <summary>SplitMix64: tiny, fully specified, identical on every platform and runtime version (System.Random is not specified to be).</summary>
public sealed class DemoRandom(ulong seed)
{
    private ulong _state = seed;

    private ulong NextU64()
    {
        unchecked
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>0 &lt;= n &lt; maxExclusive.</summary>
    public int Next(int maxExclusive) => (int)(NextU64() % (ulong)maxExclusive);

    /// <summary>Inclusive on both ends.</summary>
    public int Range(int low, int highInclusive) => low + Next(highInclusive - low + 1);

    public double NextDouble() => (NextU64() >> 11) * (1.0 / (1UL << 53));

    public bool Chance(double p) => NextDouble() < p;

    public T Pick<T>(IReadOnlyList<T> items) => items[Next(items.Count)];

    public T Weighted<T>(IReadOnlyList<(T Item, int Weight)> items)
    {
        var roll = Next(items.Sum(i => i.Weight));
        foreach (var (item, weight) in items)
        {
            if (roll < weight) { return item; }
            roll -= weight;
        }
        return items[^1].Item;
    }

    public void Shuffle<T>(IList<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
