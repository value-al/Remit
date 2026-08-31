# Service level objectives

What Remit promises, how each promise is measured, and what happens when the error budget
runs out. Three services, one user-visible journey. Numbers are set for a reference system
of a retail broker at a few thousand deposits a day; the point is that each one is
*measurable from telemetry that already exists* (ADR-0007), not that the number is right
for every business.

## The one journey that matters

A client asks to deposit; the money shows up in their wallet. Everything else — payouts,
statements, dashboards — is downstream of that being reliable.

```
POST /deposits ──► 202 (submitted)   ──► provider webhook ──► ledger posting ──► balance
     │ SLO 1: availability + latency        │ SLO 2: settlement freshness       │ SLO 3: ledger lag
```

## SLOs

| # | Objective | SLI (how it is measured) | Target (30-day window) | Error budget |
|---|---|---|---|---|
| 1 | **Deposit requests are accepted and fast** | Share of `POST /deposits` returning 202 or a 4xx that is the client's fault, out of all non-4xx-client requests; p99 latency of the same, from `http.server.request.duration` | 99.9% success · p99 ≤ 800 ms | 43 min of failure / month |
| 2 | **Settlements are applied promptly** | Time from provider webhook receipt to `Deposit.Status = Settled`, from the webhook span's duration; and share of webhooks answered 2xx (never a 5xx to a provider) | p99 ≤ 2 s · 99.95% 2xx | 21 min / month |
| 3 | **The ledger is never far behind** | Age of the oldest unsent outbox row (`funding.outbox WHERE sent_at IS NULL`), and consumer lag (queue depth on `remit-ledger`) | oldest unsent ≤ 30 s for 99.9% of minutes; queue depth < 1,000 | ~43 min / month |
| 4 | **Reconciliation finds what it should** | Stuck movements older than the sweep window that have *no* open exception (should be zero); statements processed within the run | 0 unflagged stuck movements at any sweep · statement processed ≤ 5 min | any breach is an incident, not a budget |
| 5 | **Withdrawals do not overdraw** | Wallets whose derived balance goes negative in any currency (a query on `ledger.postings`) | 0 | any breach is an incident |

SLOs 4 and 5 have no budget on purpose: they are correctness properties dressed as SLOs so
they get measured and paged like one, and a single breach is a P1 (see ADR-0007 on the
advisory balance check — SLO 5 is the alarm for that known gap).

## SLIs in terms of what the code already emits

| Signal | Source | Where it comes from |
|---|---|---|
| Request success and latency | `http.server.request.duration` histogram, tagged by route and status | ASP.NET Core instrumentation in `AddRemitTelemetry` |
| Webhook handling time | span `POST /webhooks/psp/{provider}` | same |
| Relay health | oldest `sent_at IS NULL` row, `attempts`, `last_error` | one query on `funding.outbox`; worth a gauge in the relay (`remit.outbox.oldest_unsent_seconds`) |
| Consumer lag | `remit-ledger` and `remit-reconciliation` queue depth, dead-letter queue depth | RabbitMQ management API / Prometheus plugin |
| End-to-end trace | `POST /deposits` → `relay` → `publish` → `process` → postings | W3C trace context through the broker (ADR-0007) |
| Stuck movements | `reconciliation.movements` vs open `Stuck` exceptions | one query, or the sweep's own log line |

Two gauges are worth adding to the code when the first dashboard is built: the oldest-unsent
age in the relay, and the open-exception count by kind in reconciliation. Both are one
`Meter` each and the `AddMeter("Remit.*")` registration already picks them up.

## Alerting

| Condition | Severity | Who | Why this threshold |
|---|---|---|---|
| Any provider webhook answered 5xx in the last 5 min | P2 | on-call engineer | Providers retry on 5xx; a sustained 5xx becomes a duplicate-delivery storm |
| Oldest unsent outbox row > 2 min | P2 | on-call engineer | Relay or broker is down; balances are stale but nothing is lost |
| Dead-letter queue depth > 0 | P2 | on-call engineer | A message failed twice; someone has to read it |
| SLO 1 burn rate > 14× (would exhaust budget in 2 days) over 1 h | P1 | on-call engineer | Fast-burn: deposits are failing now |
| SLO 1 burn rate > 2× over 24 h | P3 | team, next working day | Slow-burn: something is eroding the budget |
| Stuck movement without an open exception | P1 | on-call + finance | The sweep itself is broken; money may be in limbo unseen |
| Negative wallet balance | P1 | on-call + finance | Client was paid out more than they had |
| Open reconciliation exceptions > 0 for > 2 working days | P3 | finance | Exceptions are meant to be resolved, not accumulated |

## Error-budget policy

- **Budget healthy (> 50% left):** ship normally, including risky changes to the money path,
  behind the tests this repository already carries.
- **Budget under 50%:** changes to the money path need a second reviewer and a rollback plan
  written in the PR.
- **Budget exhausted:** feature work on Funding stops; the only merges are reliability fixes and
  the incident's follow-ups, until the 30-day window recovers.
- SLOs 4 and 5 are outside the budget: a breach opens an incident regardless of budget state.

## What is deliberately not an SLO

- Provider availability. It is measured (the router's health window) and routed around; it is
  not something Remit can promise.
- Reconciliation *outcome* (how many exceptions). Exceptions are the system working; the SLO is
  that they are raised and looked at, not that they are few.
- Anything on the ledger's read endpoints. They are derived views; correctness and lag are
  covered by SLOs 3 and 5.
