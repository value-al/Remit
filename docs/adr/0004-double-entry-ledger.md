# ADR-0004: Double-entry ledger, balanced by construction

**Status:** Accepted · **Date:** 2026-08-27

## Context

A wallet balance stored as a number that handlers increment and decrement is the design
that produces unexplained differences at month end. Auditors, regulators and the
reconciliation job all need the same thing: to see *why* a balance is what it is.

## Decision

The ledger is a journal of immutable **entries**, each made of two or more **postings**
(account, amount, side). `JournalEntry.Create` refuses to construct an entry whose debits
and credits do not balance, per currency. Corrections are new entries that reverse and
re-post; nothing is ever edited or deleted.

Balances are derived by summing postings for an account. Materialised balances are a
cache and are rebuilt from the journal when they disagree — the journal wins.

Account names are hierarchical strings for now (`client:wallet:42`, `psp:receivable`);
a chart of accounts arrives with the reconciliation work.

## Alternatives considered

- **Single-entry balance column with an audit log.** Rejected: the log is advisory; nothing
  stops the balance and the log from diverging.
- **Event-sourced wallet aggregate.** Close cousin; rejected only because double-entry is
  what finance teams and reconciliation already speak, and the invariant (balanced entry)
  is simpler to state and test than a fold over events.

## Consequences

Every money movement becomes at least two postings, which is more rows and more thought
per feature. In exchange, every balance is explainable to the cent, and reconciliation
is a comparison of two journals rather than a hunt.
