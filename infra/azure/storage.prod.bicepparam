using './storage.bicep'

param projectName = 'poineer'
param environmentName = 'prod'
param location = 'westeurope'
param datasetContainerName = 'regions'
param tags = {
  application: 'POIneer'
  component: 'datasets'
}
