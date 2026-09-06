namespace POIneer.Render.Infrastructure.Azure;

public interface IAzureBlobDatasetUploader
{
    Task UploadAsync(
        string blobName,
        string sourcePath,
        IReadOnlyDictionary<string, string> metadata,
        bool overwriteExisting,
        CancellationToken cancellationToken = default);
}
