param location string = resourceGroup().location
param environmentName string
param identityName string
param storageName string
param vaultName string
param registryName string
@description('Immutable image tag built by the deployment script.')
param imageTag string
param appName string = 'imagechat'
@description('Optional Key Vault configuration version for deterministic web and worker updates.')
param configurationSecretVersion string = ''

resource appEnvironment 'Microsoft.App/managedEnvironments@2026-03-02-preview' existing = { name: environmentName }
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2025-05-31-preview' existing = { name: identityName }
resource vault 'Microsoft.KeyVault/vaults@2026-03-01-preview' existing = { name: vaultName }
resource registry 'Microsoft.ContainerRegistry/registries@2026-09-01-preview' existing = { name: registryName }

var identityConfig = { type: 'UserAssigned', userAssignedIdentities: { '${identity.id}': {} } }
var image = '${registry.properties.loginServer}/imagechat:${imageTag}'
var registries = [{ server: registry.properties.loginServer, identity: identity.id }]
var configurationSecretUrl = '${vault.properties.vaultUri}secrets/foundryconfig${empty(configurationSecretVersion) ? '' : '/${configurationSecretVersion}'}'
var secrets = [{ name: 'foundryconfig', keyVaultUrl: configurationSecretUrl, identity: identity.id }]
var mounts = [{ volumeName: 'configuration', mountPath: '/run/secrets' }]
var volumes = [{ name: 'configuration', storageType: 'Secret', secrets: [{ secretRef: 'foundryconfig', path: 'foundry.json' }] }]
var healthRequest = {
  path: '/healthz'
  port: 8080
  httpHeaders: [{ name: 'Host', value: '${appName}.${appEnvironment.properties.defaultDomain}' }]
}
var commonEnv = [
  { name: 'Storage__Provider', value: 'Azure' }
  { name: 'Storage__AccountName', value: storageName }
  { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
  { name: 'IMAGECHAT_CONFIG_FILE', value: '/run/secrets/foundry.json' }
]

resource web 'Microsoft.App/containerApps@2026-03-02-preview' = {
  name: appName
  location: location
  identity: identityConfig
  properties: {
    managedEnvironmentId: appEnvironment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: true, targetPort: 8080, allowInsecure: false, transport: 'http' }
      registries: registries
      secrets: secrets
    }
    template: {
      containers: [{
        name: 'web'
        image: image
        resources: { cpu: json('0.25'), memory: '0.5Gi' }
        env: concat(commonEnv, [
          { name: 'AllowedHosts', value: '${appName}.${appEnvironment.properties.defaultDomain}' }
          { name: 'Security__RequireHttps', value: 'true' }
          { name: 'ReverseProxy__Provider', value: 'ContainerApps' }
          { name: 'Storage__KeyEncryptionKeyUri', value: '${vault.properties.vaultUri}keys/sessionkeys' }
        ])
        volumeMounts: mounts
        probes: [
          { type: 'Startup', httpGet: healthRequest, initialDelaySeconds: 5, periodSeconds: 5, failureThreshold: 30 }
          { type: 'Readiness', httpGet: healthRequest, periodSeconds: 10 }
          { type: 'Liveness', httpGet: healthRequest, periodSeconds: 30 }
        ]
      }]
      volumes: volumes
      scale: { minReplicas: 0, maxReplicas: 1, rules: [{ name: 'http', http: { metadata: { concurrentRequests: '10' } } }] }
    }
  }
}

resource worker 'Microsoft.App/jobs@2026-03-02-preview' = {
  name: '${appName}-worker'
  location: location
  identity: identityConfig
  properties: {
    environmentId: appEnvironment.id
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Event'
      replicaTimeout: 840
      replicaRetryLimit: 0
      registries: registries
      secrets: secrets
      eventTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
        scale: {
          minExecutions: 0
          maxExecutions: 3
          pollingInterval: 30
          rules: [{
            name: 'generation'
            type: 'azure-queue'
            identity: identity.id
            metadata: { accountName: storageName, queueName: 'generation', queueLength: '1', queueLengthStrategy: 'visibleonly' }
          }]
        }
      }
    }
    template: {
      containers: [{ name: 'worker', image: image, args: ['--worker'], resources: { cpu: json('0.25'), memory: '0.5Gi' }, env: commonEnv, volumeMounts: mounts }]
      volumes: volumes
    }
  }
}

resource dispatcher 'Microsoft.App/jobs@2026-03-02-preview' = {
  name: '${appName}-dispatch'
  location: location
  identity: identityConfig
  properties: {
    environmentId: appEnvironment.id
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Schedule'
      replicaTimeout: 240
      replicaRetryLimit: 0
      registries: registries
      scheduleTriggerConfig: { cronExpression: '*/5 * * * *', parallelism: 1, replicaCompletionCount: 1 }
    }
    template: {
      containers: [{ name: 'dispatcher', image: image, args: ['--dispatch'], resources: { cpu: json('0.25'), memory: '0.5Gi' }, env: filter(commonEnv, variable => variable.name != 'IMAGECHAT_CONFIG_FILE') }]
    }
  }
}

output url string = 'https://${web.properties.configuration.ingress.fqdn}'
output workerName string = worker.name
output dispatcherName string = dispatcher.name