namespace POIneer.Render.Application.Options;

// Configuration for publishing validated dataset artifacts to Azure Blob Storage.
public sealed class AzureBlobPublisherOptions
{
    public const string SectionName = "AzureBlobPublisher";

    public string? AccountName { get; init; }

    public string? BlobEndpoint { get; init; }

    public string? ContainerName { get; init; }

    public int MaxUploadsPerRun { get; init; } = 1;

    public long MaxUploadBytesPerRun { get; init; } = 1_073_741_824;
}
