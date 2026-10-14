namespace Ledger.Domain;

/// <summary>
/// Domain service that posts a journal entry against the accounts it touches.
/// It is the only place where balances change, and it is all-or-nothing:
/// if any line is rejected (insufficient funds, wrong currency) no balance has been modified,
/// because the entry is validated first and the accounts are applied in memory before the
/// unit of work persists them.
/// </summary>
public static class Posting
{
    public static JournalEntry Post(
        string reference,
        string description,
        DateTimeOffset now,
        IReadOnlyDictionary<AccountId, Account> accounts,
        params EntryLine[] lines)
    {
        var entry = JournalEntry.Create(reference, description, now, lines);

        foreach (var line in entry.Lines)
        {
            if (!accounts.TryGetValue(line.AccountId, out var account))
            {
                throw new AccountNotFoundException(line.AccountId);
            }

            account.Apply(line);
        }

        return entry;
    }

    /// <summary>A transfer is the simplest entry: one debit on the source, one credit on the destination.</summary>
    public static JournalEntry Transfer(
        string reference,
        string description,
        DateTimeOffset now,
        Account from,
        Account to,
        Money amount)
    {
        if (from.Id == to.Id)
        {
            throw new LedgerException("Source and destination accounts must differ.");
        }

        var accounts = new Dictionary<AccountId, Account> { [from.Id] = from, [to.Id] = to };
        return Post(reference, description, now, accounts,
            EntryLine.Debit(from.Id, amount),
            EntryLine.Credit(to.Id, amount));
    }
}
