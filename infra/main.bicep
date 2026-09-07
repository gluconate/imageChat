targetScope = 'resourceGroup'

param location string = resourceGroup().location
param environmentName string = 'imagechat'
@description('Object ID of the deployment operator, scoped only to this application resources.')
param operatorPrincipalId string

var resourceToken = uniqueString(subscription().id, resourceGroup().id, location, environmentName)
var tags = { application: 'imagechat', environment: environmentName }
var storageRoles = [
  'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
  '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
  '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
]

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2025-05-31-preview' = {
  name: 'azid${resourceToken}'
  location: location
  tags: tags
}

resource storage 'Microsoft.Storage/storageAccounts@2026-04-01' = {
  name: 'azst${resourceToken}'
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Enabled'
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2026-04-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: { enabled: true, days: 7 }
    containerDeleteRetentionPolicy: { enabled: true, days: 7 }
  }
}
resource containers 'Microsoft.Storage/storageAccounts/blobServices/containers@2026-04-01' = [for container in ['images', 'work', 'keys']: {
  parent: blobService
  name: container
  properties: { publicAccess: 'None' }
}]
resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2026-04-01' = {
  parent: storage
  name: 'default'
}
resource table 'Microsoft.Storage/storageAccounts/tableServices/tables@2026-04-01' = {
  parent: tableService
  name: 'imagechat'
}
resource queueService 'Microsoft.Storage/storageAccounts/queueServices@2026-04-01' = {
  parent: storage
  name: 'default'
}
resource queue 'Microsoft.Storage/storageAccounts/queueServices/queues@2026-04-01' = {
  parent: queueService
  name: 'generation'
}
resource appStorageAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for role in storageRoles: {
  name: guid(storage.id, identity.id, role)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', role)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}]
resource operatorStorageAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for role in storageRoles: {
  name: guid(storage.id, operatorPrincipalId, role)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', role)
    principalId: operatorPrincipalId
    principalType: 'User'
  }
}]

resource registry 'Microsoft.ContainerRegistry/registries@2026-09-01-preview' = {
  name: 'azcr${resourceToken}'
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: { adminUserEnabled: false, anonymousPullEnabled: false, publicNetworkAccess: 'Enabled' }
}
resource imagePull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, identity.id, 'pull')
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource vault 'Microsoft.KeyVault/vaults@2026-03-01-preview' = {
  name: 'azkv${resourceToken}'
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    enablePurgeProtection: true
    softDeleteRetentionInDays: 7
    publicNetworkAccess: 'Enabled'
  }
}
resource encryptionKey 'Microsoft.KeyVault/vaults/keys@2026-03-01-preview' = {
  parent: vault
  name: 'sessionkeys'
  properties: {
    kty: 'RSA'
    keySize: 2048
    keyOps: ['wrapKey', 'unwrapKey']
    attributes: { enabled: true }
  }
}
resource appVaultAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for role in [
  '4633458b-17de-408a-b874-0445c86b69e6'
  'e147488a-f6f5-4113-8e2d-b22465e65bf6'
]: {
  name: guid(vault.id, identity.id, role)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', role)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}]
resource secretOperator 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, operatorPrincipalId, 'secrets')
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7')
    principalId: operatorPrincipalId
    principalType: 'User'
  }
}

// Consumption only, no dedicated profile, NAT gateway, VM, or mounted file share.
resource appEnvironment 'Microsoft.App/managedEnvironments@2026-03-02-preview' = {
  name: 'azenv${resourceToken}'
  location: location
  tags: tags
  properties: {
    workloadProfiles: [{ name: 'Consumption', workloadProfileType: 'Consumption' }]
  }
}

output storageName string = storage.name
output registryName string = registry.name
output registryServer string = registry.properties.loginServer
output environmentName string = appEnvironment.name
output identityName string = identity.name
output vaultName string = vault.name
output keyUri string = '${vault.properties.vaultUri}keys/${encryptionKey.name}'