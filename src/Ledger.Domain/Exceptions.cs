namespace Ledger.Domain;

/// <summary>Base class for every rule the ledger refuses to break. Callers map it to HTTP 422.</summary>
public class LedgerException : Exception
{
    public LedgerException(string message) : base(message) { }
    public LedgerException(string message, Exception inner) : base(message, inner) { }
    public LedgerException() { }
}

public sealed class CurrencyMismatchException : LedgerException
{
    public CurrencyMismatchException(Currency left, Currency right)
        : base($"Cannot operate on {left.Code} and {right.Code} together.") { }
    public CurrencyMismatchException() { }
    public CurrencyMismatchException(string message) : base(message) { }
    public CurrencyMismatchException(string message, Exception inner) : base(message, inner) { }
}

public sealed class InsufficientFundsException : LedgerException
{
    public InsufficientFundsException(AccountId account, Money balance, Money requested)
        : base($"Account {account} has {balance} and cannot cover {requested}.") { }
    public InsufficientFundsException() { }
    public InsufficientFundsException(string message) : base(message) { }
    public InsufficientFundsException(string message, Exception inner) : base(message, inner) { }
}

public sealed class UnbalancedEntryException : LedgerException
{
    public UnbalancedEntryException(Money debits, Money credits)
        : base($"Journal entry is unbalanced: debits {debits} do not equal credits {credits}.") { }
    public UnbalancedEntryException() { }
    public UnbalancedEntryException(string message) : base(message) { }
    public UnbalancedEntryException(string message, Exception inner) : base(message, inner) { }
}

public sealed class AccountNotFoundException : LedgerException
{
    public AccountNotFoundException(AccountId id) : base($"Account {id} does not exist.") { }
    public AccountNotFoundException() { }
    public AccountNotFoundException(string message) : base(message) { }
    public AccountNotFoundException(string message, Exception inner) : base(message, inner) { }
}
