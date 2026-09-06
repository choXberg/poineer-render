namespace POIneer.Render.Infrastructure.Azure;

public sealed record AzureBlobDatasetPublishDecision(
    string BlobName,
    bool DestinationExists,
    bool ShouldUpload,
    string Reason);
