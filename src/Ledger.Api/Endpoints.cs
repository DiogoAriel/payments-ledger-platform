using System.Text.Json;
using Ledger.Application.Abstractions;
using Ledger.Application.Accounts;
using Ledger.Application.Transfers;
using Ledger.Application.Webhooks;
using Ledger.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Ledger.Api;

/// <summary>
/// HTTP contracts. Request bodies are separate records from the commands so the public API can evolve
/// without touching the application layer, and so idempotency keys travel in headers as the HTTP
/// convention expects.
/// </summary>
public static class Endpoints
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    public sealed record OpenAccountRequest(string Owner, AccountKind Kind, string Currency);

    public sealed record TransferRequest(Guid FromAccountId, Guid ToAccountId, decimal Amount, string Currency, string Description);

    public sealed record ProviderSettlementPayload(string ExternalId, Guid CustomerAccountId, decimal Amount, string Currency, SettlementDirection Direction);

    public static RouteGroupBuilder MapAccountEndpoints(this RouteGroupBuilder group)
    {
        var accounts = group.MapGroup("/accounts").WithTags("Accounts");

        accounts.MapPost("/", async (OpenAccountRequest request, ISender sender, CancellationToken ct) =>
        {
            var account = await sender.Send(new OpenAccountCommand(request.Owner, request.Kind, request.Currency), ct);
            return Results.Created($"/api/v1/accounts/{account.Id}", account);
        })
        .WithSummary("Open an account")
        .Produces<AccountDto>(StatusCodes.Status201Created)
        .ProducesValidationProblem();

        accounts.MapGet("/{id:guid}", async (Guid id, ISender sender, CancellationToken ct) =>
        {
            var account = await sender.Query(new GetAccountQuery(id), ct);
            return account is null ? Results.NotFound() : Results.Ok(account);
        })
        .WithSummary("Get an account and its balance")
        .Produces<AccountDto>()
        .Produces(StatusCodes.Status404NotFound);

        accounts.MapGet("/{id:guid}/statement", async (Guid id, int? limit, ISender sender, CancellationToken ct) =>
        {
            var statement = await sender.Query(new GetStatementQuery(id, limit ?? 50), ct);
            return statement is null ? Results.NotFound() : Results.Ok(statement);
        })
        .WithSummary("List the entries that touched an account, newest first")
        .Produces<StatementDto>()
        .Produces(StatusCodes.Status404NotFound);

        return group;
    }

    public static RouteGroupBuilder MapTransferEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/transfers", async (
            TransferRequest request,
            [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
            ISender sender,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(idempotencyKey))
            {
                return Results.Problem(
                    title: $"Missing {IdempotencyKeyHeader} header.",
                    detail: "Send a unique key per transfer so retries are safe.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var result = await sender.Send(new PostTransferCommand(
                idempotencyKey, request.FromAccountId, request.ToAccountId, request.Amount, request.Currency, request.Description), ct);

            // 200 for a replay, 201 for a fresh posting: the client can tell them apart without parsing the body.
            return result.AlreadyPosted ? Results.Ok(result) : Results.Created($"/api/v1/entries/{result.EntryId}", result);
        })
        .WithTags("Transfers")
        .WithSummary("Post a transfer between two accounts (idempotent by Idempotency-Key)")
        .Produces<TransferResult>(StatusCodes.Status201Created)
        .Produces<TransferResult>()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status409Conflict);

        return group;
    }

    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/providers/{provider}", async (
            string provider,
            HttpRequest httpRequest,
            WebhookVerifier verifier,
            ISender sender,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Webhooks");

            // The raw body is read once and used both for the signature and for deserialization,
            // so the bytes that were signed are exactly the bytes that are processed.
            using var reader = new StreamReader(httpRequest.Body);
            var body = await reader.ReadToEndAsync(ct);

            var verification = await verifier.Verify(
                provider,
                httpRequest.Headers[WebhookVerifier.SignatureHeader],
                httpRequest.Headers[WebhookVerifier.TimestampHeader],
                httpRequest.Headers[WebhookVerifier.NonceHeader],
                body,
                ct);

            if (!verification.Accepted)
            {
                // Rejections are logged with the reason but answered with a bare 401:
                // the caller learns nothing about which check failed.
                logger.LogWarning("Rejected webhook from {Provider}: {Reason}", provider, verification.Rejection);
                return Results.Unauthorized();
            }

            ProviderSettlementPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<ProviderSettlementPayload>(body, JsonWeb);
            }
            catch (JsonException)
            {
                payload = null;
            }

            if (payload is null)
            {
                return Results.Problem(title: "Malformed payload.", statusCode: StatusCodes.Status400BadRequest);
            }

            var outcome = await sender.Send(new RecordProviderSettlementCommand(
                provider, payload.ExternalId, payload.CustomerAccountId, payload.Amount, payload.Currency, payload.Direction), ct);

            return Results.Ok(outcome);
        })
        .WithTags("Webhooks")
        .WithSummary("Receive a settlement notification from a payment provider (HMAC-SHA256, timestamp, nonce)")
        .Produces<TransferOutcome>()
        .Produces(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status400BadRequest);

        return app;
    }

    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}
