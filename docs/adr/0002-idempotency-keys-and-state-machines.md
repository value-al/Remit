# ADR-0002: Idempotency keys at the edge, state machines at the core

**Status:** Accepted · **Date:** 2026-08-27

## Context

Money movement is retried from every direction: clients retry timeouts, PSPs retry
webhooks, brokers redeliver messages. The failure this produces is not an error — it is a
*second* deposit, silently. Two mechanisms are needed, because they protect against
different retries.

## Decision

**1. Every unsafe HTTP endpoint requires an `Idempotency-Key`.**
The middleware in `Remit.BuildingBlocks.Idempotency` enforces:

| Situation | Response |
|---|---|
| POST without the header | 400 — moving money is opt-in, never accidental |
| Key seen, same request hash | replay of the original status and body, `Idempotent-Replayed: true` |
| Key seen, different request hash | 422 — the client reused a key for a different request |
| Key claimed, first request still running | 409 — retry after it completes |
| Handler throws | key released; the client may retry |

The hash covers the body **and the route**, so a key cannot be replayed across endpoints.
Validation failures are stored and replayed too: a 400 is a legitimate, deterministic
answer to that request.

**2. Every money-moving aggregate is an explicit state machine.**
`Deposit` lists its allowed transitions in one table; anything else throws
`InvalidDepositTransitionException`. A duplicate "settled" webhook therefore cannot settle
a deposit twice — it hits a closed edge and is logged, not applied. Terminal states have no
outgoing edges.

## Alternatives considered

- **Database unique constraint on (account, amount, minute).** Rejected: legitimate repeat
  deposits exist, and the constraint encodes a guess about client behaviour.
- **Idempotency only in the PSP adapter.** Rejected: protects against PSP retries only, not
  client retries or our own redeliveries.
- **Status as a free `string` column updated by handlers.** Rejected: every handler becomes
  a place the invariant can be broken. The table of edges is the invariant.

## Consequences

Clients must generate keys (UUIDs) and keep them across retries. Stored responses need an
expiry (24 h is conventional) — the PostgreSQL store will carry one. Consumers of outbox
messages are required to be idempotent on message id for the same reason; see ADR-0003.
