# Azure Dataset Storage

Issue #133 introduces the first cloud storage target for published POIneer datasets. The
renderer can still run on the VPS; Azure Blob Storage provides an independently managed
destination for validated dataset artifacts.

```text
VPS Renderer
     |
     | upload validated dataset artifact
     v
Azure Blob Storage
```

## Current Scope

This workflow provisions only storage:

- one Azure Storage Account
- one private Blob container for dataset artifacts
- cost-conscious local-redundant storage defaults
- secure access defaults suitable for a future automated publisher

It does not provision Azure compute, CDN, Azure Front Door, mobile direct-download behavior,
cleanup jobs, geo-redundant disaster recovery, or production secrets.

## Current Dev Resources

The current manually created development resources are:

| Resource | Value |
| --- | --- |
| Resource group | `rg-poineer-dev` |
| Storage account | `poineerstoragedev` |
| Location | `westeurope` |
| Dataset container | `regions` |

`infra/azure/storage.dev.bicepparam` records this current dev shape so the storage resources
can be managed reproducibly later without introducing a different account or container name.

## Naming Strategy

The Bicep template derives the storage account name from:

```text
st{projectName}{environmentName}{uniqueSuffix}
```

When no explicit name is supplied, this becomes a globally unique name shaped like:

```text
stpoineerprodxxxxxxxx
```

The suffix is generated from the subscription, resource group, project name, and environment.
This keeps names deterministic inside the same resource group while satisfying Azure Storage
Account global uniqueness requirements.

For existing environments, pass `storageAccountName` explicitly. The current dev parameter
file uses `poineerstoragedev`.

The dataset container name is also parameterized. The current dev container is `regions`.

## Region

The current dev storage account is in `westeurope`. The production parameter file currently
uses `westeurope` as an initial placeholder. Both values are parameters and can be
changed before deployment if the target subscription or organization prefers another Azure
region.

## Security Defaults

The template configures the storage account and container with conservative defaults:

- anonymous Blob public access disabled at the storage account level
- container public access set to `None`
- HTTPS-only traffic
- minimum TLS version `TLS1_2`
- shared-key access disabled
- OAuth authentication preferred by default
- cross-tenant replication disabled
- hierarchical namespace disabled
- NFS v3 disabled
- Blob versioning disabled
- Blob change feed disabled
- 7-day soft delete for blobs and containers

The public network endpoint remains enabled for the initial hybrid architecture because the
renderer may run from the VPS outside Azure. Authorization should use Microsoft Entra ID and
RBAC rather than account keys. A future hardening pass can add firewall rules or private
networking once the upload identity and network path are known.

Azure Files settings are intentionally not managed by this template because POIneer datasets
are published as Blob artifacts. File share configuration can be added later if the project
starts using Azure Files directly.

## Cost-Conscious Defaults

The initial template uses:

- `StorageV2`
- `Standard_LRS`
- Hot access tier
- no geo-replication
- no CDN or Front Door

Regional SQLite datasets are expected to be reproducible from OSM input, so locally redundant
storage is acceptable for the first cloud distribution target. This can be revisited when
dataset availability requirements become stricter.

## Deploy

Create or choose a resource group, then deploy the template:

Bash:

```bash
az group create \
  --name rg-poineer-dev \
  --location westeurope
```

PowerShell:

```powershell
az group create `
  --name rg-poineer-dev `
  --location westeurope
```

Bash:

```bash
az deployment group what-if \
  --resource-group rg-poineer-dev \
  --template-file infra/azure/storage.bicep \
  --parameters infra/azure/storage.dev.bicepparam
```

PowerShell:

```powershell
az deployment group what-if `
  --resource-group rg-poineer-dev `
  --template-file infra/azure/storage.bicep `
  --parameters infra/azure/storage.dev.bicepparam
```

Bash:

```bash
az deployment group create \
  --resource-group rg-poineer-dev \
  --template-file infra/azure/storage.bicep \
  --parameters infra/azure/storage.dev.bicepparam
