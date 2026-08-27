# C4 — Level 1 (context) and Level 2 (containers)

Drawn with Mermaid so it renders on GitHub and changes in pull requests.

## Context

```mermaid
flowchart LR
    client([Trading client<br/>web / mobile])
    ops([Operations<br/>finance & support])
    psp1[(PSP A<br/>cards)]
    psp2[(PSP B<br/>bank transfer)]
    bank[(Bank statements)]

    remit[Remit<br/>money-movement backend]

    client -- deposits, withdrawals --> remit
    ops -- exceptions, approvals --> remit
    remit -- charge / payout --> psp1
    remit -- charge / payout --> psp2
    psp1 -- webhooks --> remit
    psp2 -- webhooks --> remit
    bank -- statements (SFTP/API) --> remit
```

## Containers

```mermaid
flowchart TB
    subgraph edge [Edge]
        gw[API gateway<br/>auth, rate limits]
    end

    subgraph services [Services — .NET 10]
        funding[Funding<br/>deposits & withdrawals<br/>state machines, idempotency]
        psp[PSP adapters<br/>routing, per-provider<br/>Countersign-verified webhooks]
        ledger[Ledger<br/>double-entry journal]
        recon[Reconciliation<br/>statement matching,<br/>exception queue]
    end

    subgraph infra [Infrastructure]
        pg[(PostgreSQL<br/>one schema per service<br/>+ outbox tables)]
        mq{{RabbitMQ}}
        redis[(Redis<br/>reference data, rate limits)]
        otel[OpenTelemetry collector<br/>→ Jaeger / Prometheus]
    end

    gw --> funding
    gw --> recon
    funding --> psp
    funding --> pg
    psp --> pg
    ledger --> pg
    recon --> pg
    funding -. outbox relay .-> mq
    psp -. outbox relay .-> mq
    mq --> ledger
    mq --> recon
    funding --> redis
    services --> otel
```

**PCI scope.** No container in this diagram handles card data; PSP adapters exchange
tokens and references only. The scope boundary is therefore the PSP's hosted page or
SDK, outside Remit. That is a design decision, not an accident — see ADR-0001.

Level 3 (components) is drawn per service as each one lands.
