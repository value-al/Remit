# Threat model — the deposit flow (STRIDE)

Scope: one deposit, from the client's `POST /deposits` to the wallet balance in the ledger,
as deployed by ADR-0008. Out of scope: the client application itself, the provider's own
systems, and the Azure control plane (both are trust boundaries, not components).

## The system, with trust boundaries

```
 ┌─ Internet ───────────────────────────────────────────────────────────────────┐
 │  Client app                       Payment provider (PSP)                     │
 └────┬──────────────────────────────────────┬──────────────────────────────────┘
      │ [B1] TLS, gateway auth               │ [B2] TLS, signed webhook
 ┌────▼──────────────────────────────────────▼──────────────────────────────────┐
 │  AKS namespace "remit"                                                        │
 │   funding ──(outbox, same tx)──► PostgreSQL funding.*                         │
 │      │ relay                                                                  │
 │      ▼ [B3] AMQP, in-cluster                                                  │
 │   RabbitMQ ──► ledger ──► PostgreSQL ledger.*                                  │
 │            └─► reconciliation ──► PostgreSQL reconciliation.*                 │
 │   secrets: Key Vault ──(CSI, workload identity)──► pods            [B4]       │
 └───────────────────────────────────────────────────────────────────────────────┘
```

Assets, in order of what an attacker wants: (1) the ability to create wallet balance that
was never funded; (2) the ability to move a client's balance out; (3) provider webhook and
database secrets; (4) the audit trail — journal and reconciliation records; (5) availability
of deposits.

## Threats and what is in place

Each row: the threat, the control that exists today in this repository, and what is left.
"Residual" is honest; several rows end in a decision, not a control.

### Spoofing

| Threat | Control in place | Residual / next |
|---|---|---|
| S1. Forged provider webhook settles a deposit that never funded | Countersign verification over the raw bytes with a per-provider *webhook* secret, `timestamp.body` canonical form, before parsing (ADR-0006). Constant-time comparison. Forged key → 401, tested. | Secrets are placeholders written by Bicep; rotation is manual in the vault. Add a rotation runbook and dual-key acceptance (Countersign supports multiple candidate signatures). |
| S2. Provider A's genuine key used to settle a deposit routed to provider B | Deposit records its provider and reference; a mismatch is acknowledged and not applied, tested. | None significant. |
| S3. Client impersonation on `POST /deposits` | Out of scope here: the API gateway ([B1]) authenticates clients; Funding trusts `accountId` from the gateway. | **Gap:** Funding has no authentication of its own. It must never be reachable except through the gateway; a NetworkPolicy that only admits the gateway is the concrete next step. |
| S4. A pod impersonating another pod to the vault | Workload identity: tokens are issued to the federated service account only; the identity holds *Secrets User* on one vault. | All three services share one identity and therefore see each other's connection strings. Split into one identity per service when there is more than one team. |

### Tampering

| Threat | Control in place | Residual / next |
|---|---|---|
| T1. Webhook body altered in transit | Signature over the exact received bytes; TLS at [B2]. | None. |
| T2. Replayed webhook applied twice | Five-minute timestamp tolerance in the verifier; the state machine's closed edges (duplicate → `applied:false`), tested; the ledger's inbox on message id. | Within the five-minute window a replay is *accepted* at the edge and rejected by the state machine — correct, but relies on the aggregate being final. Storing provider event ids would close it fully (noted in ADR-0006). |
| T3. Tampering with journal entries in the database | Entries are immutable by construction in code; corrections are new entries (ADR-0004). PostgreSQL access is by one application role. | Nothing stops a DBA. Row-level append-only enforcement (revoke UPDATE/DELETE on `ledger.postings` from the app role) is a one-line migration and should be done. |
| T4. Message tampering on the broker | In-cluster, cluster-network only. | AMQP is plaintext inside the cluster. Acceptable with Cilium network policy; use TLS or Service Bus when the broker leaves the cluster. |
| T5. Tampering with the outbox to inject a settlement | Same database role as Funding; anyone who can write `funding.outbox` can already write `funding.deposits`. | Covered by T3's answer: least-privilege database roles per service. |

### Repudiation

