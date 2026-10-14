namespace Ledger.Domain;

public enum AccountKind
{
    /// <summary>Money held on behalf of a customer.</summary>
    Customer = 1,
    /// <summary>Money owed to or by a payment provider (card acquirer, PIX participant).</summary>
    ProviderSettlement = 2,
    /// <summary>Fees the platform has earned.</summary>
    PlatformFees = 3,
}

/// <summary>
/// An account is a bucket of money in one currency.
/// Its balance only changes through <see cref="JournalEntry"/> lines, never by direct assignment,
/// so every change in balance has a matching, auditable entry.
/// </summary>
public sealed class Account
{
    public AccountId Id { get; private set; }
    public string Owner { get; private set; }
    public AccountKind Kind { get; private set; }
    public Currency Currency { get; private set; }
    public Money Balance { get; private set; }
    public DateTimeOffset OpenedAt { get; private set; }

    /// <summary>Optimistic concurrency token. Two transfers touching the same account cannot both win.</summary>
    public long Version { get; private set; }

    private Account(AccountId id, string owner, AccountKind kind, Currency currency, DateTimeOffset openedAt)
    {
        Id = id;
        Owner = owner;
        Kind = kind;
        Currency = currency;
        Balance = Money.Zero(currency);
        OpenedAt = openedAt;
    }

    public static Account Open(string owner, AccountKind kind, Currency currency, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        return new Account(AccountId.New(), owner.Trim(), kind, currency, now);
    }

    /// <summary>
    /// Rebuilds an account from storage. Only the persistence layer should call this;
    /// business code opens accounts with <see cref="Open"/> and changes balances through entries.
    /// </summary>
    public static Account Rehydrate(AccountId id, string owner, AccountKind kind, Currency currency, Money balance, DateTimeOffset openedAt, long version)
    {
        if (balance.Currency != currency)
        {
            throw new CurrencyMismatchException(currency, balance.Currency);
        }

        return new Account(id, owner, kind, currency, openedAt) { Balance = balance, Version = version };
    }

    /// <summary>
    /// Settlement and fee accounts are allowed to go negative because they represent what is owed,
    /// customer accounts are not. The rule lives here so no handler can forget it.
    /// </summary>
    public bool AllowsOverdraft => Kind != AccountKind.Customer;

    internal void Apply(EntryLine line)
    {
        if (line.AccountId != Id)
        {
            throw new LedgerException($"Line for account {line.AccountId} applied to account {Id}.");
        }

        if (line.Money.Currency != Currency)
        {
            throw new CurrencyMismatchException(Currency, line.Money.Currency);
        }

        var next = line.Direction == EntryDirection.Credit ? Balance + line.Money : Balance - line.Money;

        if (!AllowsOverdraft && next.Amount < 0m)
        {
            throw new InsufficientFundsException(Id, Balance, line.Money);
        }

        Balance = next;
        Version++;
    }
}
