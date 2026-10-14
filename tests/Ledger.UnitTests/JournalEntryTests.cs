using FluentAssertions;
using Ledger.Domain;

namespace Ledger.UnitTests;

public class JournalEntryTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Transfer_debits_the_source_and_credits_the_destination()
    {
        var settlement = Account.Open("acme-pay", AccountKind.ProviderSettlement, Currency.BRL, Now);
        var customer = Account.Open("alice", AccountKind.Customer, Currency.BRL, Now);

        var entry = Posting.Transfer("ref-1", "card payment", Now, settlement, customer, Money.Of(100m, "BRL"));

        entry.Lines.Should().HaveCount(2);
        settlement.Balance.Should().Be(Money.Of(-100m, "BRL"));
        customer.Balance.Should().Be(Money.Of(100m, "BRL"));
        customer.Version.Should().Be(1);
    }

    [Fact]
    public void Customer_accounts_cannot_go_negative()
    {
        var alice = Account.Open("alice", AccountKind.Customer, Currency.BRL, Now);
        var bob = Account.Open("bob", AccountKind.Customer, Currency.BRL, Now);

        var act = () => Posting.Transfer("ref-2", "payout", Now, alice, bob, Money.Of(1m, "BRL"));

        act.Should().Throw<InsufficientFundsException>();
        alice.Balance.IsZero.Should().BeTrue("a rejected entry must not touch any balance");
        bob.Balance.IsZero.Should().BeTrue();
    }

    [Fact]
    public void Entry_must_balance()
    {
        var a = AccountId.New();
        var b = AccountId.New();

        var act = () => JournalEntry.Create("ref-3", "broken", Now, new[]
        {
            EntryLine.Debit(a, Money.Of(10m, "BRL")),
            EntryLine.Credit(b, Money.Of(9m, "BRL")),
        });

        act.Should().Throw<UnbalancedEntryException>();
    }

    [Fact]
    public void Entry_lines_must_share_one_currency()
    {
        var act = () => JournalEntry.Create("ref-4", "mixed", Now, new[]
        {
            EntryLine.Debit(AccountId.New(), Money.Of(10m, "BRL")),
            EntryLine.Credit(AccountId.New(), Money.Of(10m, "USD")),
        });

        act.Should().Throw<LedgerException>().WithMessage("*one currency*");
    }

    [Fact]
    public void Transfer_to_the_same_account_is_rejected()
    {
        var alice = Account.Open("alice", AccountKind.Customer, Currency.BRL, Now);

        var act = () => Posting.Transfer("ref-5", "self", Now, alice, alice, Money.Of(1m, "BRL"));

        act.Should().Throw<LedgerException>().WithMessage("*must differ*");
    }

    [Fact]
    public void Multi_line_entry_splits_a_fee_from_a_payment()
    {
        var settlement = Account.Open("acme-pay", AccountKind.ProviderSettlement, Currency.BRL, Now);
        var customer = Account.Open("alice", AccountKind.Customer, Currency.BRL, Now);
        var fees = Account.Open("platform", AccountKind.PlatformFees, Currency.BRL, Now);
        var accounts = new Dictionary<AccountId, Account> { [settlement.Id] = settlement, [customer.Id] = customer, [fees.Id] = fees };

        Posting.Post("ref-6", "payment with fee", Now, accounts,
            EntryLine.Debit(settlement.Id, Money.Of(100m, "BRL")),
            EntryLine.Credit(customer.Id, Money.Of(97.50m, "BRL")),
            EntryLine.Credit(fees.Id, Money.Of(2.50m, "BRL")));

        customer.Balance.Should().Be(Money.Of(97.50m, "BRL"));
        fees.Balance.Should().Be(Money.Of(2.50m, "BRL"));
        settlement.Balance.Should().Be(Money.Of(-100m, "BRL"));
    }
}
