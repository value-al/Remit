// Remit on AKS (ADR-0008). Resource-group scope; one file, read top to bottom.
//
//   az group create -n rg-remit-dev -l westeurope
//   az deployment group create -g rg-remit-dev -f main.bicep -p main.bicepparam
//
// What it creates: Log Analytics, Container Registry, Key Vault (RBAC), PostgreSQL Flexible
// Server, an AKS cluster with workload identity and the Key Vault CSI driver, and one
// user-assigned identity that the Remit pods run as. No secret ever lands in a pod spec:
// connection strings are written to Key Vault here and mounted by the CSI driver there.

targetScope = 'resourceGroup'

@description('Short name used as a prefix for every resource, e.g. remit-dev.')
@minLength(3)
@maxLength(16)
param name string

@description('Azure region.')
param location string = resourceGroup().location

@description('Kubernetes version for the cluster. Leave empty for the region default.')
param kubernetesVersion string = ''

@description('Node count of the system pool.')
@minValue(1)
@maxValue(10)
param nodeCount int = 2

@description('VM size of the system pool.')
param nodeSize string = 'Standard_B4ms'

@description('PostgreSQL administrator login.')
param postgresAdmin string = 'remitadmin'

@secure()
@description('PostgreSQL administrator password. Passed at deploy time; stored only in Key Vault.')
param postgresPassword string

@description('Kubernetes namespace and service account the workloads run as; the federated credential is bound to them.')
param workloadNamespace string = 'remit'
param workloadServiceAccount string = 'remit-workload'

@description('Object id of the deployer, granted Key Vault Secrets Officer so the deploy can write secrets.')
param deployerObjectId string

var suffix = uniqueString(resourceGroup().id)
var acrName = replace('${name}acr${suffix}', '-', '')
var kvName = take('${name}-kv-${suffix}', 24)
var pgName = '${name}-pg-${suffix}'

// ---------------------------------------------------------------------------------------------
// Observability
// ---------------------------------------------------------------------------------------------

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${name}-logs'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

// ---------------------------------------------------------------------------------------------
// Images
// ---------------------------------------------------------------------------------------------

resource acr 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' = {
  name: acrName
  location: location
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false // pull is by managed identity, never by admin credentials
  }
}

// ---------------------------------------------------------------------------------------------
// Identity the pods run as
// ---------------------------------------------------------------------------------------------

resource workloadIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${name}-workload'
  location: location
}

// ---------------------------------------------------------------------------------------------
// Secrets
// ---------------------------------------------------------------------------------------------

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: kvName
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true // roles, not access policies
    enablePurgeProtection: true
    softDeleteRetentionInDays: 30
    publicNetworkAccess: 'Enabled' // tighten to private endpoint for production
  }
}

// Key Vault Secrets User — the workload identity may read secrets, nothing else.
resource kvSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, workloadIdentity.id, 'kv-secrets-user')
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: workloadIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Key Vault Secrets Officer — the deployer writes the connection strings below.
resource kvSecretsOfficer 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, deployerObjectId, 'kv-secrets-officer')
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7')
    principalId: deployerObjectId
    principalType: 'User'
  }
}

// ---------------------------------------------------------------------------------------------
// Database — one server, one database, two schemas (ADR-0005)
// ---------------------------------------------------------------------------------------------

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2023-12-01-preview' = {
  name: pgName
  location: location
  sku: { name: 'Standard_B1ms', tier: 'Burstable' }
  properties: {
    version: '16'
    administratorLogin: postgresAdmin
    administratorLoginPassword: postgresPassword
    storage: { storageSizeGB: 32 }
    backup: { backupRetentionDays: 7, geoRedundantBackup: 'Disabled' }
    highAvailability: { mode: 'Disabled' }
    network: { publicNetworkAccess: 'Enabled' } // VNet integration for production
  }
}

resource remitDb 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2023-12-01-preview' = {
  parent: postgres
  name: 'remit'
  properties: { charset: 'UTF8', collation: 'en_US.utf8' }
}

