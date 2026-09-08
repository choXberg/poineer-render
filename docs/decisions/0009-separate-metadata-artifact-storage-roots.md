# ADR 0009: Separate metadata and artifact storage roots

Status: Accepted (contract decision; implementation pending).

## Context

The original draft manifest contract (#204) assumes a shared storage root.
POIneer needs Azure practice with metadata storage and server integration while
large SQLite/PMTiles downloads can use Hetzner Object Storage. Actual cost savings
depend on storage volume, retained versions, base charges and download traffic;
the cost assessment remains in #200.

## Decision

- Configure metadata and artifact storage independently. Both may use the same
  destination for local development or alternative deployments.
- Azure Blob Storage holds `regions.json` and current region manifests in the
  planned deployment. Hetzner holds immutable SQLite/PMTiles artifacts.
- Resolve manifest paths and discovery from the metadata root; resolve every
  `objectKey` from the artifact root. No provider fields or credentials enter JSON.
- The server reads metadata from Azure and returns URLs or redirects for direct
  Hetzner downloads. Large artifact bytes do not pass through the Azure API.
- Verify all artifacts before atomically publishing the current manifest. Publish
  the complete region metadata snapshot before advertising a new region.

## Failure handling and migration

There is no cross-provider transaction. If artifact upload or verification fails,
the current manifest stays unchanged. If metadata publication fails, preserve the
previous release and retry with already verified immutable artifacts. Local cleanup
waits for successful manifest publication (#189). Retention reads the authoritative
metadata and must not delete potentially referenced files if that source is unavailable
(#190); cached releases and concurrent publication require protection as well.

Changing roots requires copying/verifying referenced content and coordinating
producer/consumer configuration. Preserve previous download targets for their
required grace period. A configuration edit alone is not a data migration.

## Consequences

Two providers require separate access configuration and failure handling. A shared
root is simpler operationally and remains supported. Existing Local/AzureBlob
artifact publishers are preserved; no deployed resources change with this decision.

This amends draft v1 semantics in place (#207), with unchanged JSON fields and
fixtures. Consumers of the earlier draft must update resolution. Once finalized,
incompatible field-meaning changes require a new contract version.

## Implementation and validation

#202 owns metadata publication; #203 owns remote metadata reads and download
resolution. Test distinct roots, a shared local root, wrong-root resolution and
metadata failure after verified artifact uploads. Existing fixture validation checks
document structure only. Server catalog #54 may continue using local fixtures.
Geographic bounds are a separate contract task.

See the [manifest contract](../../contracts/region-manifest/README.md) and
[tracking issue #207](https://github.com/christian-hofmeister/poineer-render/issues/207).
