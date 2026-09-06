targetScope = 'resourceGroup'

@description('Short project name used for resource naming and tags.')
param projectName string = 'poineer'

@allowed([
  'dev'
  'test'
  'prod'
])
@description('Deployment environment name used for resource naming and tags.')
param environmentName string = 'prod'

@description('Azure region for the storage account.')
param location string = resourceGroup().location

@maxLength(24)
@description('Optional explicit storage account name (3-24 chars, lowercase letters and numbers only). Leave empty to derive a deterministic globally unique name.')
param storageAccountName string = ''

@description('Blob container that stores published POIneer dataset artifacts.')
param datasetContainerName string = 'datasets'

@description('Optional resource tags.')
param tags object = {}

var normalizedProjectName = toLower(replace(projectName, '-', ''))
var normalizedEnvironmentName = toLower(replace(environmentName, '-', ''))
var uniqueSuffix = take(uniqueString(subscription().id, resourceGroup().id, projectName, environmentName), 8)
var resolvedStorageAccountName = empty(storageAccountName)
  ? take('st${normalizedProjectName}${normalizedEnvironmentName}${uniqueSuffix}', 24)
  : storageAccountName

resource datasetStorageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: resolvedStorageAccountName
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  tags: union(tags, {
    project: projectName
    environment: environmentName
    workload: 'dataset-storage'
    managedBy: 'bicep'
  })
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowCrossTenantReplication: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    isHnsEnabled: false
    isNfsV3Enabled: false
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Enabled'
    supportsHttpsTrafficOnly: true
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: datasetStorageAccount
  name: 'default'
  properties: {
    changeFeed: {
      enabled: false
    }
    deleteRetentionPolicy: {
      enabled: true
      days: 7
      allowPermanentDelete: false
    }
    containerDeleteRetentionPolicy: {
      enabled: true
      days: 7
    }
    isVersioningEnabled: false
  }
}

resource datasetContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: toLower(datasetContainerName)
  properties: {
    defaultEncryptionScope: '$account-encryption-key'
    denyEncryptionScopeOverride: false
    publicAccess: 'None'
  }
}

output storageAccountName string = datasetStorageAccount.name
output datasetContainerName string = datasetContainer.name
output blobEndpoint string = datasetStorageAccount.properties.primaryEndpoints.blob
