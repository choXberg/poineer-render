namespace POIneer.Render.Infrastructure.Azure;

public interface IAzureBlobDatasetMetadataReader
{
    Task<AzureBlobDatasetMetadataReadResult> ReadAsync(
        string blobName,
        CancellationToken cancellationToken = default);
}
