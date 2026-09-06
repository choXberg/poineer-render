using Azure;
using Azure.Storage.Blobs;

namespace POIneer.Render.Infrastructure.Azure;

public sealed class AzureBlobDatasetMetadataReader : IAzureBlobDatasetMetadataReader
{
    private readonly BlobContainerClient _containerClient;

    public AzureBlobDatasetMetadataReader(BlobContainerClient containerClient)
    {
        _containerClient = containerClient;
    }

    public async Task<AzureBlobDatasetMetadataReadResult> ReadAsync(
        string blobName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        var blobClient = _containerClient.GetBlobClient(blobName);

        try
        {
            var properties = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken);
            return new AzureBlobDatasetMetadataReadResult(
                BlobExists: true,
                TryReadMetadata(properties.Value.Metadata));
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return new AzureBlobDatasetMetadataReadResult(
                BlobExists: false,
                Metadata: null);
        }
    }

    private static AzureBlobDatasetMetadata? TryReadMetadata(IDictionary<string, string> metadata)
    {
        if (!TryGet(metadata, AzureBlobDatasetMetadataKeys.RegionId, out var regionId)
            || !TryGet(metadata, AzureBlobDatasetMetadataKeys.Version, out var version)
            || !TryGet(metadata, AzureBlobDatasetMetadataKeys.FileSizeBytes, out var fileSizeBytesText)
            || !TryGet(metadata, AzureBlobDatasetMetadataKeys.Sha256Checksum, out var sha256Checksum)
            || !long.TryParse(fileSizeBytesText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var fileSizeBytes))
        {
            return null;
        }

        return new AzureBlobDatasetMetadata(
            regionId,
            version,
            fileSizeBytes,
            sha256Checksum);
    }

    private static bool TryGet(
        IDictionary<string, string> metadata,
        string key,
        out string value)
    {
        if (metadata.TryGetValue(key, out value!))
            return true;

        foreach (var item in metadata)
        {
            if (string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = item.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}
