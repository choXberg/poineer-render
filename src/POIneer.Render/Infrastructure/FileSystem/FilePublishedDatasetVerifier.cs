using Microsoft.Extensions.Logging;
using POIneer.Render.Application.Contracts;
using POIneer.Render.Application.Ports;

namespace POIneer.Render.Infrastructure.FileSystem;

// Local filesystem implementation of IPublishedDatasetVerifier (issue #135), for publish
// destinations reachable as ordinary files - currently LocalDatasetPublisher's destination
// directory. Verification needs no filesystem-specific logic of its own: it reuses
// IDatasetArtifactMetadataFactory (issue #130) to describe the artifact actually present at
// destinationPath and compares that against the metadata already computed for the source
// artifact before publishing, rather than duplicating the existence/size/checksum logic
// FileDatasetArtifactMetadataFactory already implements.
//
// Out of scope (see issue #135): re-validating POI content post-upload, mobile download
// verification, automatic corruption repair, and cross-region replication verification.
// Non-filesystem publish targets, such as Azure Blob Storage, need their own
// IPublishedDatasetVerifier implementation behind the same abstraction.
public sealed class FilePublishedDatasetVerifier : IPublishedDatasetVerifier
{
    private readonly IDatasetArtifactMetadataFactory _metadataFactory;
    private readonly ILogger<FilePublishedDatasetVerifier> _logger;

    public FilePublishedDatasetVerifier(
        IDatasetArtifactMetadataFactory metadataFactory,
        ILogger<FilePublishedDatasetVerifier> logger)
    {
        _metadataFactory = metadataFactory;
        _logger = logger;
    }

    public async Task<DatasetVerificationResult> VerifyAsync(
        DatasetArtifactMetadata expectedMetadata,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedMetadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        if (!File.Exists(destinationPath))
        {
            return Fail(expectedMetadata, destinationPath,
                $"Published artifact not found at '{destinationPath}'.");
        }

        // Reuses the same factory (and therefore the same streaming SHA-256 computation)
        // that produced expectedMetadata for the source artifact, rather than a second,
        // independent hashing implementation that could disagree with the source
        // calculation about what "the checksum" of a dataset even means.
        DatasetArtifactMetadata actualMetadata;

        try
        {
            actualMetadata = await _metadataFactory.CreateAsync(
                expectedMetadata.RegionId,
                expectedMetadata.Version,
                destinationPath,
                cancellationToken);
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            // The File.Exists check above can't fully rule this out: the published artifact
            // may disappear, be replaced, or become unreadable between that check and this
            // call (a concurrent publish, external cleanup, a permissions/disk issue) -
            // reported via the Copilot review on PR #144. These are expected operational
            // failure modes at the publish destination, not programming errors, so they must
            // become a DatasetVerificationResult failure (logged, caller decides what to do)
            // instead of an exception bypassing that reporting path.
            return Fail(expectedMetadata, destinationPath,
                $"Published artifact at '{destinationPath}' could not be read for verification: {ex.Message}");
        }

        var errors = new List<string>();

        if (actualMetadata.FileSizeBytes != expectedMetadata.FileSizeBytes)
        {
            errors.Add(
                $"File size mismatch at '{destinationPath}': expected {expectedMetadata.FileSizeBytes} bytes, found {actualMetadata.FileSizeBytes} bytes.");
        }

        if (!string.Equals(actualMetadata.Sha256Checksum, expectedMetadata.Sha256Checksum, StringComparison.Ordinal))
        {
            errors.Add(
                $"Checksum mismatch at '{destinationPath}': expected {expectedMetadata.Sha256Checksum}, found {actualMetadata.Sha256Checksum}.");
        }

        if (errors.Count > 0)
        {
            return Fail(expectedMetadata, destinationPath, errors.ToArray());
        }

        _logger.LogInformation(
            "({RegionId}) Verified published dataset version {Version} at {DestinationPath}: size and checksum match the source artifact.",
            expectedMetadata.RegionId,
            expectedMetadata.Version,
            destinationPath);

        return new DatasetVerificationResult(IsVerified: true, Errors: []);
    }

    private DatasetVerificationResult Fail(
        DatasetArtifactMetadata expectedMetadata, string destinationPath, params string[] errors)
    {
        _logger.LogError(
            "({RegionId}) Verification failed for published dataset version {Version} at {DestinationPath}: {Errors}",
            expectedMetadata.RegionId,
            expectedMetadata.Version,
            destinationPath,
            string.Join("; ", errors));

        return new DatasetVerificationResult(IsVerified: false, Errors: errors);
    }
}
