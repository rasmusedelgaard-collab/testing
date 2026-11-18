using System.Security.Cryptography;
using System.Text;

namespace Crm.Web.Services;

/// <summary>The seeded test users and the reserved object-id range (port of src/testUsers.ts and TEST_USERS).</summary>
public static class TestUsers
{
    public const string OidPrefix = "00000000-0000-4000-8000-";

    public static readonly IReadOnlyList<string> SeedOids =
    [
        "00000000-0000-4000-8000-00000000a001",
        "00000000-0000-4000-8000-00000000a002",
        "00000000-0000-4000-8000-00000000a003",
        "00000000-0000-4000-8000-00000000a004",
        "00000000-0000-4000-8000-00000000b001",
        "00000000-0000-4000-8000-00000000b002",
        "00000000-0000-4000-8000-00000000c001",
    ];

    /// <summary>Deterministic oid of the named test Admin, so seeding twice updates the same row.</summary>
    public static string AdminOid(string email)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("crm-test-admin:" + email.Trim().ToLowerInvariant()));
        return OidPrefix + Convert.ToHexStringLower(hash)[..12];
    }
}
