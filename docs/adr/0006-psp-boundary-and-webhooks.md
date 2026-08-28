# ADR-0006: One boundary for every provider; submit synchronously, settle by signed webhook

**Status:** Accepted · **Date:** 2026-08-28

## Context

Every payment provider has its own API, its own failure modes, its own webhook format and
its own signing scheme. The system must work when one of them is down, must never charge
twice when one of them retries, and must not trust an inbound "your deposit settled" from
anyone who can reach the URL.

## Decision

**One interface, three outcomes.** `IPaymentProvider.ChargeAsync` returns exactly
`Accepted`, `Rejected` or `Unavailable`. The distinction is the routing rule: a rejection
is the provider's answer and stands; an outage is a reason to try the next provider. A
throwing adapter is an outage. Providers see references and amounts, never card data.

**Routing by currency, then by observed health.** `PspRouter` builds the chain per request:
providers that support the currency, ranked by success rate over a sliding window of
recent attempts. Providers below 50% are demoted to the back of the chain rather than
excluded — degraded beats none. The router records outcomes; the health store is
process-local now and Redis-backed later so instances share the view.

**Submission is synchronous; settlement is asynchronous.** `POST /deposits` commits the
deposit as `Requested`, submits to the routed provider, then commits `SubmittedToPsp` (or
`Failed`) with the outbox message. The client learns at once whether a provider took the
charge; the money's arrival is confirmed later by the provider's webhook. The client's
`Idempotency-Key` is forwarded as the provider's idempotency key, so a retried submission
cannot become a second charge at the provider either.

**Webhooks are verified over the raw bytes with the provider's own secret.** Each provider
gets a `WebhookVerifier` (Countersign) with its *webhook* secret — never the API secret —
`timestamp.body` canonical form and a five-minute tolerance. Verification happens before
the body is parsed and before any lookup. The endpoint is exempt from the `Idempotency-Key`
middleware because providers do not send one.

**Duplicates and strays are acknowledged, not applied.** A second "settled" for a deposit
already settled hits a closed edge in the state machine and returns 200 with
`applied: false` — a non-2xx would only make the provider retry forever. A webhook that is
genuinely signed by provider B for a deposit that went to provider A, or whose reference
does not match, is logged and acknowledged the same way. Unknown deposits likewise.

## Alternatives considered

- **Submit to the provider from a consumer of `deposit.requested`.** Cleaner separation and
  a faster POST, at the cost of the client not knowing whether the charge was even
  attempted. Deferred: the consumer infrastructure arrives with the ledger in week 6, and
  the synchronous path can move behind it then without changing the state machine.
- **One shared webhook secret.** Rejected: a leak at one provider would let it forge the
  others' settlements.
- **Verifying after JSON parsing.** Rejected: re-serialised bytes can differ from what the
  provider signed, and parsing untrusted input before authenticating it is the wrong order.
- **Rejecting duplicates with 409.** Rejected: providers treat non-2xx as "retry", which is
  the opposite of what a duplicate needs.

## Consequences

Two commits per deposit request; a crash between them leaves a deposit in `Requested`
with no provider — visible, and swept by reconciliation (week 8). Provider health is per
process until the Redis store lands. Webhook event ids are logged but not yet stored;
storing them becomes necessary when a webhook can carry an effect the state machine cannot
detect as a repeat (partial captures, for example) — not yet.
