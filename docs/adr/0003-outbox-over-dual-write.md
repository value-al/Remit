# ADR-0003: Outbox, not dual write

**Status:** Accepted · **Date:** 2026-08-27

## Context

When a deposit is created, two things must happen: the row is committed and a
`funding.deposit.requested.v1` message is published. Doing them as two calls — commit,
then publish — is the dual-write bug: a crash between them leaves a deposit nobody will
ever process, or a message for a deposit that does not exist.

## Decision

Every service that publishes writes the message to an `outbox` table **in the same
transaction** as the state change. A relay process reads the outbox and publishes to
RabbitMQ, marking rows as sent. Delivery is therefore at-least-once, and every consumer
is required to be idempotent on the message id (ADR-0002 applies downstream as well).

The in-memory `InMemoryOutbox` exists so the request handler has the right shape from day
one; the PostgreSQL outbox and the relay arrive with the persistence work in week four.

## Alternatives considered

- **Publish inside the request handler, before commit.** Rejected: a rolled-back
  transaction has already announced itself.
- **Two-phase commit across PostgreSQL and RabbitMQ.** Rejected: operationally heavy,
  and RabbitMQ has no XA story worth relying on.
- **Change-data-capture (Debezium) on the aggregate table.** Reasonable, and the natural
  next step at scale. Rejected here because it adds a component the reference system does
  not need to make the point.

## Consequences

One more table per service and one relay to operate. Message ordering is per aggregate,
not global — consumers must not assume otherwise. Exactly-once is achieved at the
consumer, by idempotency, not at the transport.
