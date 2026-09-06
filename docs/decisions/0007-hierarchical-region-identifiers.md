# ADR 0007: Globally Unique, Hierarchical Region Identifiers

## Status

Accepted

## Context

Issue #175 asks for region identifiers that stay unique, stable, and safe to use in
filesystem paths and Azure Blob prefixes as POIneer grows beyond the current MVP.

Region identifiers are currently short names such as `berlin` or `mittelfranken`
(`RegionDto.Id`, configured in `Cli/config/regions.production.json` /
`regions.local.json`). That is sufficient today - Geofabrik is the only source and every
configured region happens to have a unique short name - but it does not stay unique once
POIneer supports more regions, countries, or providers. `berlin` could refer to Berlin,
Germany, a differently-sourced Berlin, or a region from a future non-Geofabrik provider.

`RegionId` is also used directly as:

- a directory name under `Renderer:WorkDir` and `Renderer:OutDir` (`RenderRegion`,
  `Runner`)
- the destination directory and file name `LocalDatasetPublisher` writes to
  (`{DestinationDir}/{RegionId}/{RegionId}.{Version}{extension}`, ADR 0002)
- the Azure Blob name prefix `AzureBlobDatasetPublishPlanner` uploads to
  (`{RegionId}/{RegionId}.{Version}{extension}`, `docs/workflows/azure-dataset-storage.md`)

so it must stay stable and safe to interpolate into a local filesystem path and an Azure
Blob name, on both Windows dev machines and the Linux VPS.

## Decision

1. **Region identifiers follow the source/provider hierarchy:
   `{provider}/{continent}/{country}/{region}`.** Provider stays inside the id (rather
   than becoming separate metadata) so the id alone stays globally unique even if the
   same geographic region later becomes available from more than one provider - the
   scenario the current flat short names cannot represent at all. Example:

   ```text
   geofabrik/europe/germany/berlin
   geofabrik/europe/germany/bayern/mittelfranken
   ```

   The hierarchy depth is not fixed at 4: `mittelfranken` above needs an extra `bayern`
   segment to stay unique/meaningful under `geofabrik/europe/germany`, matching
   Geofabrik's own download hierarchy at https://download.geofabrik.de.

2. **A region id is a `/`-separated sequence of segments**, each restricted to ASCII
   letters, digits, `.`, `-`, and `_` (the same allow-list `LocalDatasetPublisher` and
   `AzureBlobDatasetPublishPlanner` already used for the flat id), with `.` and `..`
   rejected as whole segments. This keeps every id safe to use as-is:
   - as a local filesystem path, one nested directory per segment;
   - as an Azure Blob name prefix (Azure Blob Storage has no real directories, but `/`
     in a blob name is treated as a virtual folder separator by tooling and the portal).

3. **`Id` (technical) and `Name` (display) stay separate**, as they already were.
   `RegionDto` gains optional `Country`/`Category` display/filter metadata - already
   present in both region config JSON files but silently dropped by
   `GeofabrikRegionSource`'s deserialization - so the full model matches what the config
   files already describe:

   ```csharp
   public sealed record RegionDto(
       string Id,        // stable technical identifier, e.g. "geofabrik/europe/germany/berlin"
       string Name,       // human-readable display name, e.g. "Berlin"
       string PbfUrl,
       string? Poly,
       string? Country = null,  // optional display/filter metadata, e.g. "Germany"
       string? Category = null); // optional display/filter metadata, e.g. "City"
   ```

4. **Published artifact file names use only the id's leaf (last) segment**, not the full
   hierarchical id, while the directory/blob-name prefix uses the full id. Repeating the
   whole hierarchy inside the file name as well would make an already-long id longer for
   no benefit, since the surrounding directory/prefix already encodes it in full:

   ```text
   geofabrik/europe/germany/berlin/berlin.2-d790344ff0a3cbfe.sqlite
   ```

5. **A shared `RegionIdentifier` helper (`POIneer.Render.Domain.Models`) owns validation
   and leaf-segment derivation**, used by `LocalDatasetPublisher`,
   `AzureBlobDatasetPublishPlanner`, `RenderRegion`, and `Runner` instead of each call
   site re-implementing (and risking drift in) its own allow-list:
   - `ValidateHierarchicalId(value, parameterName)` - splits on `/`, validates every
     segment, rejects empty segments (leading/trailing/doubled `/`) and `.`/`..`
     segments, returns the segments.
   - `ValidateSingleSegment(value, parameterName)` - same character allow-list, but
     rejects `/` outright; used for `Version`, which is not hierarchical.
   - `GetLeafSegment(regionId)` - the last segment, for artifact file names.
   - `CombinePath(baseDir, regionId)` - `baseDir` plus one nested directory per segment,
     built with `Path.Combine` so the result uses OS-native separators instead of the
     raw `/` from the id.

   This design is fully backward compatible with a flat, non-hierarchical id: for a
   single-segment id such as `berlin`, `CombinePath` and the leaf-based file/blob name
   produce byte-identical output to the previous flat-id code path, so no existing
   published artifact path or blob name changes for a region whose id is not changed.

