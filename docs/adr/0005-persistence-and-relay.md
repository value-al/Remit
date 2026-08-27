# ADR-0005: PostgreSQL per service, EF Core with migrations, a polling relay

**Status:** Accepted · **Date:** 2026-08-28

## Context

ADR-0003 requires the aggregate and its outbox message to be committed together. That
decides the shape of persistence more than any ORM preference does: whatever writes the
deposit must write the outbox row in the same transaction, and something must drain that
table to the broker afterwards.

## Decision

**One PostgreSQL schema per service.** Funding owns `funding.*`; no other service reads it.
Services share a server in development and may share one in production; they never share
tables. This keeps the option of a separate database per service as a deployment change.

**EF Core 10 with explicit mapping and checked-in migrations.** Column names, lengths, the
`jsonb` payload column and the partial index the relay scans are all declared in
`FundingDbContext`, so the schema is readable from one file. Migrations are generated with
`dotnet ef` and committed. They run on startup only where `Database:MigrateOnStartup` is
true — development and tests — and as a release step everywhere else.

**The unit of work is the DbContext.** Repository and outbox both stage into the scoped
`FundingDbContext`; the handler calls `IUnitOfWork.CommitAsync` once, which is one
`SaveChanges` and therefore one transaction. There is no way to write a deposit without its
message from inside the handler.

**Idempotency keys are a table whose primary key is the claim.** Two concurrent requests
with the same key race on the insert; PostgreSQL's unique violation (23505) tells the loser
to return 409. A claim that never completes — the process died mid-request — is takeable
after a 60-second grace, so a crash cannot pin a key forever. Completing the key is a
separate write from committing the deposit: the window between them is what the grace
period covers.

**A polling relay with `FOR UPDATE SKIP LOCKED`.** A hosted service selects a batch of
unsent rows under a row lock, publishes each with publisher confirms, marks it sent or
records the failure, and commits. Several instances can run at once without double
publishing; a row whose publish fails stays for the next pass with its error and attempt
count visible.

**Optimistic concurrency on `xmin`.** A `uint` row-version property mapped to PostgreSQL's
system column, so two handlers moving the same deposit cannot both win.

## Alternatives considered

- **Dapper and hand-written SQL.** Less machinery and closer to the metal; rejected because
  migrations and the model-as-documentation are worth more here than the control, and the
  hot paths are not the ORM's fault at this scale.
- **`LISTEN`/`NOTIFY` to wake the relay instead of polling.** Lower latency, more moving
  parts, and it does not remove the need for the polling loop as a fallback. Polling at
  500 ms is adequate for a funding pipeline; revisit if latency becomes the constraint.
- **Change-data-capture (Debezium) instead of a relay.** The right answer at a larger
  scale; rejected as in ADR-0003 for adding a component the reference system does not need.
- **Storing the idempotency response inside the deposit transaction.** Would close the
  grace-period window, but couples the HTTP layer to the aggregate's transaction. The
  grace period is the simpler contract.

## Consequences

Two configurations exist and both are tested: no connection string means in-memory stores
(fast tests, first run); a connection string means PostgreSQL, the relay, and RabbitMQ if
configured. Integration tests use Testcontainers, so they need Docker — locally and in CI.
The relay's ordering guarantee is per batch only; consumers key on message id, not order.