```

PowerShell:

```powershell
az deployment group create `
  --resource-group rg-poineer-dev `
  --template-file infra/azure/storage.bicep `
  --parameters infra/azure/storage.dev.bicepparam
```

Deploying the template against an existing storage account updates that account to match the
documented security and cost defaults. Review RBAC assignments and Shared Key usage before
adopting an existing manually created account.

The deployment outputs:

- `storageAccountName`
- `datasetContainerName`
- `blobEndpoint`

Do not commit credentials, account keys, SAS tokens, connection strings, or tenant-specific
secrets. Publisher authentication should be configured outside source control.

## Verify

Confirm that the storage account and container exist:

Bash:

```bash
az storage account show \
  --resource-group rg-poineer-dev \
  --name poineerstoragedev \
  --query "{name:name, location:location, allowBlobPublicAccess:allowBlobPublicAccess, allowSharedKeyAccess:allowSharedKeyAccess, httpsOnly:supportsHttpsTrafficOnly}" \
  --output table
```

PowerShell:

```powershell
az storage account show `
  --resource-group rg-poineer-dev `
  --name poineerstoragedev `
  --query "{name:name, location:location, allowBlobPublicAccess:allowBlobPublicAccess, allowSharedKeyAccess:allowSharedKeyAccess, httpsOnly:supportsHttpsTrafficOnly}" `
  --output table
```

Bash:

```bash
az storage container show \
  --account-name poineerstoragedev \
  --name regions \
  --auth-mode login \
  --query "{name:name, publicAccess:properties.publicAccess}" \
  --output table
```

PowerShell:

```powershell
az storage container show `
  --account-name poineerstoragedev `
  --name regions `
  --auth-mode login `
  --query "{name:name, publicAccess:properties.publicAccess}" `
  --output table
```

The expected container public access value is `None`.

## Future Publisher Use

The Azure Blob publisher can use this storage account as its target behind the existing
`IDatasetPublisher` abstraction. It uploads only validated dataset artifacts and pairs with
a blob-specific `IPublishedDatasetVerifier` implementation so a dataset is not considered
successfully published until the destination metadata has been verified.

The Azure publisher uses deterministic blob names such as
`{regionId}/{leafRegionId}.{version}.sqlite` and reads existing blob metadata before
uploading. `regionId` may be a globally unique, hierarchical region identifier (ADR 0007),
e.g. `geofabrik/europe/germany/berlin`; `leafRegionId` is only its last segment
(`berlin`), so a hierarchical id becomes a blob "virtual folder" prefix without also
repeating the full hierarchy inside the file name itself, e.g.
`geofabrik/europe/germany/berlin/berlin.2-d790344ff0a3cbfe.sqlite`. For a flat,
non-hierarchical id the leaf segment equals the id, so this produces the same blob name
as before. When the destination metadata already matches the source artifact metadata,
the upload is skipped without creating duplicate timestamp-based blobs. When a blob for
the same region/version exists but its metadata differs from the source artifact, the
default `SkipIfIdentical` overwrite policy fails the publish instead of replacing the
blob. Replacing an existing blob requires the explicit `Overwrite` policy. Safety limits
such as `AzureBlobPublisher:MaxUploadsPerRun` and `AzureBlobPublisher:MaxUploadBytesPerRun`
guard against runaway publishing behavior within a single renderer run.

Keep checked-in environment configuration on `Publisher:Target = Local` until the deployment
is intentionally switched over. For a local development smoke test against the dev storage
account, prefer temporary environment or command-line overrides instead of committing the
target change:

```powershell
dotnet run --project src/POIneer.Render `
  --Publisher:Target=AzureBlob `
  --AzureBlobPublisher:AccountName=poineerstoragedev `
  --AzureBlobPublisher:ContainerName=regions
```

Authentication uses Azure-supported identity mechanisms. Do not commit account keys,
connection strings, SAS tokens, client secrets, or tenant-specific credentials.

The renderer compute environment remains independent. The same storage target can receive
artifacts from the VPS renderer now and from an Azure-based renderer later.
