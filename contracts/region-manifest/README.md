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

## Version identity and mutable current-release reference

| Field | Identifies | Change rule |
| --- | --- | --- |
| `schemaVersion` | The manifest contract format. | Changes when the finalized contract changes incompatibly; publishing a dataset does not change it. |
| `releaseVersion` | A specific collection of artifacts for one region. | A changed collection or changed artifact metadata requires a new release version. |
| `artifactVersion` | A specific artifact file within its region and type. | Changed file bytes require a new artifact version and object key; an unchanged file can be reused across releases. |

The pair `(regionId, releaseVersion)` must always identify the same artifact
collection, including each artifact's type, version, object key, size and checksum.
A producer must not reuse that release identity for a different collection.
Similarly, an artifact version within the same region and type, and its published
object key, must not be reassigned to different file bytes. These immutability
rules are producer/runtime obligations; a schema cannot compare publications.

For example, a release can replace its SQLite artifact while reusing its PMTiles
artifact. The release receives a new `releaseVersion`; the unchanged PMTiles file
retains its `artifactVersion` and `objectKey`, and `schemaVersion` stays the same.
Versions identify content or contract revisions; consumers must not assume all
three values are equal or use them to infer which release is current.

`<regionId>/manifest.json` is the mutable current-release reference: its contents
are replaced when another release becomes current. It is not an immutable URL
for the release it happens to describe. Versioned artifact keys identify fixed
file bytes, while the release identity identifies a fixed artifact collection.

The MVP does not provide a permanently retrievable manifest path for each release
version. Once the current manifest is replaced, the previous release manifest
need not remain retrievable. Immutable release-manifest references and historical
manifest storage are deferred beyond the MVP; immutable identity does not imply
indefinite storage retention.

## Manifest paths and current-release discovery

Each region has one current manifest at `<regionId>/manifest.json`, relative to
the configured storage root or bucket. For Berlin, the manifest key is:

```text
geofabrik/europe/germany/berlin/manifest.json
```

This fixed path identifies the current release. Consumers read `releaseVersion`
and `artifacts` from this manifest; they must not infer the current release from
artifact filenames, version sorting or storage modification timestamps. The
manifest's `regionId` must equal the path preceding `/manifest.json`; consumers
must reject a mismatch as a contract validation error.

For MVP discovery, POIneer.Server lists objects recursively under `geofabrik/`
relative to the configured storage root, following all listing pages when needed,
and selects keys ending exactly in `/manifest.json`. It reads and validates each
candidate before exposing that region and its current release in the catalog.
An artifact directory without a valid current manifest is not an available release.
No separate region index or historical release-manifest lookup is required.
The storage adapter must provide listing and read access; public HTTP directory
listing is not assumed.

## Artifact-reference resolution and publication

Each `artifacts[].objectKey` is the complete relative reference to an artifact
under the same configured storage root as the manifest. Resolve it from that root,
not from the manifest's directory, and do not prepend `regionId` again. For example:

```text
Storage root:  https://storage.example.com/datasets/
Manifest key:  geofabrik/europe/germany/berlin/manifest.json
Artifact key:  geofabrik/europe/germany/berlin/berlin.4-d790344f01234567.sqlite
Artifact URL:  https://storage.example.com/datasets/geofabrik/europe/germany/berlin/berlin.4-d790344f01234567.sqlite
```

The URL is illustrative. For filesystem storage, resolve the key beneath the
configured directory; for object storage, use the configured bucket/container
and root prefix plus the key. Private storage may require the server to generate
an authorized download URL. Credentials and signed URLs remain outside the manifest.

The producer must finish uploading and verifying all referenced artifacts before
publishing the current manifest. Reused artifacts must already be complete and
available at their existing keys. Replace `<regionId>/manifest.json` atomically
so readers see a complete old or new manifest, never a partially written document.
If artifact publication fails, leave the current manifest unchanged. Published
artifact keys must continue to identify the same file bytes; changed artifacts
receive new versioned keys.

These discovery, path-resolution and publication rules are runtime contract
requirements; JSON Schema validates the manifest document, not storage operations.
Historical manifests, retention periods and automatic storage cleanup are deferred
beyond the MVP and are not defined by this contract.

## Remaining contract work

Issue #204 will complete the geographic metadata source
and additional valid/invalid fixtures. This example establishes the agreed
structure; it is not yet the complete contract specification.

## Timestamp and checksum encoding

`publishedAt` is a valid UTC date-time using uppercase `T` and a trailing uppercase
`Z`, for example `2026-09-06T15:30:00Z`. Fractional seconds are optional, for example
`2026-09-06T15:30:00.123Z`. Numeric offsets (including `+00:00`), local timestamps
and date-only values are rejected. Validators must enable `date-time` format
checking in addition to the pattern so invalid calendar dates are rejected.

`sha256` encodes the full SHA-256 digest of the artifact's file bytes as exactly
64 lowercase hexadecimal characters (`0-9`, `a-f`). Uppercase, prefixes such as
`0x` or `sha256:`, separators and Base64 are not allowed. This is the full checksum,
not the shortened hash component in `artifactVersion`.

## Supported versions and compatibility

Currently only manifest `schemaVersion: 1` is supported. This version selects the
manifest contract; it is independent of `releaseVersion`, `artifactVersion` and
the artifact's own database schema version. Consumers must select the validator
by `schemaVersion` and validate the entire manifest before accepting the release.

Unknown fields are forbidden at both the manifest and artifact object levels by
`additionalProperties: false`. Unknown artifact types, missing or unsupported
schema versions, and any other validation failure must reject the entire manifest.
Consumers must report a clear validation error identifying the unsupported version
or invalid field; they must not silently discard fields or artifacts, partially
accept the release, or interpret an unsupported version as v1. These rejection
and error-reporting requirements are runtime consumer behavior.

While v1 remains a draft, its contract may be completed in place. Once finalized,
the v1 contract is stable: new fields (including optional fields), new artifact
types, or changes to existing validation rules or field meanings require a new
manifest schema version and version directory. Editorial clarifications that do
not change accepted manifests or their meaning do not require a version increment.
An optional field is not compatible with an older strict consumer when present,
because that consumer rejects it as unknown.

Future consumers adding support for a newer manifest version must retain explicit
v1 support and validate v1 manifests against the v1 contract. Deploy consumer
support before producers start publishing the newer version. Older consumers
continue to accept v1 and reject unsupported newer versions; there is no automatic
forward compatibility or version fallback. Removing v1 support requires an explicit
breaking migration, not a silent change to the v1 contract.

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
