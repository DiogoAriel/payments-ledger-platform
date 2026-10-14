using System.Security.Cryptography;
using System.Text;
using Ledger.Application.Abstractions;

namespace Ledger.Application.Webhooks;

/// <summary>
/// Settings for the payment providers that call our webhook endpoint.
/// Each shared secret is used to compute an HMAC-SHA256 over "{timestamp}.{body}".
/// </summary>
public sealed class WebhookOptions
{
    public const string SectionName = "Webhooks";

    /// <summary>Providers keyed by the name used in the URL: /webhooks/providers/{provider}.</summary>
    public Dictionary<string, WebhookProviderSettings> Providers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public sealed class WebhookProviderSettings
    {
        public string Secret { get; init; } = string.Empty;
        public int ToleranceSeconds { get; init; } = 300;
    }
}

public enum WebhookRejection
{
    None = 0,
    UnknownProvider,
    MissingHeaders,
    MalformedTimestamp,
    StaleTimestamp,
    BadSignature,
    Replay,
}

public sealed record WebhookVerification(bool Accepted, WebhookRejection Rejection)
{
    public static readonly WebhookVerification Ok = new(true, WebhookRejection.None);
    public static WebhookVerification Reject(WebhookRejection reason) => new(false, reason);
}

/// <summary>
/// Fail-closed webhook authentication. A request is accepted only when every check passes:
/// the provider is configured, the headers are present, the timestamp is inside the tolerance window,
/// the HMAC signature matches, and the nonce has never been seen. Any doubt means rejection.
/// Checks are ordered from cheapest to most expensive, and the signature is compared in constant time.
/// </summary>
public sealed class WebhookVerifier(WebhookOptions options, IReplayGuard replayGuard, IClock clock)
{
    public const string SignatureHeader = "X-Signature";
    public const string TimestampHeader = "X-Timestamp";
    public const string NonceHeader = "X-Nonce";

    public async Task<WebhookVerification> Verify(
        string provider,
        string? signature,
        string? timestamp,
        string? nonce,
        string body,
        CancellationToken cancellationToken)
    {
        if (!options.Providers.TryGetValue(provider, out var settings) || string.IsNullOrEmpty(settings.Secret))
        {
            return WebhookVerification.Reject(WebhookRejection.UnknownProvider);
        }

        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(timestamp) || string.IsNullOrWhiteSpace(nonce))
        {
            return WebhookVerification.Reject(WebhookRejection.MissingHeaders);
        }

        if (!long.TryParse(timestamp, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var unixSeconds))
        {
            return WebhookVerification.Reject(WebhookRejection.MalformedTimestamp);
        }

        var sentAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        var tolerance = TimeSpan.FromSeconds(settings.ToleranceSeconds);
        var skew = clock.UtcNow - sentAt;
        if (skew > tolerance || skew < -tolerance)
        {
            return WebhookVerification.Reject(WebhookRejection.StaleTimestamp);
        }

        var expected = ComputeSignature(settings.Secret, timestamp, body);
        if (!SignaturesMatch(expected, signature))
        {
            return WebhookVerification.Reject(WebhookRejection.BadSignature);
        }

        // The nonce is registered only after the signature is valid, so an attacker cannot
        // burn legitimate nonces by sending unsigned requests.
        var registered = await replayGuard.TryRegister(provider, nonce, sentAt + tolerance, cancellationToken);
        return registered ? WebhookVerification.Ok : WebhookVerification.Reject(WebhookRejection.Replay);
    }

    public static string ComputeSignature(string secret, string timestamp, string body)
    {
        var payload = Encoding.UTF8.GetBytes($"{timestamp}.{body}");
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool SignaturesMatch(string expected, string provided)
    {
        var normalized = provided.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? provided["sha256=".Length..]
            : provided;

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var providedBytes = Encoding.UTF8.GetBytes(normalized.ToLowerInvariant());
        return expectedBytes.Length == providedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }
}
