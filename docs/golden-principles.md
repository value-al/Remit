# Golden principles — Remit

Short code rules. Most are enforced by a test or an analyzer; the weekly cleanup agent opens
small PRs where code drifts from them.

| # | Rule | Enforced by |
|---|---|---|
| 1 | Every unsafe endpoint needs an `Idempotency-Key`; only provider webhooks are exempt (`ExemptFromIdempotency`). | Idempotency API tests |
| 2 | Money-moving aggregates are explicit state machines: every transition is in the allowed-edges table, anything else throws. | State machine tests |
| 3 | A state change and its outbox message are committed together. Never publish from a handler. | Outbox and end-to-end tests, review |
| 4 | Every consumer is idempotent on the message id through an inbox row written in the same transaction. | Ledger and reconciliation tests |
| 5 | A journal entry is balanced per currency by construction, never edited or deleted; corrections are new entries. Balances are derived from postings. | `JournalEntryTests` |
| 6 | Amounts are `Money` (decimal + ISO currency), never `double`, never a bare decimal across a boundary. | Review |
| 7 | Webhooks are verified over the raw bytes before anything is parsed. | Webhook API tests, architecture tests |
| 8 | Reconciliation only raises exceptions; it never posts, replays or settles. | Reconciliation tests, review |
| 9 | One schema per service; a service never reads another service's tables or types. | Architecture tests |
| 10 | Domain types and `Matching/` stay plain C#; infrastructure lives in `Persistence/`, `Messaging/`, `Psp/` and endpoints. | Architecture tests |
| 11 | Schema changes are EF Core migrations, generated and committed; never edit an applied migration. | Review (`needs_human`) |
| 12 | No real credential in this public repo: dev and sandbox secrets only, real ones from Key Vault. | gitleaks in CI |
| 13 | Warnings are errors (`TreatWarningsAsErrors`); code style from `.editorconfig`. | `dotnet build` |
| 14 | Every money-moving decision that is expensive to reverse gets an ADR in `docs/adr/`. | Review |
