# Region manifest contract

Status: Draft (issue #204).

POIneer.Render produces manifests describing completed regional releases.
POIneer.Server consumes them to build its catalog and resolve download URLs.
The contract is independent of storage providers and public domains.

## Layout

- `v1/schema.json`: machine-readable JSON Schema (Draft 2020-12).
- `v1/examples/berlin.json`: illustrative SQLite-only manifest with synthetic
  version, size and checksum values; it does not reference a real published file.
- The version directory tracks the manifest schema, not dataset releases.

## Agreed structure

- `schemaVersion`: version of the manifest format.
- `regionId`: stable hierarchical region identifier.
- `releaseVersion`: string identifying the published release.
- `publishedAt`: UTC timestamp describing publication of the release.
- `artifacts`: files belonging to the release, allowing SQLite and PMTiles.
- Each artifact carries its `type`, `artifactVersion`, `objectKey`, `sizeBytes` and full hexadecimal
  SHA-256 checksum (`sha256`).

Object keys are relative to the configured storage root or bucket. The server
resolves these references into download URLs. Credentials, domains and expiring
signed URLs do not belong in this manifest. A separate file name is unnecessary
because the object key already contains it.

## Remaining contract work

Issue #204 will complete the compatibility rules,
manifest discovery and current-release references, geographic metadata source,
and additional valid/invalid fixtures. This example establishes the agreed
structure; it is not yet the complete contract specification.

## Schema validation

All documented top-level fields and the five core artifact fields are required.
Version 1 accepts exactly one SQLite artifact and optionally one PMTiles artifact.
Unknown fields and artifact types are rejected. `artifactVersion` is always required,
even when it equals `releaseVersion`; there is no fallback to the release version.
The release version identifies the collection of artifacts, while each artifact
version identifies its file. An unchanged artifact can retain its version and
object key when reused in a new release.
Sizes are positive integer byte counts
and SHA-256 values contain exactly 64 lowercase hexadecimal characters.

Object keys must be relative paths without traversal segments or URLs. Region
identifiers use lowercase path segments. Versions are nonempty strings of
alphanumeric components separated by dots, underscores or hyphens.

Use a Draft 2020-12 validator with `date-time` format checking enabled to validate
calendar dates as well as the required UTC timestamp shape. Schema validation
does not establish that an artifact exists, matches its checksum, or belongs to
the stated region/release; producer and consumer logic must check those semantics.
