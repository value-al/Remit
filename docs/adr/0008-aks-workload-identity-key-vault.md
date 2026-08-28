# ADR-0008: AKS by Bicep, identity instead of credentials, migrations before pods

**Status:** Accepted · **Date:** 2026-08-28

## Context

The reference system has to run somewhere other than a laptop, and how it gets there is
itself a set of decisions a regulator will ask about: where secrets live, who can read
them, how schema changes reach production, what the pods are allowed to do.

## Decision

**Everything is declared in one Bicep file at resource-group scope.** Log Analytics,
Container Registry, Key Vault (RBAC mode, purge protection), PostgreSQL Flexible Server,
the AKS cluster and a user-assigned identity, in `infra/bicep/main.bicep`, read top to
bottom. Parameters in `main.bicepparam`; the only secret parameter is the database
password, supplied from the environment at deploy time and written straight into Key Vault.

**Pods have an identity, not a credential.** AKS is created with the OIDC issuer and
workload identity enabled. Bicep federates the Kubernetes service account
`remit/remit-workload` to the user-assigned identity; the identity holds exactly one
role on the vault: *Key Vault Secrets User*. The Secrets Store CSI driver (Azure Key Vault
provider, enabled as a cluster add-on) exchanges the pod's projected service-account token
for a vault token, mounts the secrets as files, and mirrors them into a Kubernetes Secret
that the containers read as environment variables. No connection string, key or password
appears in the chart, the workflow, or git. Image pulls use the kubelet identity with
*AcrPull*; the registry's admin user is disabled.

**Migrations run as Helm pre-upgrade Jobs, not on boot.** Both services accept
`--migrate`: apply pending migrations, log which, exit. The chart runs that image and
argument as a hook with weight −5 before rolling the Deployments. A failed migration fails
the release and leaves the previous pods running. `Database:MigrateOnStartup` is `false`
in the cluster and stays `true` only for local development and tests.

**Pods are locked down by default.** Non-root, read-only root filesystem with an
`emptyDir` for `/tmp`, all capabilities dropped, `RuntimeDefault` seccomp, resource
requests and limits, liveness on `/health/live` (never touches dependencies) and readiness
on `/health/ready` (includes the database). Two replicas each.

**The broker and the tracing backend run in-cluster for the reference.** RabbitMQ as a
one-replica StatefulSet with a persistent volume; Jaeger all-in-one for traces. Both are
what the local compose file runs, so the deployed system is the same system.

**Deployment is a manual workflow with OIDC login.** `deploy.yml` authenticates GitHub
Actions to Azure through a federated app registration — again, no stored credential —
deploys the Bicep, builds both images inside ACR with `az acr build`, and runs
`helm upgrade --install --wait` with the Bicep outputs as values.

## Alternatives considered

- **Terraform.** Equally good; Bicep is native to the platform, needs no state backend,
  and reads like the ARM it compiles to. For an Azure-only reference system that wins.
- **Azure Service Bus instead of in-cluster RabbitMQ.** The sensible production choice;
  it would replace `RabbitMqPublisher`/`RabbitMqConsumer` behind the same interfaces.
  Rejected here so the deployed system stays identical to the one the tests cover.
- **Secrets as Kubernetes Secrets created by the pipeline.** Rejected: the pipeline would
  need to read them, they would sit in etcd base64-encoded, and rotation would mean a
  redeploy. The CSI driver polls the vault every two minutes.
- **Migrations on startup in the cluster.** Rejected: N replicas race to migrate, and a
  bad migration takes the whole deployment down with it instead of failing the release.
- **Private cluster and private endpoints.** Correct for production; left as parameters
  and comments here so the reference deploys without a VPN.

## Consequences

Deploying costs money and needs a subscription; the repository validates what it can
without one — `bicep build`, `helm lint`/`template`, and real `docker build`s of both
images — and the workflow is the executable documentation of the rest. PostgreSQL is
reachable from Azure services generally until VNet integration is added. RabbitMQ's
password is in `values.yaml` for the reference and is the one credential that should move
to Key Vault before anyone calls this production.
