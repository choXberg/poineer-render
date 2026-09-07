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
- `artifacts`: files belonging to the release; currently known types are SQLite and PMTiles.
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
Version 1 accepts SQLite-only releases or SQLite-plus-PMTiles releases: exactly one
SQLite artifact is required, and at most one PMTiles artifact is optional.
The `type` enum rejects unknown types. Combined with `maxItems: 2` and exactly one
SQLite match (`minContains: 1`, `maxContains: 1`), the schema also rejects duplicate
types, even when their object keys or other metadata differ. `uniqueItems` alone
would only reject identical objects and is unnecessary here.
Supporting another type requires a deliberate contract extension, including review
of these cardinality rules; the generic object-key pattern remains independent of
the type list. Unknown fields are rejected. `artifactVersion` is always required,
even when it equals `releaseVersion`; there is no fallback to the release version.
The release version identifies the collection of artifacts, while each artifact
version identifies its file. An unchanged artifact can retain its version and
object key when reused in a new release.
Sizes are positive integer byte counts
and SHA-256 values contain exactly 64 lowercase hexadecimal characters.

Object keys use lowercase relative path segments containing only `a-z`, `0-9`,
`.`, `_`, `-` and `/`. Each segment consists of alphanumeric components separated
by a single dot, underscore or hyphen. Leading/trailing slashes, empty segments,
`.`/`..` traversal segments, URLs and uppercase characters are rejected.
Region identifiers use the same lowercase path syntax.
Both `artifactVersion` and `releaseVersion` match `^[1-9][0-9]*-[a-f0-9]{16}$`:
a positive schema version without leading zeros, a hyphen and exactly 16 lowercase
hexadecimal characters. `artifactVersion` remains explicitly required.

### Runtime contract validation

Producers and consumers must additionally enforce:

- The filename is `<region-name>.<artifactVersion>.<type>`, where `region-name`
  is the final segment of `regionId`, and the version is the artifact's own version.
- The object-key extension matches `type` exactly (for example, `.sqlite` or
  `.pmtiles`, with the same rule applying to future types).

Standard JSON Schema Draft 2020-12 cannot dynamically compare sibling field values.
Filename and extension matching are runtime contract rules, not guarantees provided
by schema validation alone.
For example: `geofabrik/europe/germany/berlin/berlin.4-d790344f01234567.sqlite`.

Use a Draft 2020-12 validator with `date-time` format checking enabled to validate
calendar dates as well as the required UTC timestamp shape. Schema validation
does not establish that an artifact exists, matches its checksum, or belongs to
the stated region/release; producer and consumer logic must check those semantics.
