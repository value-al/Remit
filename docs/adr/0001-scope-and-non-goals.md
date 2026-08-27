# ADR-0001: Scope and non-goals

**Status:** Accepted · **Date:** 2026-08-27

## Context

Remit is a reference implementation of a money-movement backend for a retail trading or
fintech platform: client funds come in through payment service providers (PSPs), are held
in a ledger, and go back out. It exists to show the design decisions such a system needs
— not to be a product, a framework, or a tutorial.

The domain is deliberately generic. Nothing in this repository describes any particular
company's system.

## Decision

In scope, in the order it will be built:

1. **Funding** — deposits and withdrawals as explicit state machines; idempotent on an
   `Idempotency-Key`; PSP adapters behind one boundary with routing by currency and
   observed success rate.
2. **Ledger** — double-entry, immutable, balanced-by-construction journal entries; account
   balances derived, never stored as the source of truth.
3. **Reconciliation** — PSP statements matched against ledger postings; exceptions surfaced,
   never auto-fixed.
4. **Messaging** — an outbox in every service; RabbitMQ between services; at-least-once
   delivery with idempotent consumers.
5. **Operability** — OpenTelemetry traces and metrics end to end; one SLO document.
6. **Deployment** — Docker Compose locally; AKS with infrastructure as code.
7. **Security** — inbound PSP webhooks verified with [Countersign](https://github.com/value-al/Countersign);
   a PCI scope boundary drawn on the container diagram; a STRIDE pass on the deposit flow.

Non-goals, stated so they stay out:

- No card data ever enters this system. PSPs tokenise; Remit sees references.
- No FX engine, no trading, no market data.
- No user interface beyond an operations view for reconciliation exceptions.
- No multi-region. One region, designed so a second is a deployment change, not a rewrite.
- No minor-unit arithmetic library; `decimal` with explicit currency until it is needed.

## Consequences

Small enough to read in an afternoon; complete enough that each decision can be shown
running. The budget is twenty hours of build across weeks three to nine of a twelve-week
plan; anything that threatens that is cut from scope, not from quality.
