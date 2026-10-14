# payments-ledger-platform

Double-entry payments ledger in .NET 8. Read this file before touching anything.

## What this is

- `src/Ledger.Domain`: money, accounts, journal entries, posting rules. No framework references.
- `src/Ledger.Application`: commands, queries, validators, ports (`Abstractions/Ports.cs`), webhook verification.
- `src/Ledger.Infrastructure`: EF Core (PostgreSQL, SQLite for tests), transactional outbox, Kafka publisher.
- `src/Ledger.Api`: minimal APIs, error mapping (`ErrorHandling.cs`), OpenTelemetry, health checks.
- `tests/`: unit tests on the domain and handlers; integration tests on the real HTTP pipeline over SQLite.
- `deploy/`: Helm chart, Terraform for AWS (ECS + RDS), OpenTelemetry collector config.
- `docs/`: architecture and ADRs. Every non-obvious decision has an ADR; add one when you make another.

## Commands

```bash
dotnet build PaymentsLedger.sln -c Release      # warnings are errors; vulnerable packages fail the build
dotnet test  PaymentsLedger.sln -c Release      # 36 tests, no Docker needed
dotnet run --project src/Ledger.Api             # SQLite + Swagger at https://localhost:<port>/swagger
dotnet ef migrations add <Name> --project src/Ledger.Infrastructure --startup-project src/Ledger.Api --output-dir Persistence/Migrations
```

## Rules that are not negotiable

1. **Balances change only through journal entries.** Never add a setter or an UPDATE that touches `accounts.balance` outside `Posting`.
2. **Fail closed.** Any doubt in a webhook (signature, timestamp, nonce, provider) is a 401. Any doubt in a posting is an exception, before anything is persisted.
3. **Idempotency is the reference.** A transfer or settlement with a known reference returns the original result; it never posts twice. The unique index on `journal_entries.reference` is the last line of defence.
4. **One transaction per use case.** Entry, balances and outbox row are saved together in `UnitOfWork.Commit`.
5. **Domain stays clean.** No EF attributes, no HTTP types, no logging in `Ledger.Domain`.
6. **Tests before behaviour.** A new rule gets a unit test on the domain or handler, and an integration test if it is visible over HTTP.

## How to work in this repository

- Start by writing what you intend to change in `_local/history/<date>.md` (git-ignored). Append the outcome when done. Keep facts and decisions worth remembering in `_local/memory.md`.
- Spec first: for anything bigger than a bug fix, write the plan (files, invariants, tests) before code and ask for approval.
- Run build and tests before claiming anything works. Paste the test summary, not an interpretation of it.
- Review your own diff adversarially before presenting it: use the `adversarial-review` skill.
- For a production incident or a failing test you do not understand, use the `incident-analysis` skill: reproduce, measure, then change.

## Git

- Conventional commits (`feat:`, `fix:`, `test:`, `docs:`, `chore:`), imperative mood, no trailers, no tool attribution of any kind in messages, comments or pull requests.
- Never commit `_local/`, local databases, `.env` files or secrets. The pipelines run gitleaks and will reject them.
- Branch per change; `main` is always green.

## Confidentiality

This repository is a public reference. Do not add client names, real endpoints, real secrets or data from any engagement. Example provider name is `acme-pay`.
