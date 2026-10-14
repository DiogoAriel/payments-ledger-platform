namespace Ledger.Domain;

public enum EntryDirection
{
    Debit = 1,
    Credit = 2,
}

/// <summary>One side of a journal entry: an amount leaving (debit) or entering (credit) an account.</summary>
public sealed record EntryLine(AccountId AccountId, Money Money, EntryDirection Direction)
{
    public static EntryLine Debit(AccountId account, Money money) => new(account, money, EntryDirection.Debit);
    public static EntryLine Credit(AccountId account, Money money) => new(account, money, EntryDirection.Credit);
}

/// <summary>
/// A journal entry is the unit of change in a double-entry ledger.
/// It is immutable once posted and it is always balanced: the sum of debits equals the sum of credits.
/// The <see cref="Reference"/> is the caller's idempotency key, so the same business event
/// (a transfer request, a provider settlement) can never be posted twice.
/// </summary>
public sealed class JournalEntry
{
    private readonly List<EntryLine> _lines = new();

    public EntryId Id { get; private set; }
    public string Reference { get; private set; }
    public string Description { get; private set; }
    public DateTimeOffset PostedAt { get; private set; }
    public IReadOnlyList<EntryLine> Lines => _lines;

    private JournalEntry(EntryId id, string reference, string description, DateTimeOffset postedAt)
    {
        Id = id;
        Reference = reference;
        Description = description;
        PostedAt = postedAt;
    }

    /// <summary>Rebuilds a posted entry from storage. Persistence only; business code uses <see cref="Create"/>.</summary>
    public static JournalEntry Rehydrate(EntryId id, string reference, string description, DateTimeOffset postedAt, IEnumerable<EntryLine> lines)
    {
        var entry = new JournalEntry(id, reference, description, postedAt);
        entry._lines.AddRange(lines);
        return entry;
    }

    /// <summary>
    /// Builds and validates an entry. Throws instead of returning a partially valid object:
    /// an unbalanced entry must never exist, not even in memory.
    /// </summary>
    public static JournalEntry Create(string reference, string description, DateTimeOffset postedAt, IEnumerable<EntryLine> lines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        var entry = new JournalEntry(EntryId.New(), reference.Trim(), description.Trim(), postedAt);
        entry._lines.AddRange(lines);

        if (entry._lines.Count < 2)
        {
            throw new LedgerException("A journal entry needs at least one debit and one credit line.");
        }

        if (entry._lines.Any(l => !l.Money.IsPositive))
        {
            throw new LedgerException("Every line must carry a positive amount.");
        }

        var currency = entry._lines[0].Money.Currency;
        if (entry._lines.Any(l => l.Money.Currency != currency))
        {
            throw new LedgerException("All lines of a journal entry must share one currency.");
        }

        var debits = entry._lines.Where(l => l.Direction == EntryDirection.Debit)
            .Aggregate(Money.Zero(currency), (acc, l) => acc + l.Money);
        var credits = entry._lines.Where(l => l.Direction == EntryDirection.Credit)
            .Aggregate(Money.Zero(currency), (acc, l) => acc + l.Money);

        if (debits != credits)
        {
            throw new UnbalancedEntryException(debits, credits);
        }

        return entry;
    }
}
