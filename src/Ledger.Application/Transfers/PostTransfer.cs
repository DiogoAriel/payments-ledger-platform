using FluentValidation;
using Ledger.Application.Abstractions;
using Ledger.Domain;
using Microsoft.Extensions.Logging;

namespace Ledger.Application.Transfers;

/// <summary>
/// Moves money between two accounts of the same currency.
/// <paramref name="Reference"/> is the idempotency key supplied by the caller: the same reference
/// always yields the same result and never posts twice, which is what makes client retries safe.
/// </summary>
public sealed record PostTransferCommand(
    string Reference,
    Guid FromAccountId,
    Guid ToAccountId,
    decimal Amount,
    string Currency,
    string Description) : ICommand<TransferResult>;

public sealed record TransferResult(Guid EntryId, string Reference, DateTimeOffset PostedAt, bool AlreadyPosted);

public sealed class PostTransferValidator : AbstractValidator<PostTransferCommand>
{
    public PostTransferValidator()
    {
        RuleFor(c => c.Reference).NotEmpty().MaximumLength(64);
        RuleFor(c => c.FromAccountId).NotEmpty();
        RuleFor(c => c.ToAccountId).NotEmpty().NotEqual(c => c.FromAccountId)
            .WithMessage("Source and destination accounts must differ.");
        RuleFor(c => c.Amount).GreaterThan(0m);
        RuleFor(c => c.Currency).NotEmpty().Length(3);
        RuleFor(c => c.Description).NotEmpty().MaximumLength(200);
    }
}

public sealed class PostTransferHandler(
    IAccountRepository accounts,
    IJournalRepository journal,
    IOutbox outbox,
    IUnitOfWork unitOfWork,
    IClock clock,
    ILogger<PostTransferHandler> logger) : ICommandHandler<PostTransferCommand, TransferResult>
{
    public async Task<TransferResult> Handle(PostTransferCommand command, CancellationToken cancellationToken)
    {
        // Idempotency first: a retry of an already posted transfer is a success, not a duplicate.
        var existing = await journal.FindByReference(command.Reference, cancellationToken);
        if (existing is not null)
        {
            logger.LogInformation("Transfer {Reference} already posted as entry {EntryId}", command.Reference, existing.Id);
            return new TransferResult(existing.Id.Value, existing.Reference, existing.PostedAt, AlreadyPosted: true);
        }

        var from = await accounts.Find(new AccountId(command.FromAccountId), cancellationToken)
            ?? throw new AccountNotFoundException(new AccountId(command.FromAccountId));
        var to = await accounts.Find(new AccountId(command.ToAccountId), cancellationToken)
            ?? throw new AccountNotFoundException(new AccountId(command.ToAccountId));

        var amount = Money.Of(command.Amount, command.Currency);
        var entry = Posting.Transfer(command.Reference, command.Description, clock.UtcNow, from, to, amount);

        await journal.Add(entry, cancellationToken);
        await outbox.Enqueue(EntryPosted.From(entry), cancellationToken);
        await unitOfWork.Commit(cancellationToken);

        logger.LogInformation("Posted transfer {Reference} of {Amount} from {From} to {To}", entry.Reference, amount, from.Id, to.Id);
        return new TransferResult(entry.Id.Value, entry.Reference, entry.PostedAt, AlreadyPosted: false);
    }
}
