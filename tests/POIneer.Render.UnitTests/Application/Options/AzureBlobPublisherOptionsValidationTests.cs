using FluentAssertions;
using POIneer.Render.Application.Options;
using Xunit;

namespace POIneer.Render.UnitTests.Application.Options;

public sealed class AzureBlobPublisherOptionsValidationTests
{
    [Fact]
    public void HasAccountNameOrBlobEndpoint_ReturnsTrue_WhenAccountNameIsSet()
    {
        var options = new AzureBlobPublisherOptions
        {
            AccountName = "poineerstoragedev"
        };

        AzureBlobPublisherOptionsValidation.HasAccountNameOrBlobEndpoint(options).Should().BeTrue();
    }

    [Fact]
    public void HasAccountNameOrBlobEndpoint_ReturnsTrue_WhenBlobEndpointIsSet()
    {
        var options = new AzureBlobPublisherOptions
        {
            BlobEndpoint = "https://poineerstoragedev.blob.core.windows.net"
        };

        AzureBlobPublisherOptionsValidation.HasAccountNameOrBlobEndpoint(options).Should().BeTrue();
    }

    [Fact]
    public void HasAccountNameOrBlobEndpoint_ReturnsFalse_WhenBothAccountNameAndBlobEndpointAreMissing()
    {
        var options = new AzureBlobPublisherOptions();

        AzureBlobPublisherOptionsValidation.HasAccountNameOrBlobEndpoint(options).Should().BeFalse();
    }

    [Fact]
    public void HasContainerName_ReturnsTrue_WhenContainerNameIsSet()
    {
        var options = new AzureBlobPublisherOptions
        {
            ContainerName = "regions"
        };

        AzureBlobPublisherOptionsValidation.HasContainerName(options).Should().BeTrue();
    }

    [Fact]
    public void HasContainerName_ReturnsFalse_WhenContainerNameIsMissing()
    {
        var options = new AzureBlobPublisherOptions
        {
            ContainerName = ""
        };

        AzureBlobPublisherOptionsValidation.HasContainerName(options).Should().BeFalse();
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void HasPositiveMaxUploadsPerRun_ReturnsExpectedResult(int value, bool expected)
    {
        var options = new AzureBlobPublisherOptions
        {
            MaxUploadsPerRun = value
        };

        AzureBlobPublisherOptionsValidation.HasPositiveMaxUploadsPerRun(options).Should().Be(expected);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void HasPositiveMaxUploadBytesPerRun_ReturnsExpectedResult(long value, bool expected)
    {
        var options = new AzureBlobPublisherOptions
        {
            MaxUploadBytesPerRun = value
        };

        AzureBlobPublisherOptionsValidation.HasPositiveMaxUploadBytesPerRun(options).Should().Be(expected);
    }
}
