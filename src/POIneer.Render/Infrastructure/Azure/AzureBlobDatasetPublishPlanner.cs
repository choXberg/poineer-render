using POIneer.Render.Application.Contracts;
using POIneer.Render.Application.Ports;
using POIneer.Render.Domain.Models;

namespace POIneer.Render.Infrastructure.Azure;

public sealed class AzureBlobDatasetPublishPlanner : IAzureBlobDatasetPublishPlanner
{
    private readonly IDatasetArtifactMetadataFactory _metadataFactory;
    private readonly IAzureBlobDatasetMetadataReader _metadataReader;

    public AzureBlobDatasetPublishPlanner(
        IDatasetArtifactMetadataFactory metadataFactory,
        IAzureBlobDatasetMetadataReader metadataReader)
    {
        _metadataFactory = metadataFactory;
        _metadataReader = metadataReader;
    }

    public async Task<AzureBlobDatasetPublishDecision> PlanAsync(
        DatasetPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RegionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Version);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);

        // RegionId may be a globally unique, hierarchical identifier (ADR 0007), e.g.
        // "geofabrik/europe/germany/berlin" - each '/'-separated segment is validated
        // individually and the full id becomes the blob name's virtual-folder prefix.
        // Version is not hierarchical and may not contain the '/' segment separator.
        var regionIdSegments = RegionIdentifier.ValidateHierarchicalId(request.RegionId, nameof(request.RegionId));
        RegionIdentifier.ValidateSingleSegment(request.Version, nameof(request.Version));

        var blobName = BuildBlobName(request, regionIdSegments[^1]);
        var expectedMetadata = await _metadataFactory.CreateAsync(
            request.RegionId,
            request.Version,
            request.SourcePath,
            cancellationToken);

        var readResult = await _metadataReader.ReadAsync(blobName, cancellationToken);

        if (!readResult.BlobExists)
        {
            return new AzureBlobDatasetPublishDecision(
                blobName,
                DestinationExists: false,
                ShouldUpload: true,
                "Destination blob is missing.");
        }

        if (readResult.Metadata is null)
        {
            return new AzureBlobDatasetPublishDecision(
                blobName,
                DestinationExists: true,
                ShouldUpload: true,
                "Destination blob exists but does not contain comparable POIneer dataset metadata.");
        }

        if (readResult.Metadata.Matches(expectedMetadata))
        {
            return new AzureBlobDatasetPublishDecision(
                blobName,
                DestinationExists: true,
                ShouldUpload: false,
                "Destination blob already matches the source artifact metadata.");
        }

        return new AzureBlobDatasetPublishDecision(
            blobName,
            DestinationExists: true,
            ShouldUpload: true,
            "Destination blob metadata differs from the source artifact metadata.");
    }

    // The blob name's prefix is the full hierarchical RegionId (Azure Blob Storage has no
    // real directories, but '/' in a blob name is treated as a virtual folder separator
    // by tooling and the portal); the file part is named after only the leaf segment
    // (e.g. "berlin", not "geofabrik/europe/germany/berlin") so it stays short instead of
    // repeating the prefix the blob name already carries.
    private static string BuildBlobName(DatasetPublishRequest request, string leafRegionId)
    {
        var extension = Path.GetExtension(request.SourcePath);
        return $"{request.RegionId}/{leafRegionId}.{request.Version}{extension}";
    }
}
