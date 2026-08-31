# Deploying Remit to AKS

The whole path is `infra/bicep/main.bicep` → two `docker build`s → `helm upgrade`. The GitHub
workflow `deploy.yml` runs exactly that with OIDC login; this is the same by hand.

```sh
# 1. Infrastructure (~10 min the first time). Outputs feed step 3.
az group create -n rg-remit-dev -l westeurope
export REMIT_DEPLOYER_OBJECT_ID=$(az ad signed-in-user show --query id -o tsv)
export REMIT_PG_PASSWORD=$(openssl rand -base64 24)
az deployment group create -g rg-remit-dev -n remit -f infra/bicep/main.bicep -p infra/bicep/main.bicepparam -o json > out.json

# 2. Images, built in the registry (no local Docker needed).
ACR=$(jq -r .properties.outputs.acrLoginServer.value out.json | cut -d. -f1)
az acr build -r $ACR -t remit/funding:v1 -f src/Services/Remit.Funding/Dockerfile .
az acr build -r $ACR -t remit/ledger:v1  -f src/Services/Remit.Ledger/Dockerfile .
az acr build -r $ACR -t remit/reconciliation:v1 -f src/Services/Remit.Reconciliation/Dockerfile .

# 3. Release. Migrations run as pre-upgrade Jobs; pods start only after they succeed.
az aks get-credentials -g rg-remit-dev -n $(jq -r .properties.outputs.clusterName.value out.json)
helm upgrade --install remit deploy/helm/remit -n remit --create-namespace --wait \
  --set image.registry=$(jq -r .properties.outputs.acrLoginServer.value out.json) \
  --set image.tag=v1 \
  --set identity.clientId=$(jq -r .properties.outputs.workloadIdentityClientId.value out.json) \
  --set identity.tenantId=$(jq -r .properties.outputs.tenantId.value out.json) \
  --set keyVault.name=$(jq -r .properties.outputs.keyVaultName.value out.json)

# 4. Look at it.
kubectl -n remit get pods
kubectl -n remit port-forward svc/funding 5000:80 &
kubectl -n remit port-forward svc/jaeger 16686:16686 &
```

Where secrets live and who can read them:

| Secret | Written by | Read by | How |
|---|---|---|---|
| `ConnectionStrings--Funding` / `--Ledger` / `--Reconciliation` | Bicep, into Key Vault | the three services, migrate jobs | CSI driver mounts and syncs to `remit-secrets`; pods use workload identity, no credential anywhere |
| `Psp--Providers--*--WebhookSecret` | Bicep placeholder; rotate in the vault | funding | same |
| PostgreSQL admin password | you, at deploy time | Bicep only | never stored outside Key Vault |
| ACR pull | — | kubelet | `AcrPull` role on the cluster's kubelet identity |

Nothing in this repository, the chart, or the cluster's YAML contains a credential.
