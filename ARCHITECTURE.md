# Architecture — Remit

Five parts, as the value-al Architecture standard asks. Kept true by the architecture tests
in `tests/Remit.Architecture.Tests` (NetArchTest) and by the reviewer, who fails a PR that
changes structure without updating this file. The C4 diagrams and the overview picture in
`docs/architecture/` show the intended production shape (gateway, Redis); this file shows
what the code does today.

## 1. System context

```mermaid
flowchart LR
    C["Client app or the Remit console<br/>(browser, curl)"]
    OPS["Operations<br/>statements, exceptions"]
    PSP["PSPs alpha, beta<br/>simulated in-process"]
    R["Remit<br/>Funding · Ledger · Reconciliation<br/>.NET 10"]
    PG[("PostgreSQL<br/>one schema per service")]
    MQ{{"RabbitMQ<br/>topic exchange remit"}}
    OT["Jaeger / OTLP collector"]

    C -- "POST /deposits, /withdrawals<br/>Idempotency-Key" --> R
    PSP -- "signed webhooks<br/>/webhooks/psp/{provider}" --> R
    OPS -- "POST /statements/{provider} (CSV)<br/>GET /exceptions" --> R
    R -- "charge / payout" --> PSP
    R --> PG
    R <--> MQ
    R -. "traces, when Otel:Endpoint is set" .-> OT
```

No authentication inside Remit: in production a gateway in front does that (ADR-0001, C4).
No card data ever enters; PSPs exchange references only. Runs locally with
`docker-compose.yml`, as a public sandbox at `remit-sandbox.value.al` (`deploy/sandbox`), and
has an AKS path (`infra/bicep`, `deploy/helm`, ADR-0008) that is deployed by hand.

## 2. Components and allowed dependencies

```mermaid
flowchart TD
    subgraph bb["Remit.BuildingBlocks"]
        MONEY["Money, IUnitOfWork"]
        IDEM["Idempotency/<br/>middleware, store contract"]
        OUTB["Outbox/<br/>IOutbox, OutboxMessage"]
        MSG["Messaging/<br/>RabbitMqPublisher, RabbitMqConsumer, trace context"]
        HOST["Hosting/, Telemetry/<br/>edge, health, migrate, OpenTelemetry"]
    end
    subgraph funding["Remit.Funding"]
        FE["Deposits/, Withdrawals/<br/>state machines, endpoints, repository contracts"]
        PSPB["Psp/<br/>router, providers, Countersign webhooks"]
        FP["Persistence/<br/>FundingDbContext, EF stores, migrations"]
        REL["Messaging/<br/>OutboxRelay"]
    end
    subgraph ledger["Remit.Ledger"]
        JE["JournalEntry, Posting<br/>(balanced by construction)"]
        LC["Consuming/<br/>SettlementHandler + inbox"]
        LB["Balances/<br/>read endpoints"]
        LP["Persistence/"]
    end
    subgraph recon["Remit.Reconciliation"]
        MA["Matching/<br/>StatementMatcher, CSV (pure)"]
        RC["Consuming/, Statements/, Sweeps/"]
        RP["Persistence/"]
    end

    FE <--> PSPB
    FE --> OUTB
    FE --> IDEM
    FP --> FE
    REL --> FP
    REL --> MSG
    LC --> JE
    LC --> LP
    LB --> LP
    LP --> JE
    RC --> MA
    RC --> RP
    RP --> MA
    funding --> MONEY
    ledger --> MONEY
    recon --> MONEY
    LC --> MSG
    RC --> MSG
```

Rules enforced by `tests/Remit.Architecture.Tests` (each failure says how to fix it):

- `Remit.BuildingBlocks` depends on no service. Services never depend on each other: they
  talk through RabbitMQ events or HTTP (Funding asks the Ledger for balances over HTTP).
- `Deposit`, `Withdrawal`, `JournalEntry`/`Posting`, `Money` and everything in
  `Reconciliation/Matching` are plain C#: no EF Core, Npgsql, RabbitMQ, ASP.NET Core,
  HttpClient, Countersign or OpenTelemetry, and no other folders of their service.
- `Deposits/`, `Withdrawals/` and `Psp/` reach the database only through repository, outbox
  and unit-of-work contracts (one known exception, `EfWithdrawalRepository`; see
  `docs/tech-debt.md`). `Persistence/` never depends on `Psp/` or `Messaging/`.
- Only `Remit.BuildingBlocks.Messaging` uses RabbitMQ.Client; only `Funding/Psp` uses Countersign.

## 3. Key flows

**Deposit: request, provider, signed webhook**

```mermaid
sequenceDiagram
    actor Client
    participant MW as Idempotency middleware
    participant EP as POST /deposits
    participant DB as funding schema
    participant PSP as PspRouter → provider
    participant WH as /webhooks/psp/{provider}
    Client->>MW: POST /deposits + Idempotency-Key
    MW->>DB: claim key (primary key), else replay / 409 / 422
    MW->>EP: first time only
    EP->>DB: deposit Requested + outbox row (one commit)
    EP->>PSP: charge (currency, then health rank; our key passed on)
    PSP-->>EP: accepted / rejected / unavailable
    EP->>DB: SubmittedToPsp or Failed + outbox row (one commit)
    EP-->>Client: 202 with provider and reference
    PSP->>WH: settled, signed {timestamp}.{body}
    WH->>WH: verify signature over raw bytes, 5 min window
    WH->>DB: Settled + funding.deposit.settled.v1 in outbox
```

**Settlement to the ledger, exactly once**