6. **Current configured regions adopt the new convention now**, since acceptance
   criteria for #175 require it and there is no production traffic depending on the old
   ids yet:

   | Old id          | New id                                          |
   | --------------- | ------------------------------------------------|
   | `berlin`         | `geofabrik/europe/germany/berlin`               |
   | `mittelfranken`  | `geofabrik/europe/germany/bayern/mittelfranken` |

   `Renderer:OnlyRegionId` in `appsettings.json`/`appsettings.Development.json` is
   updated to the new Berlin id to match.

## Consequences

- Region ids stay globally unique and stable as POIneer adds regions, countries, and
  (later) providers, instead of relying on every short name happening not to collide.
- `RegionIdentifier` is the one place hierarchical-id validation and leaf-segment
  derivation live; `LocalDatasetPublisher.ValidatePathSegment` and
  `AzureBlobDatasetPublishPlanner.ValidateBlobNameSegment` (near-duplicate private
  methods) are removed in favor of it.
- Local filesystem layout, published file names, and Azure Blob names all change shape
  for regions whose id changed (see the migration table above) - e.g. locally,
  `data/dev/renderer-work-dir/berlin/` becomes
  `data/dev/renderer-work-dir/geofabrik/europe/germany/berlin/`. See "Migration /
  Cleanup" below for what this means for existing dev artifacts.
- A region id can now legitimately contain `/`; anything that still needs a single,
  non-hierarchical segment (currently only `Version`) must call
  `RegionIdentifier.ValidateSingleSegment`, not treat every string the same way.
- No change to `IDatasetPublisher`, `DatasetPublishRequest`, or the Azure Blob metadata
  schema (ADR 0003) - `RegionId` remains a single string end to end; only what a valid
  value looks like, and how it maps to a path/blob name, changes.

## Migration / Cleanup for Existing Dev Artifacts

This decision does not migrate already-published artifacts (out of scope for #175) - it
only changes what a valid id looks like and what path/blob name a given id produces
going forward. For local development:

- Anything previously rendered/published under the old flat ids
  (`data/dev/renderer-work-dir/berlin/`, `data/dev/renderer-out-dir/berlin/`,
  `data/dev/renderer-publish-dir/berlin/`, and the equivalent `mittelfranken/`
  directories) is orphaned by this change: the renderer will no longer look for or
  write to those paths. They are safe to delete locally, or can be left in place and
  ignored - they will not be read, updated, or cleaned up automatically.
- A fresh render with the updated region config re-downloads the PBF and re-renders into
  the new hierarchical path (`data/dev/renderer-work-dir/geofabrik/europe/germany/berlin/`
  etc.), since `RegionUpdateChecker`'s render-state file lives under the region's work
  directory and therefore does not carry over either.
- Any manually created Azure dev blobs under the old `berlin/...` /
  `mittelfranken/...` prefixes in the `regions` container are similarly orphaned and can
  be deleted; a new publish run creates blobs under the new
  `geofabrik/europe/germany/berlin/...` prefix instead.
- No automated migration or cleanup job is introduced for this, matching the issue's
  explicit "Out of Scope: Migrating already published production datasets
  automatically." There is no production traffic on the old ids yet.

## Out of Scope

- Changing Azure Blob publishing behavior beyond supporting hierarchical blob names
  derived from region identifiers.
- Changing dataset generation behavior.
- Supporting multiple OSM providers immediately (the hierarchy leaves room for it; only
  Geofabrik is configured today).
- Migrating already-published production datasets automatically.
- Admin UI or mobile app changes.

## Testing Guidance

- `RegionIdentifier` unit tests: valid multi-segment ids, leaf-segment extraction,
  rejection of empty segments (leading/trailing/doubled `/`), `.`/`..` segments,
  disallowed characters, and `Version`-style single-segment rejection of `/`.
- `LocalDatasetPublisher` integration tests: a hierarchical `RegionId` produces the
  expected nested destination directory and a leaf-named file; a flat `RegionId` keeps
  producing the previous non-nested path.
- `AzureBlobDatasetPublishPlanner` unit tests: a hierarchical `RegionId` produces the
  expected `{RegionId}/{leaf}.{Version}{extension}` blob name; a flat `RegionId` keeps
  producing the previous blob name.
- `RendererConfigurationFilesTests`: updated to assert the new
  `Renderer:OnlyRegionId` value.

## References

- #175 - globally unique, hierarchical region identifiers for rendered datasets
- ADR 0002 - Local Dataset Publisher
- ADR 0003 - Dataset Artifact Metadata
- `docs/workflows/azure-dataset-storage.md` - Azure Blob naming convention
- `docs/architecture/hybrid-dataset-architecture.md`
- [Geofabrik download index](https://download.geofabrik.de) - the provider/continent/country
  hierarchy the region id convention follows
