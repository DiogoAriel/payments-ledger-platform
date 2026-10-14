using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Ledger.Application.Webhooks;

namespace Ledger.IntegrationTests;

public class WebhooksApiTests : IClassFixture<LedgerApiFactory>
{
    private readonly HttpClient _client;

    public WebhooksApiTests(LedgerApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    private static HttpRequestMessage SignedWebhook(string body, string? secret = LedgerApiFactory.Secret, string? nonce = null, DateTimeOffset? sentAt = null)
    {
        var timestamp = (sentAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/providers/{LedgerApiFactory.Provider}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(WebhookVerifier.TimestampHeader, timestamp);
        request.Headers.Add(WebhookVerifier.NonceHeader, nonce ?? Guid.NewGuid().ToString("N"));
        request.Headers.Add(WebhookVerifier.SignatureHeader, WebhookVerifier.ComputeSignature(secret ?? "wrong", timestamp, body));
        return request;
    }

    private static string PayInBody(Guid customer, string externalId, decimal amount) =>
        JsonSerializer.Serialize(new { externalId, customerAccountId = customer, amount, currency = "BRL", direction = "PayIn" }, LedgerApiFactory.Json);

    [Fact]
    public async Task Signed_pay_in_credits_the_customer()
    {
        var alice = await LedgerApiFactory.OpenAccount(_client, "alice", "Customer");

        var response = await _client.SendAsync(SignedWebhook(PayInBody(alice, $"evt-{Guid.NewGuid():N}", 80m)));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await LedgerApiFactory.Balance(_client, alice)).Should().Be(80m);
    }

    [Fact]
    public async Task Redelivery_with_a_new_nonce_is_idempotent_by_external_id()
    {
        var alice = await LedgerApiFactory.OpenAccount(_client, "alice", "Customer");
        var externalId = $"evt-{Guid.NewGuid():N}";

        var first = await _client.SendAsync(SignedWebhook(PayInBody(alice, externalId, 50m)));
        var redelivery = await _client.SendAsync(SignedWebhook(PayInBody(alice, externalId, 50m)));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        redelivery.StatusCode.Should().Be(HttpStatusCode.OK);
        var outcome = await redelivery.Content.ReadFromJsonAsync<JsonElement>(LedgerApiFactory.Json);
        outcome.GetProperty("alreadyPosted").GetBoolean().Should().BeTrue();
        (await LedgerApiFactory.Balance(_client, alice)).Should().Be(50m, "the provider retried, the money must move once");
    }

    [Fact]
    public async Task Replayed_nonce_is_rejected()
    {
        var alice = await LedgerApiFactory.OpenAccount(_client, "alice", "Customer");
        var nonce = Guid.NewGuid().ToString("N");
        var body = PayInBody(alice, $"evt-{Guid.NewGuid():N}", 10m);

        await _client.SendAsync(SignedWebhook(body, nonce: nonce));
        var replay = await _client.SendAsync(SignedWebhook(body, nonce: nonce));

        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Wrong_secret_is_rejected_without_touching_the_ledger()
    {
        var alice = await LedgerApiFactory.OpenAccount(_client, "alice", "Customer");

        var response = await _client.SendAsync(SignedWebhook(PayInBody(alice, $"evt-{Guid.NewGuid():N}", 10m), secret: "not-the-secret"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await LedgerApiFactory.Balance(_client, alice)).Should().Be(0m);
    }

    [Fact]
    public async Task Stale_timestamp_is_rejected()
    {
        var alice = await LedgerApiFactory.OpenAccount(_client, "alice", "Customer");

        var response = await _client.SendAsync(SignedWebhook(PayInBody(alice, $"evt-{Guid.NewGuid():N}", 10m), sentAt: DateTimeOffset.UtcNow.AddMinutes(-15)));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Pay_out_beyond_the_balance_is_refused()
    {
        var alice = await LedgerApiFactory.OpenAccount(_client, "alice", "Customer");
        var body = JsonSerializer.Serialize(new { externalId = $"evt-{Guid.NewGuid():N}", customerAccountId = alice, amount = 5m, currency = "BRL", direction = "PayOut" }, LedgerApiFactory.Json);

        var response = await _client.SendAsync(SignedWebhook(body));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }
}
