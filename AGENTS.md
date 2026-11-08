# AGENTS.md — Remit

The map for agents working in this repo. It points to where things are; the details live in
`docs/`. This project follows the value-al AI Development Protocol at autonomy level **L0**:
every PR needs the owner's `human-approved`.

**This repository is public.** The protocol lets agents run only in private repos, so no
agent runs here unattended until the owner decides (see `docs/tech-debt.md`). Everything
you commit is visible to the world: no real credentials, no client names, nothing private.

## What this is

An open-source reference money-movement backend in .NET 10: idempotent deposits and
withdrawals through payment providers, a double-entry ledger, an outbox between services and
reconciliation against provider statements. Small on purpose; every decision is written down
as an ADR and shown running. Scope and non-goals: `docs/adr/0001-scope-and-non-goals.md`.

## Where things are

| Need | Look in |
|---|---|
| What it does, how to run it | `README.md` |
| How the system fits together | `ARCHITECTURE.md` |
| Why a technical choice was made | `docs/adr/` (0001–0009) |
| Production shape, PCI boundary | `docs/architecture/c4-context.md` |
| SLOs, alerts, error budget | `docs/operations/slo.md` |
| Threat model and the fixes to do first | `docs/security/threat-model-deposit-flow.md` |
| Specs being built / done | `docs/specs/active/`, `docs/specs/done/` |
| Code rules | `docs/golden-principles.md` |
| Known debt and protocol gaps | `docs/tech-debt.md` |
| Deploying (AKS, sandbox) | `deploy/README.md`, `deploy/sandbox/README.md` |
| Commands, environments, high-risk paths | `project.yml` |

## Layout

- `src/BuildingBlocks/Remit.BuildingBlocks` — `Money`, idempotency middleware and store
  contract, outbox contract, RabbitMQ publisher/consumer, telemetry, hosting helpers.
- `src/Services/Remit.Funding` — deposits and withdrawals (state machines, endpoints),
  `Psp/` (router, simulated providers, Countersign-verified webhooks), `Persistence/` (EF Core,
  migrations), `Messaging/` (outbox relay), `wwwroot/console` (dev-only browser console).
- `src/Services/Remit.Ledger` — `JournalEntry`, settlement consumer with inbox, balance endpoints.
- `src/Services/Remit.Reconciliation` — movement record from events, statement matching,
  exceptions, stuck sweep.
- `tests/` — one test project per service (xUnit, Testcontainers for PostgreSQL and RabbitMQ)
  plus `Remit.Architecture.Tests` (NetArchTest).
- `infra/bicep`, `deploy/helm/remit` — AKS. `deploy/sandbox` — the public sandbox.

## Commands

```bash
dotnet restore && dotnet tool restore
dotnet build -c Release
dotnet test tests/Remit.Architecture.Tests -c Release   # architecture rules, fast
dotnet test -c Release                                  # everything; needs Docker
dotnet stryker --since:main                             # mutation testing on changed code
docker compose up -d && dotnet run --project src/Services/Remit.Funding
```

## How to work here

- One issue per session. Change only what the issue says; anything else becomes a new issue.
- Branch `<issue-number>-<short-name>`, PR body `Closes #<n>`. Never push to `main`, never merge.
- Tests from the acceptance criteria first, then the code.
- Never modify, delete or rename existing tests; add new ones.
- Never wait for an answer: decide, and write `Decision made: <what and why>` in the PR.
  Never add labels yourself.
- After 3 failed attempts, stop and write a failure summary in the issue.
- A structural change updates `ARCHITECTURE.md` and, if a rule moves, the architecture tests.
  A decision that is expensive to reverse gets an ADR (next number in `docs/adr/`).
- Schema changes: `dotnet ef migrations add <Name>` in the service, commit the migration;
  never edit an applied one.
- Never use `--no-verify`, `--force`, hard resets, `git clean`, `DROP` or `TRUNCATE`; never
  print secrets. Never run `deploy.yml`, `az`, `helm` or `deploy/sandbox/reset.sh` against a
  real environment.

Until the protocol's platform exists (machine account, App, taskrunner), the owner merges.

## Money rules (short; details in `docs/golden-principles.md`)

- Nothing moves twice: `Idempotency-Key` at the edge, state machines at the core, inboxes
  on every consumer.
- State change and outbox message in one transaction; never publish from a handler.
- Journal entries balance by construction and are never edited; balances are derived.
- Reconciliation raises exceptions and never fixes anything.
- Services share no tables and no types; they talk through events or HTTP.
- Ledger, funding, reconciliation, migrations, CI and deploy are High tier (`needs_human`).

## Owner

value-al's own open-source reference project, Playbook level light; Aldiger approves.
Design questions belong in an ADR or a question to him, not a silent choice.
No AI attribution in commits, PRs, issues or code comments.
