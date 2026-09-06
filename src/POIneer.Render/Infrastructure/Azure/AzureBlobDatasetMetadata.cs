using POIneer.Render.Application.Contracts;

namespace POIneer.Render.Infrastructure.Azure;

public sealed record AzureBlobDatasetMetadata(
    string RegionId,
    string Version,
    long FileSizeBytes,
    string Sha256Checksum)
{
    public static AzureBlobDatasetMetadata FromArtifact(DatasetArtifactMetadata artifactMetadata)
    {
        ArgumentNullException.ThrowIfNull(artifactMetadata);

        return new AzureBlobDatasetMetadata(
            artifactMetadata.RegionId,
            artifactMetadata.Version,
            artifactMetadata.FileSizeBytes,
            artifactMetadata.Sha256Checksum);
    }

    public bool Matches(DatasetArtifactMetadata artifactMetadata)
        => Equals(FromArtifact(artifactMetadata));
}
