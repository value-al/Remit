# ADR-0009: Reconciliation compares its own record to the provider's, and never fixes anything

**Status:** Accepted · **Date:** 2026-08-28

## Context

Every earlier decision left a residue that only a comparison against the outside world can
catch: a crash between Funding's two commits (ADR-0006), a webhook that never arrives, a
withdrawal that passed an advisory balance check it should not have (ADR-0007), a provider
that settled something we never asked for. The ledger is internally consistent by
construction (ADR-0004); reconciliation is where internal consistency meets the bank.

## Decision

**Reconciliation is its own service with its own record.** It consumes every Funding event —
requested, submitted, settled/paid, failed — through the shared consumer with an inbox, and
keeps a `movements` table: one row per deposit or withdrawal with its last known status,
provider and reference. It never reads Funding's or the ledger's tables (ADR-0005). Events
may arrive out of order; a terminal status is never overwritten by an earlier one, and a
late event still contributes the reference.

**A statement is matched on the reference and nothing else.** `POST /statements/{provider}`
takes the provider's statement as CSV for a period. The matcher (`StatementMatcher`, pure)
joins on the PSP reference — amounts are evidence, never keys, because two deposits of
50 EUR on one day are normal. Four differences are named:

| Exception | Meaning |
|---|---|
| `UnknownAtPsp` | on the statement, not in our records |
| `AmountMismatch` | same reference, different amount or currency |
| `SettledButNotFinal` | the provider settled it; our record is still in flight — a lost webhook |
| `MissingAtPsp` | final on our side inside the period, absent from the statement |

A fifth, `Stuck`, comes from a sweep, not a statement: anything in `Requested` or
`SubmittedToPsp` for longer than a configurable window. That is the ADR-0006 crash window
and the lost-webhook window, found without waiting for month end.

**Exceptions are raised once and resolved by a person with a written reason.** A partial
unique index allows one open exception per (kind, provider, reference), so re-posting a
statement is safe. `POST /exceptions/{id}/resolve` requires text and refuses a second
resolution. The service records decisions; it never makes them. In particular it never
posts to the ledger, never re-sends a webhook, never marks anything settled — a
`SettledButNotFinal` exception is the signal for an operator to replay the provider's
event through Funding, where the state machine still applies.

## Alternatives considered

- **Reconcile inside the ledger against `psp:receivable` postings.** Tempting, since the
  ledger already has one side; rejected because the ledger would then need Funding's
  in-flight states and the provider's statements, and it stops being a journal.
- **Fuzzy matching on amount and date when the reference is missing.** Rejected for the
  reference system: it is exactly the class of automation that makes an audit unable to
  say why two things were joined. A missing reference is an `UnknownAtPsp` exception.
- **Auto-resolving `SettledButNotFinal` by marking the deposit settled.** Rejected; that is
  a money-moving decision taken by a batch job on the strength of a CSV.

## Consequences

Three services now consume from the broker; the reconciliation queue binds to
`funding.deposit.#` and `funding.withdrawal.#`. The statement format is fixed and strict;
real providers need an adapter that produces it. Balance-level reconciliation (does the
sum of a provider's settlements equal what the ledger holds in `psp:receivable`) is the
natural next check and is not in this ADR.