| Threat | Control in place | Residual / next |
|---|---|---|
| R1. "We never received that webhook" / "you settled it twice" | Every transition recorded with a timestamp (`deposit_transitions`); every webhook decision logged with the provider's event id; one trace per movement (ADR-0007). | Logs and traces are in Log Analytics / Jaeger with default retention. Set retention to match the regulatory record-keeping period (typically 5–7 years for financial records) and export to immutable storage. |
| R2. An operator resolves a reconciliation exception without justification | Resolution requires written text and happens once; the record keeps who-when-why (ADR-0009). | "Who" is not captured yet — the endpoint has no authenticated principal. Add the operator identity from the gateway's headers. |
| R3. Disputed balance | Balance is derived from the journal on every read; `GET /entries?correlationId=` shows every posting behind a movement. | None. |

### Information disclosure

| Threat | Control in place | Residual / next |
|---|---|---|
| I1. Card data exposure | No card data exists in the system: providers tokenise; Remit holds references only (ADR-0001). PCI scope boundary drawn in the C4. | None; keep it that way — it is the single most valuable property of the design. |
| I2. Secrets in git, images, or pod specs | None anywhere: Key Vault + CSI + workload identity (ADR-0008); `nuget.config` pins nuget.org so builds never touch private feeds. | RabbitMQ password sits in `values.yaml` for the reference deployment — move it to the vault before any real use. |
| I3. Secrets in logs | Idempotency keys and references are logged; secrets and bodies are not. | Webhook bodies are not logged today; keep the rule explicit in a logging policy. |
| I4. Balance enumeration via `GET /accounts/{id}/balance` | Account ids are GUIDs. | The ledger endpoint has no authorization; it is behind the gateway only. Same fix as S3. |
| I5. Database exposure | PostgreSQL Flexible Server allows Azure services in the reference deployment. | VNet integration and a private endpoint before production, as ADR-0008 says. |

### Denial of service

| Threat | Control in place | Residual / next |
|---|---|---|
| D1. Webhook flood | Verification before parsing, so unsigned traffic costs one HMAC; providers' retries are bounded by always answering 2xx on duplicates. | No rate limit on the endpoint itself; add one at the gateway per provider. |
| D2. Idempotency-key exhaustion (a client pins many keys) | Keys are per row with a claim; stale claims expire after 60 s. | No expiry on *completed* keys yet — the table grows forever. Add a 24-hour retention job (ADR-0005 anticipates it). |
| D3. Poison message stalls the ledger | Dead-letter after two failures; prefetch 16 bounds in-flight work (ADR-0007). | Dead-lettered messages need a person; the SLO document's alert covers it. |
| D4. Provider outage | Router falls through outages by health window (ADR-0006). | All providers down → deposits fail fast with a reason; that is the intended behaviour. |
| D5. Resource exhaustion in the cluster | Requests/limits on every pod; two replicas for the request path. | No HPA; add one on CPU/RPS when load is real. |

### Elevation of privilege

| Threat | Control in place | Residual / next |
|---|---|---|
| E1. Container breakout | Non-root, read-only root filesystem, all capabilities dropped, `RuntimeDefault` seccomp, no privilege escalation (ADR-0008). | AppArmor/SELinux profiles are cluster defaults; no custom profile. Acceptable. |
| E2. A compromised pod reaching the vault for more than it needs | Identity holds *Secrets User* only; no keys, no certificates, no management-plane role. | See S4: one identity for three services. |
| E3. A compromised pod reaching the Kubernetes API | Default service account tokens are auto-mounted. | Set `automountServiceAccountToken: false` on the workload service account except where the CSI driver needs the projected token — verify and tighten. |
| E4. Migration job with more database rights than the services | Same connection string for both today. | Give migrations a DDL role and the services a DML-only role; this also answers T3/T5. |

## The five things to do first

In the order that removes the most risk per hour, all small:

1. **Database roles:** a DDL role for migrations, DML-only roles per service, and revoke
   UPDATE/DELETE on `ledger.postings` and `ledger.journal_entries` (T3, T5, E4).
2. **NetworkPolicy** admitting only the gateway to the services' HTTP ports and only the
   services to RabbitMQ (S3, I4, T4).
3. **Operator identity** on the reconciliation resolve endpoint, from the gateway (R2).
4. **Retention** for traces/logs aligned with the record-keeping obligation, and an
   idempotency-key retention job (R1, D2).
5. **One identity per service** in Bicep, and the RabbitMQ password into the vault (S4, E2, I2).

None of these change the money path. All of them are the difference between a reference
system and one a regulator would let hold client funds.