// Allow Azure services (the cluster's egress) to reach the server. Replace with VNet rules in production.
resource pgAllowAzure 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2023-12-01-preview' = {
  parent: postgres
  name: 'AllowAzureServices'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

var pgHost = postgres.properties.fullyQualifiedDomainName
var connectionString = 'Host=${pgHost};Port=5432;Database=remit;Username=${postgresAdmin};Password=${postgresPassword};SSL Mode=Require'

resource secretFunding 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'ConnectionStrings--Funding'
  properties: { value: connectionString }
  dependsOn: [kvSecretsOfficer]
}

resource secretLedger 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'ConnectionStrings--Ledger'
  properties: { value: connectionString }
  dependsOn: [kvSecretsOfficer]
}

// Provider webhook secrets are placeholders here; rotate them in the vault, never in code.
resource secretAlpha 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'Psp--Providers--alpha--WebhookSecret'
  properties: { value: 'whsec_alpha_${suffix}' }
  dependsOn: [kvSecretsOfficer]
}

resource secretBeta 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'Psp--Providers--beta--WebhookSecret'
  properties: { value: 'whsec_beta_${suffix}' }
  dependsOn: [kvSecretsOfficer]
}

// ---------------------------------------------------------------------------------------------
// Cluster
// ---------------------------------------------------------------------------------------------

resource aks 'Microsoft.ContainerService/managedClusters@2024-09-01' = {
  name: '${name}-aks'
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    dnsPrefix: name
    kubernetesVersion: empty(kubernetesVersion) ? null : kubernetesVersion
    agentPoolProfiles: [
      {
        name: 'system'
        mode: 'System'
        count: nodeCount
        vmSize: nodeSize
        osType: 'Linux'
        osSKU: 'AzureLinux'
        enableAutoScaling: false
      }
    ]
    networkProfile: {
      networkPlugin: 'azure'
      networkPluginMode: 'overlay'
      networkPolicy: 'cilium'
      networkDataplane: 'cilium'
    }
    // Workload identity: pods get an Entra token through a federated Kubernetes service account. No secrets.
    oidcIssuerProfile: { enabled: true }
    securityProfile: { workloadIdentity: { enabled: true } }
    addonProfiles: {
      // Secrets Store CSI driver with the Azure Key Vault provider: secrets mount as files / sync to a k8s Secret.
      azureKeyvaultSecretsProvider: {
        enabled: true
        config: { enableSecretRotation: 'true', rotationPollInterval: '2m' }
      }
      omsagent: {
        enabled: true
        config: { logAnalyticsWorkspaceResourceID: logs.id }
      }
    }
    apiServerAccessProfile: { enablePrivateCluster: false }
  }
}

// The kubelet pulls images from the registry by identity.
resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, aks.id, 'acrpull')
  scope: acr
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalId: aks.properties.identityProfile.kubeletidentity.objectId
    principalType: 'ServicePrincipal'
  }
}

// Bind the Kubernetes service account to the workload identity. Tokens issued by the cluster's
// OIDC issuer for this subject are exchanged for this identity's Entra tokens.
resource federatedCredential 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: workloadIdentity
  name: 'aks-${workloadNamespace}-${workloadServiceAccount}'
  properties: {
    issuer: aks.properties.oidcIssuerProfile.issuerURL
    subject: 'system:serviceaccount:${workloadNamespace}:${workloadServiceAccount}'
    audiences: ['api://AzureADTokenExchange']
  }
}

// ---------------------------------------------------------------------------------------------
// Outputs the Helm values need
// ---------------------------------------------------------------------------------------------

output clusterName string = aks.name
output acrLoginServer string = acr.properties.loginServer
output keyVaultName string = keyVault.name
output workloadIdentityClientId string = workloadIdentity.properties.clientId
output tenantId string = subscription().tenantId
output oidcIssuer string = aks.properties.oidcIssuerProfile.issuerURL
output postgresHost string = pgHost
