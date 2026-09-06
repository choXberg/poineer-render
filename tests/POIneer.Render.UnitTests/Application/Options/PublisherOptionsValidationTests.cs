using FluentAssertions;
using POIneer.Render.Application.Contracts;
using POIneer.Render.Application.Options;
using Xunit;

namespace POIneer.Render.UnitTests.Application.Options;

public sealed class PublisherOptionsValidationTests
{
    [Fact]
    public void HasRequiredDestinationDir_ReturnsTrue_WhenTargetIsLocalAndDestinationDirIsSet()
    {
        var options = new PublisherOptions
        {
            Target = DatasetPublisherTarget.Local,
            DestinationDir = "publish-dir"
        };

        PublisherOptionsValidation.HasRequiredDestinationDir(options).Should().BeTrue();
    }

    [Fact]
    public void HasRequiredDestinationDir_ReturnsFalse_WhenTargetIsLocalAndDestinationDirIsMissing()
    {
        var options = new PublisherOptions
        {
            Target = DatasetPublisherTarget.Local,
            DestinationDir = ""
        };

        PublisherOptionsValidation.HasRequiredDestinationDir(options).Should().BeFalse();
    }

    [Fact]
    public void HasRequiredDestinationDir_ReturnsTrue_WhenTargetIsAzureBlob()
    {
        var options = new PublisherOptions
        {
            Target = DatasetPublisherTarget.AzureBlob,
            DestinationDir = null
        };

        PublisherOptionsValidation.HasRequiredDestinationDir(options).Should().BeTrue();
    }

    [Fact]
    public void HasDefinedTarget_ReturnsTrue_WhenTargetIsKnown()
    {
        var options = new PublisherOptions
        {
            Target = DatasetPublisherTarget.AzureBlob
        };

        PublisherOptionsValidation.HasDefinedTarget(options).Should().BeTrue();
    }

    [Fact]
    public void HasDefinedTarget_ReturnsFalse_WhenTargetIsUnknownNumericValue()
    {
        var options = new PublisherOptions
        {
            Target = (DatasetPublisherTarget)999
        };

        PublisherOptionsValidation.HasDefinedTarget(options).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("Local", true)]
    [InlineData("AzureBlob", true)]
    [InlineData("local", false)]
    [InlineData("AzureBlobb", false)]
    public void IsDefinedTargetName_ReturnsExpectedResult(string? target, bool expected)
    {
        PublisherOptionsValidation.IsDefinedTargetName(target).Should().Be(expected);
    }
}
