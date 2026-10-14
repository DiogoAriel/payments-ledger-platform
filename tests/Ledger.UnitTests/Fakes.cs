using Ledger.Application.Abstractions;
using Ledger.Domain;

namespace Ledger.UnitTests;

/// <summary>In-memory ports. They behave like the real adapters without a database.</summary>
internal sealed class InMemoryAccountRepository : IAccountRepository
{
    public Dictionary<AccountId, Account> Accounts { get; } = new();

    public Task<Account?> Find(AccountId id, CancellationToken cancellationToken) =>
        Task.FromResult(Accounts.GetValueOrDefault(id));

    public Task<Account?> FindSettlementAccount(string provider, Currency currency, CancellationToken cancellationToken) =>
        Task.FromResult(Accounts.Values.FirstOrDefault(a =>
            a.Kind == AccountKind.ProviderSettlement && a.Owner == provider && a.Currency == currency));

    public Task Add(Account account, CancellationToken cancellationToken)
    {
        Accounts[account.Id] = account;
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryJournalRepository : IJournalRepository
{
    public List<JournalEntry> Entries { get; } = new();

    public Task<JournalEntry?> FindByReference(string reference, CancellationToken cancellationToken) =>
        Task.FromResult(Entries.FirstOrDefault(e => e.Reference == reference));

    public Task<IReadOnlyList<JournalEntry>> ListByAccount(AccountId accountId, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<JournalEntry>>(Entries.Where(e => e.Lines.Any(l => l.AccountId == accountId)).Take(limit).ToList());

    public Task Add(JournalEntry entry, CancellationToken cancellationToken)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryOutbox : IOutbox
{
    public List<IIntegrationEvent> Events { get; } = new();

    public Task Enqueue(IIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        Events.Add(integrationEvent);
        return Task.CompletedTask;
    }
}

internal sealed class CountingUnitOfWork : IUnitOfWork
{
    public int Commits { get; private set; }

    public Task Commit(CancellationToken cancellationToken)
    {
        Commits++;
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryReplayGuard : IReplayGuard
{
    private readonly HashSet<(string, string)> _seen = new();

    public Task<bool> TryRegister(string provider, string nonce, DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
        Task.FromResult(_seen.Add((provider, nonce)));
}

internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}
