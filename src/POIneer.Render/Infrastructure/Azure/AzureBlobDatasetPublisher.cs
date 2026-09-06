using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using POIneer.Render.Application.Contracts;
using POIneer.Render.Application.Options;
using POIneer.Render.Application.Ports;

namespace POIneer.Render.Infrastructure.Azure;

public sealed class AzureBlobDatasetPublisher : IDatasetPublisher
{
    private readonly IAzureBlobDatasetPublishPlanner _publishPlanner;
    private readonly IAzureBlobDatasetUploader _uploader;
    private readonly IDatasetArtifactMetadataFactory _metadataFactory;
    private readonly ILogger<AzureBlobDatasetPublisher> _logger;
    private readonly PublisherOptions _publisherOptions;
    private readonly AzureBlobPublisherOptions _azureOptions;
    private int _uploadsThisRun;
    private long _uploadedBytesThisRun;

    public AzureBlobDatasetPublisher(
        IAzureBlobDatasetPublishPlanner publishPlanner,
        IAzureBlobDatasetUploader uploader,
        IDatasetArtifactMetadataFactory metadataFactory,
        ILogger<AzureBlobDatasetPublisher> logger,
        IOptions<PublisherOptions> publisherOptions,
        IOptions<AzureBlobPublisherOptions> azureOptions)
    {
        _publishPlanner = publishPlanner;
        _uploader = uploader;
        _metadataFactory = metadataFactory;
        _logger = logger;
        _publisherOptions = publisherOptions.Value;
        _azureOptions = azureOptions.Value;
    }

    public async Task<DatasetPublishResult> PublishAsync(
        DatasetPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RegionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Version);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);

        if (!File.Exists(request.SourcePath))
            throw new FileNotFoundException($"Dataset artifact not found: {request.SourcePath}", request.SourcePath);

        var decision = await _publishPlanner.PlanAsync(request, cancellationToken);

        if (!decision.ShouldUpload)
        {
            _logger.LogInformation(
                "Azure Blob dataset publish skipped for {BlobName}: {Reason}",
                decision.BlobName,
                decision.Reason);

            return new DatasetPublishResult(decision.BlobName, WasSkipped: true);
        }

        if (decision.DestinationExists)
        {
            switch (_publisherOptions.OverwritePolicy)
            {
                case DatasetPublishOverwritePolicy.Skip:
                    _logger.LogInformation(
                        "Azure Blob publish target already exists at {BlobName}, skipping (overwrite policy: Skip).",
                        decision.BlobName);
                    return new DatasetPublishResult(decision.BlobName, WasSkipped: true);

                case DatasetPublishOverwritePolicy.Fail:
                    throw new InvalidOperationException(
                        $"Azure Blob publish target already exists at {decision.BlobName} and the overwrite policy is Fail.");

                case DatasetPublishOverwritePolicy.SkipIfIdentical:
                    throw new InvalidOperationException(
                        $"Azure Blob publish target already exists at {decision.BlobName}, but does not match the source artifact metadata. Use overwrite policy Overwrite to replace it.");

                case DatasetPublishOverwritePolicy.Overwrite:
                    break;

                default:
                    throw new InvalidOperationException($"Unrecognized overwrite policy: {_publisherOptions.OverwritePolicy}");
            }
        }

        var artifactMetadata = await _metadataFactory.CreateAsync(
            request.RegionId,
            request.Version,
            request.SourcePath,
            cancellationToken);

        ReserveUploadCapacity(artifactMetadata.FileSizeBytes);

        try
        {
            await _uploader.UploadAsync(
                decision.BlobName,
                request.SourcePath,
                ToBlobMetadata(artifactMetadata),
                overwriteExisting: decision.DestinationExists
                                   && _publisherOptions.OverwritePolicy == DatasetPublishOverwritePolicy.Overwrite,
                cancellationToken);
        }
        catch
        {
            // Release the reserved upload capacity if the upload fails.
            ReleaseUploadCapacity(artifactMetadata.FileSizeBytes);
            throw;
        }

        _logger.LogInformation(
            "Published dataset for region {RegionId} (version {Version}) to Azure Blob {BlobName}.",
            request.RegionId,
            request.Version,
            decision.BlobName);

        return new DatasetPublishResult(decision.BlobName, WasSkipped: false);
    }

    private void ReserveUploadCapacity(long uploadBytes)
    {
        if (_uploadsThisRun >= _azureOptions.MaxUploadsPerRun)
        {
            throw new InvalidOperationException(
                $"Azure Blob publisher safety limit exceeded: MaxUploadsPerRun is {_azureOptions.MaxUploadsPerRun}.");
        }

        if (_uploadedBytesThisRun + uploadBytes > _azureOptions.MaxUploadBytesPerRun)
        {
            throw new InvalidOperationException(
                $"Azure Blob publisher safety limit exceeded: MaxUploadBytesPerRun is {_azureOptions.MaxUploadBytesPerRun} bytes.");
        }

        _uploadsThisRun++;
        _uploadedBytesThisRun += uploadBytes;
    }

    private void ReleaseUploadCapacity(long uploadBytes)
    {
        _uploadsThisRun--;
        _uploadedBytesThisRun -= uploadBytes;
    }

    private static Dictionary<string, string> ToBlobMetadata(DatasetArtifactMetadata artifactMetadata)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            [AzureBlobDatasetMetadataKeys.RegionId] = artifactMetadata.RegionId,
            [AzureBlobDatasetMetadataKeys.Version] = artifactMetadata.Version,
            [AzureBlobDatasetMetadataKeys.FileName] = artifactMetadata.FileName,
            [AzureBlobDatasetMetadataKeys.FileSizeBytes] = artifactMetadata.FileSizeBytes.ToString(CultureInfo.InvariantCulture),
            [AzureBlobDatasetMetadataKeys.CreatedUtc] = artifactMetadata.CreatedUtc.ToString("O"),
            [AzureBlobDatasetMetadataKeys.Sha256Checksum] = artifactMetadata.Sha256Checksum
        };
}
