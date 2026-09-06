namespace POIneer.Render.Infrastructure.Azure;

public sealed record AzureBlobDatasetMetadataReadResult(
    bool BlobExists,
    AzureBlobDatasetMetadata? Metadata);
