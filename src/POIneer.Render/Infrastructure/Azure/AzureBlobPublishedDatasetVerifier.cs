using Microsoft.Extensions.Logging;
using POIneer.Render.Application.Contracts;
using POIneer.Render.Application.Ports;

namespace POIneer.Render.Infrastructure.Azure;

public sealed class AzureBlobPublishedDatasetVerifier : IPublishedDatasetVerifier
{
    private readonly IAzureBlobDatasetMetadataReader _metadataReader;
    private readonly ILogger<AzureBlobPublishedDatasetVerifier> _logger;

    public AzureBlobPublishedDatasetVerifier(
        IAzureBlobDatasetMetadataReader metadataReader,
        ILogger<AzureBlobPublishedDatasetVerifier> logger)
    {
        _metadataReader = metadataReader;
        _logger = logger;
    }

    public async Task<DatasetVerificationResult> VerifyAsync(
        DatasetArtifactMetadata expectedMetadata,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedMetadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var readResult = await _metadataReader.ReadAsync(destinationPath, cancellationToken);

        if (!readResult.BlobExists)
        {
            return Fail(
                expectedMetadata,
                destinationPath,
                $"Published Azure Blob dataset not found at '{destinationPath}'.");
        }

        if (readResult.Metadata is null)
        {
            return Fail(
                expectedMetadata,
                destinationPath,
                $"Published Azure Blob dataset at '{destinationPath}' does not contain comparable POIneer dataset metadata.");
        }

        var errors = GetMetadataMismatchErrors(expectedMetadata, readResult.Metadata, destinationPath);
        if (errors.Count > 0)
        {
            return Fail(expectedMetadata, destinationPath, errors.ToArray());
        }

        _logger.LogInformation(
            "Verified published Azure Blob dataset for region {RegionId} (version {Version}) at {DestinationPath}.",
            expectedMetadata.RegionId,
            expectedMetadata.Version,
            destinationPath);

        return new DatasetVerificationResult(IsVerified: true, Errors: []);
    }

    private DatasetVerificationResult Fail(
        DatasetArtifactMetadata expectedMetadata,
        string destinationPath,
        params string[] errors)
    {
        _logger.LogWarning(
            "Verification failed for published Azure Blob dataset region {RegionId} version {Version} at {DestinationPath}: {Errors}",
            expectedMetadata.RegionId,
            expectedMetadata.Version,
            destinationPath,
            string.Join("; ", errors));

        return new DatasetVerificationResult(IsVerified: false, Errors: errors);
    }

    private static List<string> GetMetadataMismatchErrors(
        DatasetArtifactMetadata expectedMetadata,
        AzureBlobDatasetMetadata actualMetadata,
        string destinationPath)
    {
        var errors = new List<string>();

        if (!string.Equals(actualMetadata.RegionId, expectedMetadata.RegionId, StringComparison.Ordinal))
        {
            errors.Add(
                $"Region id mismatch at '{destinationPath}': expected {expectedMetadata.RegionId}, found {actualMetadata.RegionId}.");
        }

        if (!string.Equals(actualMetadata.Version, expectedMetadata.Version, StringComparison.Ordinal))
        {
            errors.Add(
                $"Version mismatch at '{destinationPath}': expected {expectedMetadata.Version}, found {actualMetadata.Version}.");
        }

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

        return errors;
    }
}
