using './storage.bicep'

param projectName = 'poineer'
param environmentName = 'dev'
param location = 'westeurope'
param storageAccountName = 'poineerstoragedev'
param datasetContainerName = 'regions'
param tags = {
  application: 'POIneer'
  component: 'datasets'
}
