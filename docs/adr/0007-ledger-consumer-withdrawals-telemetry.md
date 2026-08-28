# ADR-0007: The ledger consumes with an inbox; withdrawals mirror deposits; one trace per money movement

**Status:** Accepted · **Date:** 2026-08-28

## Context

Until now Funding published settlement events that nobody read. The ledger has to turn them
into journal entries exactly once, withdrawals need a path out of the system that is as
disciplined as the path in, and a deposit that crosses HTTP, PostgreSQL, RabbitMQ and a
second service needs to be one trace, not four.

## Decision

**The ledger is a consumer with an inbox.** `RabbitMqConsumer` (shared building block)
gives each service one durable queue bound to the patterns its handler declares, manual
acks, prefetch 16, and a dead-letter exchange: a message that fails twice is parked, not
retried forever. `SettlementHandler` writes an `inbox` row keyed by the outbox message id
**in the same transaction** as the journal entry. A redelivery finds the row and skips; two
concurrent deliveries race on the primary key and the loser rolls back. That is exactly-once
posting on top of at-least-once delivery — the other half of ADR-0003.

**Postings are fixed per event type.**

| Event | Debit | Credit |
|---|---|---|
| `funding.deposit.settled.v1` | `psp:receivable:{provider}` | `client:wallet:{account}` |
| `funding.withdrawal.paid.v1` | `client:wallet:{account}` | `psp:payable:{provider}` |

Wallets are liabilities and therefore credit-normal; the balance endpoint sums credits
minus debits from the journal on every call (ADR-0004). No stored balance exists to drift.

**Withdrawals mirror deposits.** Same explicit state machine (`Requested → SubmittedToPsp →
Paid | Failed`), same two commits, same router chain (`PayoutAsync` alongside
`ChargeAsync`, same three outcomes), same signed webhook naming `withdrawalId` instead of
`depositId`, same closed edges against duplicates.

**The balance check before a payout is advisory.** Funding asks the ledger's balance
endpoint and refuses with 422 if funds are short. It does not place a hold, so two
withdrawals racing on one balance can both pass. This is accepted for now, stated here so
nobody mistakes it for a guarantee, and it is what reconciliation (week 8) exists to catch.
The planned fix is a reservation entry (`client:wallet → client:hold`) posted by the ledger
on `withdrawal.requested` and released on `paid`/`failed`.

**One trace per money movement.** `AddRemitTelemetry` configures OpenTelemetry once per
service: ASP.NET Core, HttpClient and Npgsql instrumentation, every `Remit.*`
ActivitySource, runtime and HTTP metrics, OTLP export when `Otel:Endpoint` is set. The
relay starts a span per publish and injects W3C `traceparent`/`tracestate` into the
message headers; the consumer extracts them and starts its span as a child. A deposit is
therefore visible in Jaeger as request → relay → publish → process → postings, across two
services.

## Alternatives considered

- **Skip the inbox and rely on the ledger entry's correlation id being unique.** Rejected:
  it couples idempotency to a business field that legitimately repeats (a reversal carries
  the same correlation id), and it makes the guarantee implicit.
- **Balance check inside the ledger via a synchronous "reserve" call.** The right eventual
  answer; deferred so the mirror-of-deposit structure lands first without a saga.
- **Trace context in the outbox row instead of message headers.** Would survive a relay
  restart between commit and publish, but makes the row schema carry transport concerns.
  Headers are the conventional place; the relay's span is the root when the request's
  context is gone, which is honest about where the work happens.

## Consequences

Two services now share a broker and a trace. The ledger's consumer is one instance per
process; prefetch 16 bounds in-flight work. Dead-lettered messages are visible in
`remit-ledger.dead` and need an operator; that is deliberate. Provider payouts use the
same health window as charges, which conflates the two paths' reliability — acceptable
until a provider actually differs.
