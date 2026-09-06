# ADR 0002: Local Dataset Publisher

## Status

Accepted

## Context

Issue #132 asks for a local filesystem implementation of a dataset-publishing
abstraction, as the first step of a larger dataset-delivery epic (#132-#137):
a local publisher (#132), Azure Blob Storage provisioning (#133), an Azure
publisher (#134), published-dataset integrity verification (#135),
configurable render execution targets (#136), and documentation of the
resulting hybrid architecture (#137).
The public architecture summary for that separation lives in
[`docs/architecture/hybrid-dataset-architecture.md`](../architecture/hybrid-dataset-architecture.md).

POIneer.Render already produces validated, canonical `.sqlite` datasets per
region in `outDir` (see `RenderRegion` and ADR-less prior work). What is
still missing is a way to hand a validated dataset to a *destination* -
somewhere other than the renderer's own working directories - without
requiring Azure infrastructure. The existing VPS deployment (#110) provides
predictable fixed-cost compute and storage and must remain a valid,
Azure-independent deployment option. A local publisher is also the simplest
possible implementation for development and testing, and the natural first
step before adding an Azure-backed one (#134) behind the same abstraction.

## Decision

Introduce `IDatasetPublisher` (`Application/Ports`) as the publishing
abstraction, with `LocalDatasetPublisher` (`Infrastructure/FileSystem`) as
its first implementation:

- `IDatasetPublisher.PublishAsync(DatasetPublishRequest, CancellationToken)`
  takes a region id, a version identifier, and the path to an
  already-validated dataset artifact, and returns a `DatasetPublishResult`
  (destination path + whether the publish was skipped).
- `LocalDatasetPublisher` copies the artifact into a configurable
  `PublisherOptions.DestinationDir`, laid out as
  `{DestinationDir}/{RegionId}/{RegionId}.{Version}{extension}` so the
  region and dataset version stay identifiable from the file layout alone -
  useful when inspecting the destination directly on the VPS.
- The copy goes through a `.tmp` staging file that is renamed into place
  only once fully written, so an interrupted copy never leaves a partial
  file at the real destination path.
- What happens when a file for the same region/version already exists at
  the destination is governed by an explicit
  `PublisherOptions.OverwritePolicy` (`Skip` (default), `SkipIfIdentical`,
  `Overwrite`, or `Fail`), rather than an implicit, undocumented behavior.
  `Skip` preserves the existing file unconditionally. `SkipIfIdentical`
  preserves it only when it already matches the source bytes; otherwise it
  fails so the same-version mismatch can be investigated. `Overwrite` is the
  only policy that replaces an existing destination.
- `RenderRegion` calls `IDatasetPublisher.PublishAsync` right after
  promoting a validated dataset to its canonical `outDir` location. The
  version is computed by `IDatasetVersionCalculator`
  (`FileHashDatasetVersionCalculator`): a SHA-256 hash of the source PBF
  (the *cut* PBF, so a poly-file change is picked up too, not just a raw
  PBF re-download) combined with `PublisherOptions.SchemaVersion`. This was
  originally a UTC timestamp, but that made every render - including a
  forced re-render of byte-identical input while testing - publish a new
  file, even with nothing actually different about the data. A
  content-derived version fixes that: identical PBF content and an
  unchanged `SchemaVersion` always compute the identical version string,
  so republishing unchanged data lands on the same destination filename
  and can be skipped by `IDatasetPublisher`'s overwrite policy
  instead of accumulating a new file on every run. A new version - and a
  new published file - is only produced when the OSM PBF genuinely
  changed, or a deployment deliberately bumps `SchemaVersion` (e.g. a new
  POIneer.Render release changes the exported schema or POI mapping).
- `PublisherOptions.SchemaVersion` must be bumped whenever a change intentionally alters the
  generated SQLite dataset artifact while the source PBF can remain unchanged. This includes
  SQLite schema changes, POI tag mapping changes, export logic changes, and dataset semantics
  changes. Without the bump, the same `{SchemaVersion}-{PbfHash}` version can be reused for
  byte-different SQLite output. Dev and production configuration use `SkipIfIdentical` so this
  mismatch is surfaced instead of silently replacing an already published artifact. Operators can
  investigate the mismatch, bump `SchemaVersion` when the rendered dataset intentionally changed,
  or temporarily choose `Overwrite` when replacing the existing destination is deliberate.
- `PublisherOptions.DestinationDir` is required and validated on startup
  (`ValidateOnStart`) when `PublisherOptions.Target` is `Local`, matching
  `RendererOptions`. The value itself is never hardcoded to a specific
  machine: `appsettings.Development.json` points at a relative
  `data/dev/renderer-publish-dir`, `appsettings.Production.json` at an
  absolute VPS path (`/opt/poineer-render/data/prod/renderer-publish-dir`),
  the same pattern already used for `Renderer:WorkDir`/`OutDir`/
  `LockFilePath`.
- `PublisherOptions.Target` selects the configured publisher implementation.
  Existing development, CI, and VPS configurations explicitly use `Local`.
  `AzureBlob` selects the Azure Blob Storage publisher introduced by #134.

Naming note: `RenderRegion` already used the word "publish" informally for
promoting a validated staging file to its canonical `outDir` location (a
purely local, same-directory move). That step is now called "promoting" in
logs/comments to avoid confusion with the new, distinct `IDatasetPublisher`
concept introduced here.

Out of scope for this issue (tracked by later issues in the epic): Azure
Blob Storage (#133, #134), FTP/SFTP publishing, CDN integration, automatic
synchronization between VPS and Azure, and public download URLs.

## Consequences

- `IDatasetPublisher` is a clean seam for #134 to add an Azure Blob Storage
  implementation without changing `RenderRegion` or the publishing contract.
- Publishing is a required part of the render pipeline for every render
  region from now on: a publish failure fails that region's render (surfaced
  the same way a validation failure already is, via `RenderRegion` throwing
  and `Runner` recording the region as failed).
- `Publisher:Target` is now explicit in configuration and defaults to `Local`
  in code. `Publisher:DestinationDir` remains required for the local target.
  Existing `appsettings.json`/`appsettings.Development.json`/
  `appsettings.Production.json` already define both values, so no action is
  needed for the default dev/CI/VPS setups; a fully custom local deployment
  overriding every renderer setting via environment variables will also need
  to set `POINEER_RENDER__PUBLISHER__TARGET=Local` and
  `POINEER_RENDER__PUBLISHER__DESTINATIONDIR`.
- Testable: `LocalDatasetPublisher` is covered by integration tests against
  the real filesystem (copy, directory creation, all three overwrite
  policies, missing-source handling, staging-file cleanup);
  `FileHashDatasetVersionCalculator` is covered by integration tests
  asserting the stability property that matters (identical input -> identical
  version, across repeated calls too; different PBF content or a different
  `SchemaVersion` -> a different version); and `RenderRegion`'s calls into
  `IDatasetPublisher`/`IDatasetVersionCalculator` are covered by unit tests
  with mocked dependencies (correct request contents, version sourced from
  the cut PBF, no publish on validation failure, exception propagation on
  publish failure).
- A forced re-render (`Renderer:OverwriteDatabase`/`OverwritePbf`) of
  unchanged input still re-renders and re-validates the dataset - that
  behavior is unrelated to publishing and out of scope here - but no longer
  clutters the publish destination with a near-duplicate file, since the
  publish step is now idempotent on unchanged content.

## References

- #132 - Add local dataset publisher
- #133 - Provision Azure Blob Storage for datasets
- #134 - Publish datasets to Azure Blob Storage
- #135 - Verify published dataset integrity
- #136 - Prepare configurable render execution targets
- #137 - Document hybrid dataset architecture
- #110 - Renderer scheduled VPS config groundwork
- ADR 0001 - Prevent Overlapping Scheduled Renders
- Hybrid Dataset Architecture - `docs/architecture/hybrid-dataset-architecture.md`
