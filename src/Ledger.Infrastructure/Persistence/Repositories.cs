using Ledger.Application.Abstractions;
using Ledger.Domain;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Infrastructure.Persistence;

/// <summary>
/// Loads accounts as domain objects and remembers which records they came from,
/// so the unit of work can write the changed balances back at commit time.
/// </summary>
public sealed class AccountRepository(LedgerDbContext db) : IAccountRepository
{
    private readonly Dictionary<AccountId, (Account Account, AccountRecord Record)> _tracked = new();

    public async Task<Account?> Find(AccountId id, CancellationToken cancellationToken)
    {
        if (_tracked.TryGetValue(id, out var tracked))
        {
            return tracked.Account;
        }

        var record = await db.Accounts.SingleOrDefaultAsync(a => a.Id == id.Value, cancellationToken);
        return record is null ? null : Track(record);
    }

    public async Task<Account?> FindSettlementAccount(string provider, Currency currency, CancellationToken cancellationToken)
    {
        var kind = (int)AccountKind.ProviderSettlement;
        var record = await db.Accounts
            .SingleOrDefaultAsync(a => a.Kind == kind && a.Owner == provider && a.Currency == currency.Code, cancellationToken);

        if (record is null)
        {
            return null;
        }

        return _tracked.TryGetValue(new AccountId(record.Id), out var tracked) ? tracked.Account : Track(record);
    }

    public Task Add(Account account, CancellationToken cancellationToken)
    {
        var record = new AccountRecord { Id = account.Id.Value };
        CopyToRecord(account, record);
        db.Accounts.Add(record);
        _tracked[account.Id] = (account, record);
        return Task.CompletedTask;
    }

    /// <summary>Pushes in-memory balances into the tracked EF records before SaveChanges.</summary>
    internal void FlushTracked()
    {
        foreach (var (account, record) in _tracked.Values)
        {
            CopyToRecord(account, record);
        }
    }

    private Account Track(AccountRecord record)
    {
        var currency = Currency.FromCode(record.Currency);
        var account = Account.Rehydrate(
            new AccountId(record.Id), record.Owner, (AccountKind)record.Kind, currency,
            new Money(record.Balance, currency), record.OpenedAt, record.Version);
        _tracked[account.Id] = (account, record);
        return account;
    }

    private static void CopyToRecord(Account account, AccountRecord record)
    {
        record.Owner = account.Owner;
        record.Kind = (int)account.Kind;
        record.Currency = account.Currency.Code;
        record.Balance = account.Balance.Amount;
        record.OpenedAt = account.OpenedAt;
        record.Version = account.Version;
    }
}

public sealed class JournalRepository(LedgerDbContext db) : IJournalRepository
{
    public async Task<JournalEntry?> FindByReference(string reference, CancellationToken cancellationToken)
    {
        var record = await db.JournalEntries.AsNoTracking()
            .SingleOrDefaultAsync(e => e.Reference == reference, cancellationToken);
        return record is null ? null : ToDomain(record);
    }

    public async Task<IReadOnlyList<JournalEntry>> ListByAccount(AccountId accountId, int limit, CancellationToken cancellationToken)
    {
        var entryIds = db.EntryLines.Where(l => l.AccountId == accountId.Value).Select(l => l.EntryId);
        var records = await db.JournalEntries.AsNoTracking()
            .Where(e => entryIds.Contains(e.Id))
            .OrderByDescending(e => e.PostedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return records.Select(ToDomain).ToList();
    }

    public Task Add(JournalEntry entry, CancellationToken cancellationToken)
    {
        db.JournalEntries.Add(new JournalEntryRecord
        {
            Id = entry.Id.Value,
            Reference = entry.Reference,
            Description = entry.Description,
            PostedAt = entry.PostedAt,
            Lines = entry.Lines.Select(l => new EntryLineRecord
            {
                EntryId = entry.Id.Value,
                AccountId = l.AccountId.Value,
                Amount = l.Money.Amount,
                Currency = l.Money.Currency.Code,
                Direction = (int)l.Direction,
            }).ToList(),
        });
        return Task.CompletedTask;
    }

    private static JournalEntry ToDomain(JournalEntryRecord record) =>
        JournalEntry.Rehydrate(
            new EntryId(record.Id), record.Reference, record.Description, record.PostedAt,
            record.Lines.Select(l => new EntryLine(
                new AccountId(l.AccountId),
                Money.Of(l.Amount, l.Currency),
                (EntryDirection)l.Direction)));
}

/// <summary>
/// One commit per use case. Account balances, the journal entry and the outbox message are saved in
/// a single database transaction, so a consumer can never see an event for an entry that failed.
/// </summary>
public sealed class UnitOfWork(LedgerDbContext db, AccountRepository accounts) : IUnitOfWork
{
    public async Task Commit(CancellationToken cancellationToken)
    {
        accounts.FlushTracked();
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class EfReplayGuard(LedgerDbContext db) : IReplayGuard
{
    public async Task<bool> TryRegister(string provider, string nonce, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var exists = await db.WebhookNonces.AnyAsync(n => n.Provider == provider && n.Nonce == nonce, cancellationToken);
        if (exists)
        {
            return false;
        }

        db.WebhookNonces.Add(new WebhookNonceRecord { Provider = provider, Nonce = nonce, ExpiresAt = expiresAt });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            // Two deliveries raced: the primary key on (provider, nonce) let only one through.
            return false;
        }
    }
}
