using FluentAssertions;
using Ledger.Application.Webhooks;

namespace Ledger.UnitTests;

public class WebhookVerifierTests
{
    private const string Provider = "acme-pay";
    private const string Secret = "unit-test-secret";
    private const string Body = """{"externalId":"evt-1","amount":10.00}""";

    private static readonly DateTimeOffset Now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly FixedClock _clock = new(Now);
    private readonly InMemoryReplayGuard _replayGuard = new();
    private readonly WebhookVerifier _verifier;

    public WebhookVerifierTests()
    {
        var options = new WebhookOptions
        {
            Providers = { [Provider] = new WebhookOptions.WebhookProviderSettings { Secret = Secret, ToleranceSeconds = 300 } },
        };
        _verifier = new WebhookVerifier(options, _replayGuard, _clock);
    }

    private static string Timestamp(DateTimeOffset at) => at.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Accepts_a_correctly_signed_fresh_request()
    {
        var ts = Timestamp(Now);
        var signature = WebhookVerifier.ComputeSignature(Secret, ts, Body);

        var result = await _verifier.Verify(Provider, signature, ts, "nonce-1", Body, CancellationToken.None);

        result.Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task Accepts_the_sha256_prefixed_signature_format()
    {
        var ts = Timestamp(Now);
        var signature = "sha256=" + WebhookVerifier.ComputeSignature(Secret, ts, Body).ToUpperInvariant();

        var result = await _verifier.Verify(Provider, signature, ts, "nonce-2", Body, CancellationToken.None);

        result.Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task Rejects_a_tampered_body()
    {
        var ts = Timestamp(Now);
        var signature = WebhookVerifier.ComputeSignature(Secret, ts, Body);

        var result = await _verifier.Verify(Provider, signature, ts, "nonce-3", Body.Replace("10.00", "99.00", StringComparison.Ordinal), CancellationToken.None);

        result.Should().Be(WebhookVerification.Reject(WebhookRejection.BadSignature));
    }

    [Fact]
    public async Task Rejects_a_replayed_nonce()
    {
        var ts = Timestamp(Now);
        var signature = WebhookVerifier.ComputeSignature(Secret, ts, Body);

        await _verifier.Verify(Provider, signature, ts, "nonce-4", Body, CancellationToken.None);
        var replay = await _verifier.Verify(Provider, signature, ts, "nonce-4", Body, CancellationToken.None);

        replay.Should().Be(WebhookVerification.Reject(WebhookRejection.Replay));
    }

    [Fact]
    public async Task Rejects_a_request_outside_the_tolerance_window()
    {
        var old = Now.AddMinutes(-10);
        var ts = Timestamp(old);
        var signature = WebhookVerifier.ComputeSignature(Secret, ts, Body);

        var result = await _verifier.Verify(Provider, signature, ts, "nonce-5", Body, CancellationToken.None);

        result.Should().Be(WebhookVerification.Reject(WebhookRejection.StaleTimestamp));
    }

    [Fact]
    public async Task Rejects_unknown_providers_before_doing_any_work()
    {
        var result = await _verifier.Verify("someone-else", "sig", Timestamp(Now), "nonce-6", Body, CancellationToken.None);

        result.Should().Be(WebhookVerification.Reject(WebhookRejection.UnknownProvider));
    }

    [Fact]
    public async Task Rejects_missing_headers()
    {
        var result = await _verifier.Verify(Provider, null, Timestamp(Now), "nonce-7", Body, CancellationToken.None);

        result.Should().Be(WebhookVerification.Reject(WebhookRejection.MissingHeaders));
    }

    [Fact]
    public async Task Does_not_burn_a_nonce_when_the_signature_is_wrong()
    {
        var ts = Timestamp(Now);
        var good = WebhookVerifier.ComputeSignature(Secret, ts, Body);

        await _verifier.Verify(Provider, "deadbeef", ts, "nonce-8", Body, CancellationToken.None);
        var legitimate = await _verifier.Verify(Provider, good, ts, "nonce-8", Body, CancellationToken.None);

        legitimate.Accepted.Should().BeTrue("an unsigned request must not be able to block a real delivery");
    }
}
