using './main.bicep'

param name = 'remit-dev'
param nodeCount = 2
param nodeSize = 'Standard_B4ms'

// az ad signed-in-user show --query id -o tsv
param deployerObjectId = readEnvironmentVariable('REMIT_DEPLOYER_OBJECT_ID', '00000000-0000-0000-0000-000000000000')

// Never commit a value here. Supply it at deploy time:
//   az deployment group create ... -p postgresPassword="$(openssl rand -base64 24)"
param postgresPassword = readEnvironmentVariable('REMIT_PG_PASSWORD', '')
