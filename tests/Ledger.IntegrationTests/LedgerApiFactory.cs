using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Ledger.IntegrationTests;

/// <summary>
/// Hosts the real application in-process on an in-memory SQLite database.
/// Nothing is mocked below the HTTP layer, so these tests exercise routing, validation,
/// error mapping, EF mapping, idempotency and the webhook pipeline together.
/// </summary>
public sealed class LedgerApiFactory : WebApplicationFactory<Program>
{
    public const string Provider = "acme-pay";
    public const string Secret = "integration-test-secret";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // UseSetting feeds the host configuration before Program.cs reads it, which matters because
        // the database provider and the webhook secrets are resolved while the app is being built.
        var settings = new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["Database:AutoMigrate"] = "true",
            ["ConnectionStrings:Ledger"] = "Data Source=:memory:",
            ["Messaging:Kafka:BootstrapServers"] = "",
            ["Auth:Authority"] = "",
            [$"Webhooks:Providers:{Provider}:Secret"] = Secret,
            [$"Webhooks:Providers:{Provider}:ToleranceSeconds"] = "300",
            ["Serilog:MinimumLevel:Default"] = "Warning",
        };
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }
    }

    public static async Task<Guid> OpenAccount(HttpClient client, string owner, string kind, string currency = "BRL")
    {
        var response = await client.PostAsJsonAsync("/api/v1/accounts", new { owner, kind, currency }, Json);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return body.GetProperty("id").GetGuid();
    }

    public static async Task<decimal> Balance(HttpClient client, Guid accountId)
    {
        var body = await client.GetFromJsonAsync<JsonElement>($"/api/v1/accounts/{accountId}", Json);
        return body.GetProperty("balance").GetDecimal();
    }
}