```mermaid
sequenceDiagram
    participant REL as OutboxRelay (Funding)
    participant MQ as RabbitMQ
    participant SH as SettlementHandler (Ledger)
    participant LDB as ledger schema
    REL->>REL: SELECT unsent FOR UPDATE SKIP LOCKED
    REL->>MQ: publish with traceparent, publisher confirm
    REL->>REL: mark sent
    MQ->>SH: funding.deposit.settled.v1 (at least once)
    SH->>LDB: inbox row for message id?
    alt already there
        SH-->>MQ: ack, nothing posted
    else new
        SH->>LDB: inbox row + balanced entry (debit psp:receivable, credit client:wallet) in one commit
        SH-->>MQ: ack
    end
```

**Reconciliation against a provider statement**

```mermaid
sequenceDiagram
    participant MQ as RabbitMQ
    participant MH as MovementHandler
    actor Ops
    participant ST as POST /statements/{provider}
    participant M as StatementMatcher (pure)
    participant RDB as reconciliation schema
    MQ->>MH: every funding.deposit.# / funding.withdrawal.# event
    MH->>RDB: inbox + own movement record (terminal status never overwritten)
    Ops->>ST: statement CSV, period from..to
    ST->>RDB: movements of that provider and period
    ST->>M: lines + expected movements
    M-->>ST: matched, exceptions (UnknownAtPsp, AmountMismatch, SettledButNotFinal, MissingAtPsp)
    ST->>RDB: statement + exceptions (one open per kind and reference)
    Note over ST,RDB: never posts, replays or settles anything
```

## 4. Data model

One schema per service; no foreign keys across schemas. Dotted lines are references by id only.

```mermaid
erDiagram
    DEPOSITS ||--o{ DEPOSIT_TRANSITIONS : "history"
    WITHDRAWALS ||--o{ WITHDRAWAL_TRANSITIONS : "history"
    JOURNAL_ENTRIES ||--|{ POSTINGS : "2 or more, balanced"
    MOVEMENTS }o..o{ EXCEPTIONS : "movement_id"
    STATEMENTS |o..o{ EXCEPTIONS : "statement_id"
    DEPOSITS ||..o| MOVEMENTS : "same id, via events"
    DEPOSITS ||..o| JOURNAL_ENTRIES : "correlation_id"

    DEPOSITS {
        uuid id PK
        uuid account_id
        numeric amount
        text currency
        text status "Requested, SubmittedToPsp, Settled, Failed"
        text provider
        text psp_reference
        xid xmin "optimistic concurrency"
    }
    WITHDRAWALS {
        uuid id PK
        uuid account_id
        numeric amount
        text status "Requested, SubmittedToPsp, Paid, Failed"
        text psp_reference
    }
    DEPOSIT_TRANSITIONS {
        int id PK
        uuid deposit_id FK
        text from_status
        text to_status
    }
    WITHDRAWAL_TRANSITIONS {
        int id PK
        uuid withdrawal_id FK
    }
    OUTBOX {
        uuid id PK
        text type "funding.deposit.settled.v1, ..."
        jsonb payload
        text correlation_id
        timestamptz sent_at "null until relayed"
    }
    IDEMPOTENCY_KEYS {
        text key PK "the claim"
        text request_hash "body + route"
        int status_code
        text body
    }
    JOURNAL_ENTRIES {
        uuid id PK
        text description
        text correlation_id
    }
    POSTINGS {
        bigint id PK
        uuid entry_id FK
        text account "client:wallet:{id}, psp:receivable:{provider}"
        numeric amount
        text side "Debit, Credit"
    }
    LEDGER_INBOX {
        uuid message_id PK
    }
    MOVEMENTS {
        uuid id PK
        text kind "Deposit, Withdrawal"
        text provider
        text reference
        text status
    }
    STATEMENTS {
        uuid id PK
        text provider
        int lines
        int matched
    }
    EXCEPTIONS {
        uuid id PK
        text kind
        text reference "unique while open, per kind"
        uuid movement_id
        uuid statement_id
        text resolution "written reason, once"
    }
    RECON_INBOX {
        uuid message_id PK
    }
```

## 5. Decision log

| Date | Decision | ADR |
|---|---|---|
| 2026-08-27 | Scope and non-goals: no card data, no FX, one region | [ADR-0001](docs/adr/0001-scope-and-non-goals.md) |
| 2026-08-27 | `Idempotency-Key` at the edge, explicit state machines at the core | [ADR-0002](docs/adr/0002-idempotency-keys-and-state-machines.md) |
| 2026-08-27 | Outbox in every publishing service, never a dual write | [ADR-0003](docs/adr/0003-outbox-over-dual-write.md) |
| 2026-08-27 | Double-entry ledger, balanced by construction; balances derived | [ADR-0004](docs/adr/0004-double-entry-ledger.md) |
| 2026-08-28 | PostgreSQL schema per service, EF Core migrations, polling relay with SKIP LOCKED, xmin concurrency | [ADR-0005](docs/adr/0005-persistence-and-relay.md) |
| 2026-08-28 | One PSP boundary, three outcomes; route by currency then health; settle by signed webhook | [ADR-0006](docs/adr/0006-psp-boundary-and-webhooks.md) |
| 2026-08-28 | Ledger consumes with an inbox; withdrawals mirror deposits; one trace per movement | [ADR-0007](docs/adr/0007-ledger-consumer-withdrawals-telemetry.md) |
| 2026-08-28 | AKS by Bicep, workload identity and Key Vault CSI, migrations as pre-upgrade Jobs | [ADR-0008](docs/adr/0008-aks-workload-identity-key-vault.md) |
| 2026-08-28 | Reconciliation keeps its own record, matches on reference only, never fixes anything | [ADR-0009](docs/adr/0009-reconciliation.md) |
