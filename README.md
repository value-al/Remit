# Remit

[![CI](https://github.com/value-al/Remit/actions/workflows/ci.yml/badge.svg)](https://github.com/value-al/Remit/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

A reference money-movement backend for a retail trading or fintech platform, in .NET 10 —
deposits and withdrawals through payment providers, a double-entry ledger, reconciliation,
and the messaging and observability that hold it together.

It is small on purpose. The point is not the code volume; it is that every decision a system
like this has to make is **made, written down, and shown running**.

> The domain is generic. Nothing here describes any particular company's system.

## What is decided

| Decision | Where |
|---|---|
| Scope and non-goals — no card data, no FX, one region | [ADR-0001](docs/adr/0001-scope-and-non-goals.md) |
| `Idempotency-Key` at the edge, state machines at the core | [ADR-0002](docs/adr/0002-idempotency-keys-and-state-machines.md) |
| Outbox in every service, never a dual write | [ADR-0003](docs/adr/0003-outbox-over-dual-write.md) |
| Double-entry ledger, balanced by construction | [ADR-0004](docs/adr/0004-double-entry-ledger.md) |
| Context and container diagrams, PCI scope boundary | [C4](docs/architecture/c4-context.md) |

## What runs today

- `Remit.BuildingBlocks` — `Money`, the idempotency middleware and store, the outbox contract.
- `Remit.Funding` — `POST /deposits` (idempotent, 202 + outbox message) and `GET /deposits/{id}`;
  `Deposit` as an explicit state machine.
- `Remit.Ledger` — `JournalEntry` that cannot be constructed unbalanced.
- Tests for all of the above, including the replay / mismatch / conflict behaviour of the
  idempotency layer through the real HTTP pipeline.

```sh
dotnet test
docker compose up -d      # PostgreSQL, RabbitMQ, Redis, Jaeger — used from week 4
dotnet run --project src/Services/Remit.Funding
```

```sh
curl -X POST localhost:5000/deposits/ \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: 5f1c9a0e-1d3b-4a8c-9a7e-2c1f0b6d8e44' \
  -d '{"accountId":"11111111-1111-1111-1111-111111111111","amount":100,"currency":"EUR"}'
# → 202 Accepted. Send it again: same response, plus  Idempotent-Replayed: true
```

## Roadmap

| Week | Lands |
|---|---|
| 4 | PostgreSQL persistence, outbox table and relay to RabbitMQ |
| 5 | PSP adapter boundary, two simulated providers, routing by currency and success rate; webhooks verified with [Countersign](https://github.com/value-al/Countersign) |
| 6 | Ledger consumer posts settlements; withdrawals; OpenTelemetry end to end |
| 7 | AKS deployment with infrastructure as code; Key Vault; managed identity |
| 8 | Reconciliation against a statement file; exceptions endpoint |
| 9 | SLO document; STRIDE threat model of the deposit flow |

## Layout

```
src/BuildingBlocks/Remit.BuildingBlocks   cross-cutting primitives
src/Services/Remit.Funding                deposits & withdrawals (HTTP)
src/Services/Remit.Ledger                 journal
tests/                                    one test project per service
docs/adr/                                 architecture decision records
docs/architecture/                        C4 diagrams
```

## Author

Designed and written by [Aldiger Mehilli](https://aldiger.com) — software architect for
payment and trading backends — at [value.al](https://value.al). MIT licensed.
