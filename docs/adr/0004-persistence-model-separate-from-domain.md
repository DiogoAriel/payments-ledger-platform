# ADR 0004: Persistence records separate from domain objects

**Status:** accepted · **Date:** 2026-10-14

## Context

EF Core can map domain classes directly, but doing so pushes persistence concerns into the domain:
parameterless constructors, public setters, value objects shaped to fit the mapper, provider quirks
(SQLite cannot order by `DateTimeOffset`, decimals need explicit precision).

## Decision

`Ledger.Infrastructure.Persistence` has its own records (`AccountRecord`, `JournalEntryRecord`,
`EntryLineRecord`, ...) that mirror the tables. Repositories translate between records and domain
objects; domain types expose `Rehydrate` factories for that purpose only. The unit of work pushes
the in-memory balances of every loaded account back to its record before `SaveChanges`.

## Consequences

- The domain keeps private setters, value objects (`Money`, `Currency`) and strongly typed ids.
- Provider-specific workarounds (the SQLite `DateTimeOffset` converter) live in the `DbContext`,
  where they belong.
- Each aggregate needs a small mapping; this is the cost, and it is paid once per aggregate.
- Tests of the domain need no database; tests of the mapping run against SQLite in memory.
