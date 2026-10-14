# Architecture

The platform records money movements for a payments product: customer balances, settlements with
payment providers and platform fees. It is a reference implementation, small enough to read in an
afternoon, built the way a production ledger has to be built.

## Shape

```mermaid
flowchart LR
    Client -->|HTTP + Idempotency-Key| API
    Provider[Payment provider] -->|HMAC webhook| API
    API --> App[Application\ncommands / queries]
    App --> Domain[Domain\nAccount · JournalEntry · Posting]
    App --> Ports[(Ports)]
    Ports --> Infra[Infrastructure\nEF Core · Outbox · Kafka]
    Infra --> PG[(PostgreSQL)]
    Infra -->|outbox relay| Kafka[(Kafka topic\nledger.events)]
    API --> OTel[OpenTelemetry\ntraces · metrics]
```

Dependencies point inwards: API → Application → Domain. Infrastructure implements the ports the
application declares. Nothing in the domain knows about HTTP, EF Core or Kafka.

## Request path for a transfer

```mermaid
sequenceDiagram
    participant C as Client
    participant A as API
    participant H as PostTransferHandler
    participant D as Posting (domain)
    participant U as UnitOfWork
    participant O as Outbox relay
    C->>A: POST /api/v1/transfers (Idempotency-Key)
    A->>H: PostTransferCommand (after FluentValidation)
    H->>H: FindByReference → replay? return original (200)
    H->>D: Transfer(from, to, amount)
    D->>D: validate entry (balanced, one currency), apply lines (overdraft rule)
    H->>U: Add entry, enqueue EntryPosted, Commit
    U->>U: one SQL transaction: accounts + entry + outbox
    A-->>C: 201 Created
    O->>O: poll outbox, publish to Kafka, mark published
```

## Layers

| Layer | Responsibility | Must not |
|---|---|---|
| Domain | Money, Currency, Account, JournalEntry, Posting, integration events | Reference frameworks; expose setters for balances |
| Application | Commands/queries, validation, idempotency, webhook verification, ports | Know which database or broker is used |
| Infrastructure | EF Core mapping, repositories, unit of work, outbox, Kafka, replay guard | Contain business rules |
| API | Endpoints, auth, error mapping, logging, tracing, health | Call repositories directly |

## Data model

```mermaid
erDiagram
    accounts ||--o{ entry_lines : "has"
    journal_entries ||--|{ entry_lines : "contains"
    journal_entries ||--o| outbox_messages : "emits"
    accounts {
        uuid id PK
        string owner
        int kind
        string currency
        decimal balance
        long version "concurrency token"
    }
    journal_entries {
        uuid id PK
        string reference UK "idempotency key"
        string description
        datetimeoffset posted_at
    }
    entry_lines {
        long id PK
        uuid entry_id FK
        uuid account_id FK
        decimal amount
        string currency
        int direction "debit | credit"
    }
    outbox_messages {
        uuid id PK
        string event_type
        string payload
        datetimeoffset published_at "null until relayed"
    }
    webhook_nonces {
        string provider PK
        string nonce PK
        datetimeoffset expires_at
    }
```

## Invariants and where they live

| Invariant | Enforced by | Backed by |
|---|---|---|
| Entry debits equal credits | `JournalEntry.Create` | unit tests |
| Customer accounts never negative | `Account.Apply` | unit + integration tests |
| Same reference never posts twice | `PostTransferHandler`, `RecordProviderSettlementHandler` | unique index on `journal_entries.reference` |
| Balance + entry + event saved together | `UnitOfWork.Commit` | single `SaveChanges` transaction |
| Concurrent updates do not overwrite | `accounts.version` concurrency token | 409 response, client retries with same key |
| Webhook accepted only if every check passes | `WebhookVerifier` | unit + integration tests |
| Nonce used once | `EfReplayGuard` | primary key on (provider, nonce) |

## Observability

- Serilog writes one JSON line per event; the trace id is included automatically.
- OpenTelemetry exports traces and metrics over OTLP. Health endpoints are excluded from traces.
- Every rejected request is logged with its reason at Information level; the client receives only the status code.

## Decisions

See `docs/adr/`.
