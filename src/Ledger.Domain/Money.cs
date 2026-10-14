namespace Ledger.Domain;

/// <summary>
/// A monetary amount in a specific currency.
/// Money is a value object: two instances with the same amount and currency are equal,
/// and arithmetic between different currencies is a domain error, never a silent conversion.
/// </summary>
public readonly record struct Money
{
    public decimal Amount { get; }
    public Currency Currency { get; }

    public Money(decimal amount, Currency currency)
    {
        if (decimal.Round(amount, currency.MinorUnits) != amount)
        {
            throw new LedgerException($"Amount {amount} has more decimal places than {currency.Code} allows ({currency.MinorUnits}).");
        }

        Amount = amount;
        Currency = currency;
    }

    public static Money Of(decimal amount, string currencyCode) => new(amount, Currency.FromCode(currencyCode));

    public static Money Zero(Currency currency) => new(0m, currency);

    public bool IsPositive => Amount > 0m;
    public bool IsZero => Amount == 0m;

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount - right.Amount, left.Currency);
    }

    public static bool operator <(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return left.Amount < right.Amount;
    }

    public static bool operator >(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return left.Amount > right.Amount;
    }

    public static bool operator <=(Money left, Money right) => !(left > right);
    public static bool operator >=(Money left, Money right) => !(left < right);

    public Money Add(Money other) => this + other;
    public Money Subtract(Money other) => this - other;

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (left.Currency != right.Currency)
        {
            throw new CurrencyMismatchException(left.Currency, right.Currency);
        }
    }

    public override string ToString() => $"{Amount.ToString($"F{Currency.MinorUnits}", System.Globalization.CultureInfo.InvariantCulture)} {Currency.Code}";
}

/// <summary>
/// ISO 4217 currency with its number of minor units (decimal places).
/// Only the currencies the platform settles are listed; adding one is a deliberate change.
/// </summary>
public readonly record struct Currency
{
    public string Code { get; }
    public int MinorUnits { get; }

    private Currency(string code, int minorUnits)
    {
        Code = code;
        MinorUnits = minorUnits;
    }

    public static readonly Currency BRL = new("BRL", 2);
    public static readonly Currency USD = new("USD", 2);
    public static readonly Currency EUR = new("EUR", 2);

    private static readonly Dictionary<string, Currency> Known = new(StringComparer.Ordinal)
    {
        [BRL.Code] = BRL,
        [USD.Code] = USD,
        [EUR.Code] = EUR,
    };

    public static Currency FromCode(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return Known.TryGetValue(code.ToUpperInvariant(), out var currency)
            ? currency
            : throw new LedgerException($"Currency '{code}' is not supported.");
    }

    public override string ToString() => Code;
}
