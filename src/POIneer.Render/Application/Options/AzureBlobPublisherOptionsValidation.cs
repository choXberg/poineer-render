namespace POIneer.Render.Application.Options;

public static class AzureBlobPublisherOptionsValidation
{
    public const string RequiredAccountOrEndpointMessage =
        "AzureBlobPublisher:AccountName or AzureBlobPublisher:BlobEndpoint must be set";

    public const string RequiredContainerNameMessage =
        "AzureBlobPublisher:ContainerName must be set";

    public const string PositiveMaxUploadsPerRunMessage =
        "AzureBlobPublisher:MaxUploadsPerRun must be greater than zero";

    public const string PositiveMaxUploadBytesPerRunMessage =
        "AzureBlobPublisher:MaxUploadBytesPerRun must be greater than zero";

    public static bool HasAccountNameOrBlobEndpoint(AzureBlobPublisherOptions options)
        => !string.IsNullOrWhiteSpace(options.AccountName)
           || !string.IsNullOrWhiteSpace(options.BlobEndpoint);

    public static bool HasContainerName(AzureBlobPublisherOptions options)
        => !string.IsNullOrWhiteSpace(options.ContainerName);

    public static bool HasPositiveMaxUploadsPerRun(AzureBlobPublisherOptions options)
        => options.MaxUploadsPerRun > 0;

    public static bool HasPositiveMaxUploadBytesPerRun(AzureBlobPublisherOptions options)
        => options.MaxUploadBytesPerRun > 0;
}
