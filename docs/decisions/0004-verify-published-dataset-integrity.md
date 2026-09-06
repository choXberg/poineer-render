# ADR 0004: Verify Published Dataset Integrity

## Status

Accepted

## Context

Issue #135 asks for verification that a published dataset artifact was actually transferred
correctly to its publish destination. `IDatasetPublisher.PublishAsync` returning without
throwing only means the publisher's own copy/upload logic didn't raise an exception - it
does not, on its own, guarantee that what is now sitting at the destination is complete and
byte-identical to the source. The issue asks for this to be checked explicitly - existence,
file size, and a SHA-256 checksum comparison - "before a dataset is considered successfully
published," with verification failures preventing that and being logged clearly.

`DatasetArtifactMetadata` (issue #130, ADR 0003) already computes exactly the fields needed
to describe the source artifact - `FileSizeBytes` and `Sha256Checksum` - and was deliberately
designed to be reusable by this issue rather than duplicated: "#135 can call it a second time
against a publish destination and compare `Sha256Checksum` values without introducing its
own hashing logic."

## Decision

Introduce `IPublishedDatasetVerifier` (`Application/Ports`) as the verification abstraction,
with `FilePublishedDatasetVerifier` (`Infrastructure/FileSystem`) as its first implementation:

- `IPublishedDatasetVerifier.VerifyAsync(DatasetArtifactMetadata expectedMetadata, string
  destinationPath, CancellationToken)` returns a `DatasetVerificationResult` (`IsVerified` +
  `Errors`). It takes the already-computed source metadata rather than a source path, so an
  implementation only ever reads the *published* artifact, never the source again.
- `FilePublishedDatasetVerifier` re-uses `IDatasetArtifactMetadataFactory` (issue #130)
  against `destinationPath` to describe the artifact actually present at the publish
  destination, then compares `FileSizeBytes` and `Sha256Checksum` against `expectedMetadata`.
  This avoids a second, independent hashing implementation that could disagree with the
  source calculation about what "the checksum" of a dataset even means - both the source and
  destination checksums are always produced the same way.
- A missing destination file is reported as a verification failure (`IsVerified: false`,
  with a descriptive error), not an exception - unlike `FileDatasetArtifactMetadataFactory`,
  which throws `FileNotFoundException` for a missing *source* artifact (a programming error,
  since the source must already exist by the time metadata is generated). A missing
  *destination* artifact is an expected failure mode this feature exists to detect.
  `FilePublishedDatasetVerifier` still validates its own required arguments
  (`ArgumentNullException`/`ArgumentException`) before doing any I/O.
- The upfront `File.Exists` check can't fully rule out a missing/unreadable artifact: the
  published file can disappear, be replaced, or become unreadable between that check and the
  `IDatasetArtifactMetadataFactory.CreateAsync` call right after it (a concurrent publish,
  external cleanup, a permissions/disk issue). Found by Copilot's review on PR #144,
  `FilePublishedDatasetVerifier` now catches `FileNotFoundException`/`IOException`/
  `UnauthorizedAccessException` from that call and converts them into the same
  `DatasetVerificationResult` failure path, instead of letting them propagate as exceptions
  that would bypass `RenderRegion`'s verification-failure handling entirely. Any other
  exception (e.g. a genuine programming error) still propagates unchanged.
- Both a size mismatch and a checksum mismatch are collected into `Errors` in the same pass
  rather than stopping at the first one found, so an operator diagnosing a failed publish
  sees every problem at once.
- `RenderRegion` calls `IPublishedDatasetVerifier.VerifyAsync` immediately after
  `IDatasetPublisher.PublishAsync`, passing the same `artifactMetadata` already computed for
  the canonical source artifact and `publishResult.DestinationPath`. If verification fails,
  `RenderRegion` logs the errors and throws `InvalidOperationException` - the same pattern
  already used for a dataset validation failure - so `Runner` records the region as failed
  and the render is not silently treated as successfully published.
- Verification runs after *every* `PublishAsync` call, including when `publishResult
  .WasSkipped` is `true`. `WasSkipped` only means this run didn't need to copy new bytes
  because the configured overwrite policy left the existing destination file in place (see
  ADR 0002) - it says nothing about whether that existing file was ever verified,
  particularly on the first run after this feature ships, or if the destination was modified
  out of band between runs. Always verifying keeps "successfully published" meaning
  "confirmed correct right now," not "was correct as of some earlier, unverified run."

Out of scope for this issue (see issue #135 and the surrounding epic): re-validating POI
content post-upload, mobile download verification, automatic corruption repair, and
cross-region replication verification. Azure Blob Storage gets its own
`IPublishedDatasetVerifier` implementation in #134 behind the same abstraction.

## Consequences

- `IPublishedDatasetVerifier` is a clean seam for #134: an Azure-backed implementation can
  compare stored blob metadata with source artifact metadata without changing `RenderRegion`
  or the verification contract.
- Publishing now costs one additional full read-and-hash pass over the published artifact on
  every render run that reaches the publish step, on top of the hash already computed for the
  source artifact by `IDatasetArtifactMetadataFactory`. This is accepted as the price of
  verifying "what actually landed," including on skip - see the decision above. If this
  proves too costly for a large regional dataset published to a slower destination (relevant
  once #134 adds Azure), a cheaper skip-aware verification strategy can be revisited then.
- A previously-published, already-correct dataset can now cause a render run to fail if the
  file at the destination was corrupted or modified out of band since it was published -
  this is the intended behavior (issue #135's stated goal), not a regression.
- Testable: `FilePublishedDatasetVerifier` is covered by integration tests against the real
  filesystem and the real `FileDatasetArtifactMetadataFactory` (verified on an exact match,
  not verified on a missing destination/size mismatch/checksum mismatch, both mismatches
  reported together, invalid-argument handling), plus unit tests with a mocked
  `IDatasetArtifactMetadataFactory` for the race condition above (`FileNotFoundException`/
  `IOException`/`UnauthorizedAccessException` from the factory become a failed result rather
  than propagating; any other exception still propagates); `RenderRegion`'s call into
  `IPublishedDatasetVerifier` is covered by unit tests with a mocked dependency (correct
  metadata/destination path passed, verification runs even when the publish was skipped,
  a verification failure throws and fails the region without disturbing the canonical
  dataset, cancellation token forwarded).

## References

- #135 - Verify published dataset integrity
- #130 - Add dataset artifact metadata
- #132 - Add local dataset publisher
- #134 - Publish datasets to Azure Blob Storage
- ADR 0002 - Local Dataset Publisher
- ADR 0003 - Dataset Artifact Metadata
