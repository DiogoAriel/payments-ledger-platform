using Ledger.Domain;

namespace Ledger.Application.Abstractions;

/// <summary>
/// Ports are the interfaces the application needs from the outside world.
/// The infrastructure project implements them; tests replace them with in-memory fakes.
/// </summary>
public interface IAccountRepository
{
    Task<Account?> Find(AccountId id, CancellationToken cancellationToken);
    Task<Account?> FindSettlementAccount(string provider, Currency currency, CancellationToken cancellationToken);
    Task Add(Account account, CancellationToken cancellationToken);
}

public interface IJournalRepository
{
    Task<JournalEntry?> FindByReference(string reference, CancellationToken cancellationToken);
    Task<IReadOnlyList<JournalEntry>> ListByAccount(AccountId accountId, int limit, CancellationToken cancellationToken);
    Task Add(JournalEntry entry, CancellationToken cancellationToken);
}

/// <summary>Stores integration events in the same transaction as the state change (transactional outbox).</summary>
public interface IOutbox
{
    Task Enqueue(IIntegrationEvent integrationEvent, CancellationToken cancellationToken);
}

/// <summary>
/// Remembers webhook nonces so a captured request cannot be replayed.
/// Returns false when the nonce was already seen.
/// </summary>
public interface IReplayGuard
{
    Task<bool> TryRegister(string provider, string nonce, DateTimeOffset expiresAt, CancellationToken cancellationToken);
}

public interface IUnitOfWork
{
    Task Commit(CancellationToken cancellationToken);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
