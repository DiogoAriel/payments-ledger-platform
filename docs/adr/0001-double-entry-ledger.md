# ADR 0001: Double-entry journal instead of a balance column that gets updated

**Status:** accepted · **Date:** 2026-10-14

## Context

The simplest payments model is a `balance` column that endpoints increment and decrement. It is also
the model behind most reconciliation nightmares: when a balance is wrong there is no record of how it
got there, and concurrent updates silently lose money.

## Decision

Every movement is a `JournalEntry` with at least one debit and one credit line that sum to the same
amount. Balances are derived by applying entries; the `balance` column is a cache of that derivation,
updated only by `Posting`, under an optimistic concurrency token.

## Consequences

- The journal is an audit trail by construction: any balance can be recomputed from its lines.
- Fees, FX spreads and splits are just entries with more lines; no special cases.
- Writes are slightly heavier (entry + lines + balance) than a single UPDATE. For a ledger this is the
  right trade.
- Reports that need historical balances read the journal, not the accounts table.
