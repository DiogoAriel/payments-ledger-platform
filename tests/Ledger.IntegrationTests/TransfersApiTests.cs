using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Ledger.IntegrationTests;

public class TransfersApiTests : IClassFixture<LedgerApiFactory>
{
    private readonly HttpClient _client;

    public TransfersApiTests(LedgerApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    private static HttpRequestMessage Transfer(object body, string? idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transfers")
        {
            Content = JsonContent.Create(body, options: LedgerApiFactory.Json),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }
        return request;
    }

    [Fact]
    public async Task Posts_a_transfer_and_replays_it_idempotently()
    {
        var settlement = await LedgerApiFactory.OpenAccount(_client, "acme-pay", "ProviderSettlement");
        var alice = await LedgerApiFactory.OpenAccount(_client, "alice", "Customer");
        var body = new { fromAccountId = settlement, toAccountId = alice, amount = 150.25m, currency = "BRL", description = "top-up" };
        var key = $"idem-{Guid.NewGuid():N}";

        var first = await _client.SendAsync(Transfer(body, key));
        var second = await _client.SendAsync(Transfer(body, key));

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.OK, "a replay is acknowledged, not re-posted");

        var firstResult = await first.Content.ReadFromJsonAsync<JsonElement>(LedgerApiFactory.Json);
        var secondResult = await second.Content.ReadFromJsonAsync<JsonElement>(LedgerApiFactory.Json);
        secondResult.GetProperty("entryId").GetGuid().Should().Be(firstResult.GetProperty("entryId").GetGuid());
        secondResult.GetProperty("alreadyPosted").GetBoolean().Should().BeTrue();

        (await LedgerApiFactory.Balance(_client, alice)).Should().Be(150.25m);
        (await LedgerApiFactory.Balance(_client, settlement)).Should().Be(-150.25m);
    }

    [Fact]
    public async Task Requires_an_idempotency_key()
    {
        var a = await LedgerApiFactory.OpenAccount(_client, "a", "Customer");
        var b = await LedgerApiFactory.OpenAccount(_client, "b", "Customer");

        var response = await _client.SendAsync(Transfer(new { fromAccountId = a, toAccountId = b, amount = 1m, currency = "BRL", description = "x" }, null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Insufficient_funds_return_422_and_leave_balances_untouched()
    {
        var alice = await LedgerApiFactory.OpenAccount(_client, "alice", "Customer");
        var bob = await LedgerApiFactory.OpenAccount(_client, "bob", "Customer");

        var response = await _client.SendAsync(Transfer(
            new { fromAccountId = alice, toAccountId = bob, amount = 10m, currency = "BRL", description = "p2p" },
            $"idem-{Guid.NewGuid():N}"));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(LedgerApiFactory.Json);
        problem.GetProperty("detail").GetString().Should().Contain("cannot cover");
        (await LedgerApiFactory.Balance(_client, alice)).Should().Be(0m);
        (await LedgerApiFactory.Balance(_client, bob)).Should().Be(0m);
    }

    [Fact]
    public async Task Validation_errors_return_400_with_field_details()
    {
        var a = await LedgerApiFactory.OpenAccount(_client, "a", "Customer");

        var response = await _client.SendAsync(Transfer(
            new { fromAccountId = a, toAccountId = a, amount = -5m, currency = "BRL", description = "" },
            $"idem-{Guid.NewGuid():N}"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(LedgerApiFactory.Json);
        var errors = problem.GetProperty("errors");
        errors.TryGetProperty("Amount", out _).Should().BeTrue();
        errors.TryGetProperty("ToAccountId", out _).Should().BeTrue();
        errors.TryGetProperty("Description", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Statement_lists_entries_newest_first()
    {
        var settlement = await LedgerApiFactory.OpenAccount(_client, "acme-pay", "ProviderSettlement");
        var alice = await LedgerApiFactory.OpenAccount(_client, "alice", "Customer");

        foreach (var amount in new[] { 10m, 20m })
        {
            var response = await _client.SendAsync(Transfer(
                new { fromAccountId = settlement, toAccountId = alice, amount, currency = "BRL", description = $"top-up {amount}" },
                $"idem-{Guid.NewGuid():N}"));
            response.EnsureSuccessStatusCode();
        }

        var statement = await _client.GetFromJsonAsync<JsonElement>($"/api/v1/accounts/{alice}/statement", LedgerApiFactory.Json);

        var entries = statement.GetProperty("entries").EnumerateArray().ToList();
        entries.Should().HaveCount(2);
        entries.Should().OnlyContain(e => e.GetProperty("direction").GetString() == "Credit");
        statement.GetProperty("account").GetProperty("balance").GetDecimal().Should().Be(30m);
    }

    [Fact]
    public async Task Unknown_account_returns_404()
    {
        var response = await _client.GetAsync($"/api/v1/accounts/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
