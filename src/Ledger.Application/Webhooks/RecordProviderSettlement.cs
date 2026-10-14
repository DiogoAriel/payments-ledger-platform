using FluentValidation;
using Ledger.Application.Abstractions;
using Ledger.Domain;
using Microsoft.Extensions.Logging;

namespace Ledger.Application.Webhooks;

public enum SettlementDirection
{
    /// <summary>Money came in from the provider to the customer (a card payment, a received PIX).</summary>
    PayIn = 1,
    /// <summary>Money went out from the customer through the provider (a payout, a sent PIX).</summary>
    PayOut = 2,
}

/// <summary>
/// Records a settlement notified by a payment provider through a webhook.
/// The provider's external id becomes the ledger reference, so a redelivered webhook is idempotent.
/// </summary>
public sealed record RecordProviderSettlementCommand(
    string Provider,
    string ExternalId,
    Guid CustomerAccountId,
    decimal Amount,
    string Currency,
    SettlementDirection Direction) : ICommand<TransferOutcome>;

public sealed record TransferOutcome(Guid EntryId, string Reference, bool AlreadyPosted);

public sealed class RecordProviderSettlementValidator : AbstractValidator<RecordProviderSettlementCommand>
{
    public RecordProviderSettlementValidator()
    {
        RuleFor(c => c.Provider).NotEmpty().MaximumLength(40);
        RuleFor(c => c.ExternalId).NotEmpty().MaximumLength(64);
        RuleFor(c => c.CustomerAccountId).NotEmpty();
        RuleFor(c => c.Amount).GreaterThan(0m);
        RuleFor(c => c.Currency).NotEmpty().Length(3);
        RuleFor(c => c.Direction).IsInEnum();
    }
}

public sealed class RecordProviderSettlementHandler(
    IAccountRepository accounts,
    IJournalRepository journal,
    IOutbox outbox,
    IUnitOfWork unitOfWork,
    IClock clock,
    ILogger<RecordProviderSettlementHandler> logger) : ICommandHandler<RecordProviderSettlementCommand, TransferOutcome>
{
    public async Task<TransferOutcome> Handle(RecordProviderSettlementCommand command, CancellationToken cancellationToken)
    {
        var reference = $"{command.Provider}:{command.ExternalId}";

        var existing = await journal.FindByReference(reference, cancellationToken);
        if (existing is not null)
        {
            return new TransferOutcome(existing.Id.Value, reference, AlreadyPosted: true);
        }

        var currency = Currency.FromCode(command.Currency);
        var customer = await accounts.Find(new AccountId(command.CustomerAccountId), cancellationToken)
            ?? throw new AccountNotFoundException(new AccountId(command.CustomerAccountId));

        // Each provider has one settlement account per currency. It is created on first use so that
        // onboarding a provider is configuration, not a migration.
        var settlement = await accounts.FindSettlementAccount(command.Provider, currency, cancellationToken);
        if (settlement is null)
        {
            settlement = Account.Open(command.Provider, AccountKind.ProviderSettlement, currency, clock.UtcNow);
            await accounts.Add(settlement, cancellationToken);
        }

        var amount = new Money(command.Amount, currency);
        var (from, to) = command.Direction == SettlementDirection.PayIn
            ? (settlement, customer)
            : (customer, settlement);

        var description = $"{command.Direction} via {command.Provider} ({command.ExternalId})";
        var entry = Posting.Transfer(reference, description, clock.UtcNow, from, to, amount);

        await journal.Add(entry, cancellationToken);
        await outbox.Enqueue(EntryPosted.From(entry), cancellationToken);
        await unitOfWork.Commit(cancellationToken);

        logger.LogInformation("Recorded {Direction} settlement {Reference} of {Amount}", command.Direction, reference, amount);
        return new TransferOutcome(entry.Id.Value, reference, AlreadyPosted: false);
    }
}
