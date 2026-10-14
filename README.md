# payments-ledger-platform

[![ci](https://github.com/DiogoAriel/payments-ledger-platform/actions/workflows/ci.yml/badge.svg)](https://github.com/DiogoAriel/payments-ledger-platform/actions/workflows/ci.yml)
[![security](https://github.com/DiogoAriel/payments-ledger-platform/actions/workflows/security.yml/badge.svg)](https://github.com/DiogoAriel/payments-ledger-platform/actions/workflows/security.yml)
![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)
![license](https://img.shields.io/badge/license-MIT-green)

A double-entry **payments ledger** in .NET 8: accounts, transfers, provider settlements over
authenticated webhooks, and integration events. It is a reference implementation of how I build
financial backends: domain rules first, fail-closed on every doubt, idempotent everywhere, observable
by default, and a security pipeline that fails the build.

It runs with no Docker (SQLite in memory for tests and local runs) and ships with everything a
production deployment needs: PostgreSQL migrations, Kafka outbox, Helm chart, Terraform for AWS,
GitHub Actions and GitLab CI pipelines.

## What it does

| Capability | Where | Guarantee |
|---|---|---|
| Open accounts (customer, provider settlement, platform fees) | `POST /api/v1/accounts` | one currency per account |
| Transfer between accounts | `POST /api/v1/transfers` + `Idempotency-Key` | balanced entry, no overdraft for customers, same key never posts twice |
| Record a provider settlement (pay-in / pay-out) | `POST /webhooks/providers/{provider}` | HMAC-SHA256 + timestamp window + nonce; redelivery is idempotent by external id |
| Read balance and statement | `GET /api/v1/accounts/{id}`, `/statement` | statement derived from journal lines |
| Publish `ledger.entry.posted` / `ledger.account.opened` | transactional outbox → Kafka | event only after the entry committed, at-least-once |

## Quick start

```bash
git clone https://github.com/DiogoAriel/payments-ledger-platform.git
cd payments-ledger-platform
dotnet test PaymentsLedger.sln          # 36 tests, ~5 s, no database to install
dotnet run --project src/Ledger.Api     # Development profile: SQLite file + Swagger UI
```

Then open `https://localhost:<port>/swagger` (the port is printed at start-up).

Full local stack (API, PostgreSQL, Redpanda, OpenTelemetry collector, Jaeger):

```bash
docker compose up --build
# API: http://localhost:8080/swagger   Traces: http://localhost:16686
```

### Walkthrough with curl

```bash
# 1. a settlement account for the provider and a customer account
SETTLE=$(curl -s -X POST localhost:8080/api/v1/accounts -H 'content-type: application/json' \
  -d '{"owner":"acme-pay","kind":"ProviderSettlement","currency":"BRL"}' | jq -r .id)
ALICE=$(curl -s -X POST localhost:8080/api/v1/accounts -H 'content-type: application/json' \
  -d '{"owner":"alice","kind":"Customer","currency":"BRL"}' | jq -r .id)

# 2. a transfer; send it twice with the same key and watch the second answer 200 instead of 201
curl -i -X POST localhost:8080/api/v1/transfers -H 'content-type: application/json' \
  -H 'Idempotency-Key: order-1001' \
  -d "{\"fromAccountId\":\"$SETTLE\",\"toAccountId\":\"$ALICE\",\"amount\":150.25,\"currency\":\"BRL\",\"description\":\"top-up\"}"

# 3. a signed webhook from the provider (secret from appsettings.Development.json)
BODY="{\"externalId\":\"evt-42\",\"customerAccountId\":\"$ALICE\",\"amount\":80,\"currency\":\"BRL\",\"direction\":\"PayIn\"}"
TS=$(date +%s); NONCE=$(uuidgen)
SIG=$(printf '%s.%s' "$TS" "$BODY" | openssl dgst -sha256 -hmac 'dev-only-secret-change-me' | awk '{print $2}')
curl -i -X POST localhost:8080/webhooks/providers/acme-pay -H 'content-type: application/json' \
  -H "X-Timestamp: $TS" -H "X-Nonce: $NONCE" -H "X-Signature: $SIG" -d "$BODY"

# 4. balance and statement
curl -s localhost:8080/api/v1/accounts/$ALICE/statement | jq
```

## How it is built

```
src/
├── Ledger.Domain          Money, Currency, Account, JournalEntry, Posting. No framework references.
├── Ledger.Application     Commands, queries, validators, ports, webhook verifier. Small hand-written dispatcher.
├── Ledger.Infrastructure  EF Core (PostgreSQL / SQLite), repositories, unit of work, outbox relay, Kafka publisher.
└── Ledger.Api             Minimal APIs, problem-details error mapping, JWT bearer (optional), OpenTelemetry, health checks.
tests/
├── Ledger.UnitTests       Domain rules and handlers with in-memory ports (24 tests).
└── Ledger.IntegrationTests Real HTTP pipeline over SQLite: idempotency, validation, webhooks, statements (12 tests).
deploy/
├── helm/payments-ledger   Chart with probes, HPA, PDB, non-root security context, secrets from an existing Secret.
├── terraform/aws          ECS Fargate + ALB + RDS PostgreSQL + Secrets Manager, least-privilege roles.
└── otel/collector.yaml    Collector config for the local stack.
docs/
├── architecture.md        Diagrams, layers, data model, invariants and where each one is enforced.
└── adr/                   Why double-entry, why an outbox, why fail-closed webhooks, why separate persistence records.
```

### The rules the code enforces

1. **Balances only change through journal entries.** `Posting` is the single writer; the entry is
   validated (balanced, one currency, positive lines) before any balance is touched, so a rejected
   entry leaves nothing behind.
2. **Customer accounts cannot go negative; settlement and fee accounts can**, because they represent
   what is owed. The rule is on the `Account`, not in a handler.
3. **Idempotency is the reference.** Transfers use the client's `Idempotency-Key`; settlements use
   the provider's `externalId`. A known reference returns the original result (`200` instead of
   `201`). The unique index on `journal_entries.reference` is the last line of defence.
4. **One transaction per use case.** Balances, entry and outbox row are saved in one `SaveChanges`.
5. **Optimistic concurrency.** Two transfers racing on the same account: one wins, the other gets a
   `409` and retries with the same key, which is then a harmless replay.
6. **Fail-closed webhooks.** Unknown provider, missing header, stale timestamp, bad signature or
   reused nonce: `401`, no detail to the caller, reason in the log. The nonce is registered only after
   the signature is valid, so unsigned requests cannot burn legitimate nonces.

### Errors are a contract

| Situation | Status | Example |
|---|---|---|
| Invalid request shape | 400 | amount ≤ 0, same source and destination, missing `Idempotency-Key` |
| Account not found | 404 | |
| Business rule rejected it | 422 | insufficient funds, unsupported currency, unbalanced entry |
| Concurrent update | 409 | retry with the same key |
| Webhook not authenticated | 401 | |

All errors are RFC 7807 problem details with a `traceId` that matches the trace in your collector.

### Observability

Serilog writes compact JSON logs, one line per event, correlated by trace id. OpenTelemetry exports
traces and runtime/HTTP metrics over OTLP to whatever backend you point `OTEL_EXPORTER_OTLP_ENDPOINT`
at. `/health/live` and `/health/ready` (database check) feed the Kubernetes probes.

### Security pipeline

`.github/workflows/security.yml` (and the equivalent `.gitlab-ci.yml`) run on every change:

| Control | Tool | Fails the build on |
|---|---|---|
| Secret detection | gitleaks | any committed secret |
| SAST | semgrep (`p/csharp`, `p/owasp-top-ten`, `p/secrets`) | findings |
| Dependency vulnerabilities | NuGet audit (build time) + trivy fs | HIGH/CRITICAL, fixable |
| SBOM | syft (CycloneDX) | — (artifact) |
| Infrastructure as code | checkov on Terraform, Helm, Dockerfile | failed checks |
| Container image | trivy image, SARIF to code scanning | HIGH/CRITICAL, fixable |

The build itself treats warnings as errors and runs the .NET analyzers at the recommended level.
During development this pipeline caught a vulnerable `OpenTelemetry.Exporter.OpenTelemetryProtocol`
version before the first commit, which is exactly what it is there for.

## Configuration

| Key | Purpose | Default |
|---|---|---|
| `Database:Provider` | `Postgres` or `Sqlite` | `Postgres` (`Sqlite` in Development) |
| `Database:AutoMigrate` | run migrations (Postgres) or create schema (SQLite) at start-up | `false` (`true` in Development) |
| `ConnectionStrings:Ledger` | database connection string | local PostgreSQL |
| `Messaging:Kafka:BootstrapServers` | Kafka brokers; empty logs events instead of publishing | empty |
| `Auth:Authority` / `Auth:Audience` | OIDC authority for JWT bearer; empty disables auth (local only) | empty |
| `Webhooks:Providers:<name>:Secret` | HMAC secret per provider | none |
| `Webhooks:Providers:<name>:ToleranceSeconds` | accepted clock skew | 300 |

Secrets never live in files: in Kubernetes they come from an existing `Secret`, in AWS from Secrets
Manager (see `deploy/`).

## Working on it

```bash
dotnet build PaymentsLedger.sln -c Release
dotnet test  PaymentsLedger.sln -c Release
dotnet ef migrations add <Name> --project src/Ledger.Infrastructure --startup-project src/Ledger.Api --output-dir Persistence/Migrations
helm lint deploy/helm/payments-ledger
terraform -chdir=deploy/terraform/aws validate
```

`CLAUDE.md` describes the working agreement for this repository (rules, review checklist, journal in
`_local/`). The `.claude/skills` folder holds the adversarial-review and incident-analysis procedures
used to review every change.

## Roadmap

- Multi-currency entries with FX legs and a rate source.
- Scheduled pruning of expired webhook nonces and published outbox rows.
- Read model for statements (CQRS query side) when volume requires it.
- Contract tests for the Kafka events (schema registry).

## License

MIT. See `LICENSE`.
