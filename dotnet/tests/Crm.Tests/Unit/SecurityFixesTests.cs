using System.Buffers.Binary;
using System.Net;
using Crm.Tests.Http;
using Crm.Web.Auth;
using Crm.Web.Data;
using Crm.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Crm.Tests.Unit;

public class LogoCheckTests
{
    private static byte[] Png(uint w = 1, uint h = 1, bool iend = true, int extraChunkLen = 0)
    {
        var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
            ms.Write(len);
            ms.Write(System.Text.Encoding.ASCII.GetBytes(type));
            ms.Write(data);
            ms.Write(new byte[4]); // CRC is not verified
        }
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), w);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), h);
        ihdr[8] = 8;
        ihdr[9] = 6;
        Chunk("IHDR", ihdr);
        Chunk("IDAT", new byte[extraChunkLen]);
        if (iend) { Chunk("IEND", []); }
        return ms.ToArray();
    }

    [Fact]
    public void A_small_complete_png_is_accepted() => Assert.True(SettingsValidators.CheckLogo(Png()).Ok);

    [Fact]
    public void Width_and_height_up_to_1024_are_accepted_and_larger_or_zero_refused()
    {
        Assert.True(SettingsValidators.CheckLogo(Png(1024, 1024)).Ok);
        Assert.False(SettingsValidators.CheckLogo(Png(1025, 10)).Ok);
        Assert.False(SettingsValidators.CheckLogo(Png(10, 1025)).Ok);
        Assert.False(SettingsValidators.CheckLogo(Png(0, 10)).Ok);
        Assert.False(SettingsValidators.CheckLogo(Png(uint.MaxValue, uint.MaxValue)).Ok);
        Assert.Equal("Logo must be a PNG, 500 KB or smaller.", SettingsValidators.CheckLogo(Png(5000, 5000)).Message);
    }

    [Fact]
    public void A_missing_iend_trailing_bytes_or_a_lying_chunk_length_are_refused()
    {
        Assert.False(SettingsValidators.CheckLogo(Png(iend: false)).Ok);
        Assert.False(SettingsValidators.CheckLogo([.. Png(), 0]).Ok); // data after IEND
        var truncated = Png();
        Assert.False(SettingsValidators.CheckLogo(truncated.AsSpan(0, truncated.Length - 5)).Ok);
        var lying = Png();
        BinaryPrimitives.WriteUInt32BigEndian(lying.AsSpan(33, 4), 0x7FFFFFFF); // IDAT claims to be huge
        Assert.False(SettingsValidators.CheckLogo(lying).Ok);
    }

    [Fact]
    public void Svg_is_still_refused_with_its_own_message() =>
        Assert.Equal("SVG logos are not supported. Upload a PNG, 500 KB or smaller.", SettingsValidators.CheckLogo("<svg></svg>"u8.ToArray()).Message);
}

public class GateAndBrandingTests
{
    [Fact]
    public void An_unconfigured_gate_never_accepts_even_an_empty_code()
    {
        var gate = new TestAccessGate("", new FakeTimeProvider(), NullLogger<TestAccessGate>.Instance);
        Assert.Equal(CodeAttempt.Wrong, gate.Submit("c", ""));
        Assert.Equal(CodeAttempt.Wrong, gate.Submit("c", "anything"));
    }

    [Fact]
    public void A_configured_gate_still_accepts_the_right_code() =>
        Assert.Equal(CodeAttempt.Ok, new TestAccessGate("a-long-enough-code-123456", new FakeTimeProvider(), NullLogger<TestAccessGate>.Instance).Submit("c", "a-long-enough-code-123456"));

    [Fact]
    public async Task Branding_invalidate_and_reads_are_safe_under_concurrency_and_fall_back_to_defaults()
    {
        var cache = new BrandingCache(null!, TimeProvider.System); // db unusable: every refresh fails and serves defaults
        var tasks = Enumerable.Range(0, 64).Select(i => Task.Run(async () =>
        {
            if (i % 8 == 0) { cache.Invalidate(); }
            Assert.Equal(BrandingValues.Default, await cache.GetAsync(CancellationToken.None));
        }));
        await Task.WhenAll(tasks);
    }
}

public sealed class HttpsRedirectTests
{
    [Fact]
    public async Task Plain_http_is_redirected_to_https_when_cookies_are_secure_but_health_probes_are_not()
    {
        using var f = new CrmFactory(null, new Dictionary<string, string?> { ["REDIRECT_URI"] = "https://crmtest.example.com/auth/redirect" });
        var c = HttpHelpers.NewClient(f);
        var r = await c.GetAsync("/signin");
        Assert.Equal(HttpStatusCode.PermanentRedirect, r.StatusCode);
        Assert.Equal("https://localhost/signin", r.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task Plain_http_is_served_when_the_redirect_uri_is_http()
    {
        using var f = new CrmFactory();
        Assert.Equal(HttpStatusCode.OK, (await HttpHelpers.NewClient(f).GetAsync("/signin")).StatusCode);
    }
}
