# Region manifest contract

Status: Draft (issue #204).

POIneer.Render produces manifests describing completed regional releases.
POIneer.Server consumes them to build its catalog and resolve download URLs.
The contract is independent of storage providers and public domains.

## Layout

- `v1/schema.json`: machine-readable JSON Schema (Draft 2020-12).
- `v1/examples/berlin.json`: illustrative SQLite-only manifest with synthetic
  version, size and checksum values; it does not reference a real published file.
- `v1/examples/berlin-with-pmtiles.json`: valid two-artifact release with an independently versioned PMTiles file.
- `v1/invalid/`: standalone negative fixtures for schema and filename contract validation.
- `v1/fixtures.json`: shared fixture paths, expected outcomes and reasons.
- `Test-RegionManifest.ps1`: executable fixture checks, requiring PowerShell 7.4 or later.
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
No separate release-availability index or historical release-manifest lookup is required.
Catalog display metadata comes from the separate published regions.json snapshot below.
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

### Complete verified release guarantee

A published manifest advertises only a complete, verified release, never an
in-progress render, pending upload or partially available artifact collection.
Every listed artifact must exist at its advertised key and have verified byte
size and SHA-256 matching the manifest before the release becomes current.
This includes reused artifacts and PMTiles whenever it is part of the release.
SQLite-only releases are complete when tiles are disabled; a failed PMTiles step
in an intended SQLite-plus-PMTiles release must not silently produce a partial
SQLite-only release.

If an upload, verification or manifest publication fails, the previously advertised
release must remain usable; an incomplete release must not become current.
Schema validation establishes document validity, not that these storage and
integrity guarantees have been fulfilled.

Issue #204 defines this consumer-visible guarantee. Manifest generation and the
publication mechanics are implemented separately in
[#202: Publish region manifests after verified artifact uploads](https://github.com/christian-hofmeister/poineer-render/issues/202).
That work owns upload/verification sequencing, atomic current-reference updates,
concurrency protection, idempotent retries, failure recovery and lifecycle/cleanup
coordination. This contract does not implement those mechanisms.

## Storage portability and configuration boundary

All published contract files, including region manifests and `regions.json`, must
remain independent of the deployment location and storage account. Manifest paths
and artifact object keys are relative to an application-configured storage root.
They must not contain absolute filesystem paths, drive letters, bucket/container
names added as account configuration, or provider-specific endpoints.

Provider selection, local root directories, account endpoints, bucket/container
names, root prefixes and authentication settings belong in the consuming or
publishing application's configuration, not in the published contract files.
Never embed credentials, connection strings, access keys, bearer tokens or
expiring signed URLs (including SAS and presigned URLs) in these files. The server
may generate an authorized download URL at request time from a validated object
key and its own configuration; that URL is not persisted in the contract.

`PbfUrl` is a source-data reference, not a storage-account setting or artifact
reference. Public, stable source URLs such as the existing Geofabrik HTTPS URLs
are allowed. They must not contain embedded credentials or expiring access tokens.
If an input source needs authentication, configure it separately in the renderer.

The same artifact reference works without modifying the manifest in either case:

```text
objectKey: geofabrik/europe/germany/berlin/berlin.4-d790344f01234567.sqlite

Local configuration:
  root directory: C:/datasets
  resolved file:  C:/datasets/geofabrik/europe/germany/berlin/berlin.4-d790344f01234567.sqlite

Object-storage configuration:
  container: datasets
  root prefix: published/
  resolved key: published/geofabrik/europe/germany/berlin/berlin.4-d790344f01234567.sqlite
```

These configuration values are illustrative and are not manifest fields. Storage
adapters resolve validated relative keys beneath the configured root and apply
platform-specific path handling internally. The same rule applies to `regions.json`
and `<regionId>/manifest.json`. Publication must enforce this boundary for all
exported files; JSON Schema alone does not detect every possible credential or
access token embedded in otherwise permitted string values.

## Published region metadata source

Catalog display metadata is provided separately from release manifests through
`regions.json` at the configured storage root. POIneer.Render publishes this
shared JSON snapshot; POIneer.Server reads it from storage and joins entries to
manifests by exact, case-sensitive equality of `Id` and `regionId`.
Azure Blob Storage can host the snapshot alongside manifests and artifacts;
the contract does not require a particular provider, a shared VPS/filesystem,
a direct service connection or access to the renderer's database.

The published format preserves the existing region configuration JSON array and
PascalCase field names. The current configuration files are
`src/POIneer.Render/Cli/config/regions.production.json` and
`src/POIneer.Render/Cli/config/regions.local.json`. An example published entry is:

```json
[
  {
    "Id": "geofabrik/europe/germany/berlin",
    "Name": "Berlin",
    "Country": "Germany",
    "Category": "City",
    "PbfUrl": "https://download.geofabrik.de/europe/germany/berlin-latest.osm.pbf"
  }
]
```

- `Id` is required, unique within the snapshot and follows the manifest's
  `regionId` syntax. It is the stable join key, not a display name.
- `Name` is a required nonempty display name.
- `Country` and `Category` are optional display/filter strings and may be absent
  or null, consistent with the existing region model. They do not define a parent
  relationship; category values such as `City` and `District` are display metadata.
- `PbfUrl` retains the renderer's source URL in the existing format. It is not an
  artifact download URL and is not used to determine release availability.

Bounds and explicit parent relationships are not provided in the MVP. Consumers
must not invent bounds or infer a parent record from path segments. Catalog
metadata is not duplicated in release manifests. This snapshot has no manifest
`schemaVersion` field; its array format is a separate published contract. A future
change to that format requires coordinated producer/consumer compatibility work.

The renderer's internal source is an implementation detail: it can read today's
JSON configuration or later query SQL and export the same published JSON shape.
The server continues to consume the storage snapshot in either case. A change
of internal source therefore requires no manifest or catalog exchange redesign.

Publish a complete snapshot for the configured catalog, not just the regions in
a single render job. Validate it before atomically replacing `regions.json`;
failed publication must leave the previous snapshot intact. Publish metadata for
a new region before its first current manifest, and preserve entries for regions
with current manifests. Metadata-only changes do not require new artifact or
release versions. The server requires read access; the publisher requires write
access to the snapshot.

Manifest discovery still determines which releases are available; `regions.json`
only supplies their catalog metadata. A metadata entry without a valid current
manifest does not make a region downloadable. If a manifest has no matching entry,
report a catalog metadata error and omit that region from the catalog. Duplicate
IDs or a malformed snapshot must be reported and not accepted as a new metadata
snapshot; an unavailable source must not be treated as an empty catalog. Optional
missing Country/Category values do not prevent a matched region from being shown.
These publication and join rules are runtime requirements, not validation performed
by the region-manifest schema.

## Shared fixtures and validation

Renderer and server can consume the same standalone JSON fixtures without .NET
DTOs, storage access or rendered datasets. Both Berlin examples contain synthetic
versions, sizes and checksums; they are contract fixtures, not downloadable data.
Use `v1/fixtures.json` as the language-independent test case inventory:

- `schemaValid` is the expected result of Draft 2020-12 validation with `date-time`
  format checking enabled.
- `contractValid` additionally checks that the filename matches the region name,
  artifact version and type. It does not assert remote existence, actual file
  size/checksum, publication history or catalog metadata availability.
- `reason` explains the case. All paths are relative to `v1/`.

The negative fixtures cover required artifact versions, version syntax, unknown
versions and fields, missing/duplicate/unknown artifact types, empty releases,
unsafe or uppercase paths, timestamp offsets, invalid calendar dates, checksum
encoding and positive byte sizes. The three `mismatched-*` cases are deliberately
schema-valid and fail only the documented cross-field filename rules.

Run from the repository root:

```powershell
pwsh -NoProfile -File contracts/region-manifest/Test-RegionManifest.ps1
```

The script resolves inputs relative to its own location, checks every registered
fixture, rejects unregistered fixture files and fails with a nonzero exit code on
unexpected results. It uses `Test-Json` for schema validation and supplements its
missing date-time format enforcement with .NET `XmlConvert` calendar/time checking
after the schema's UTC pattern check. Consumers using another validator must enable
its date-time format checking explicitly. Filename checks are separate from schema
validation. No new package dependencies or storage credentials are required.

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
