using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using POIneer.Render.Application.Contracts;
using POIneer.Render.Infrastructure.FileSystem;
using POIneer.Render.TestHelpers;
using Xunit;

namespace POIneer.Render.IntegrationTests.Infrastructure.FileSystem;

// Exercises LocalDatasetPublisher against the real filesystem. Source/destination
// directories are isolated per test via TestTemporaryDirectories, mirroring the
// conventions established by FileSingleInstanceLockTests.
public sealed class LocalDatasetPublisherTests
{
    // NullLogger, not a real LoggerFactory: none of these tests assert log output, and a
    // LoggerFactory instance would never be disposed here (see tests/README.md).
    private static readonly ILogger<LocalDatasetPublisher> Logger = NullLogger<LocalDatasetPublisher>.Instance;

    [Fact]
    public async Task PublishAsync_CopiesArtifact_ToDestination_WithRegionAndVersionInFilename()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("publish-copies-artifact", false);
        var sourcePath = Path.Combine(tempDir.DirectoryPath, "poi.sqlite");
        TestFiles.WriteAllText(sourcePath, "dataset bytes");

        var destinationDir = Path.Combine(tempDir.DirectoryPath, "publish");
        var sut = new LocalDatasetPublisher(Logger, TestOptionsFactory.CreatePublisherOptions(destinationDir));

        var request = new DatasetPublishRequest("berlin", "20260101000000000", sourcePath);

        // Act
        var result = await sut.PublishAsync(request, CancellationToken.None);

        // Assert
        var expectedDestinationPath = Path.GetFullPath(
            Path.Combine(destinationDir, "berlin", "berlin.20260101000000000.sqlite"));

        result.DestinationPath.Should().Be(expectedDestinationPath);
        result.WasSkipped.Should().BeFalse();
        File.Exists(expectedDestinationPath).Should().BeTrue();
        (await File.ReadAllTextAsync(expectedDestinationPath)).Should().Be("dataset bytes");
    }

    // A globally unique, hierarchical RegionId (ADR 0007) such as
    // "geofabrik/europe/germany/berlin" must produce one nested directory per segment,
    // with the published file named after only the leaf ("berlin") segment - not the
    // whole hierarchy, which the directory structure already encodes.
    [Fact]
    public async Task PublishAsync_CopiesArtifact_ToNestedDestination_ForHierarchicalRegionId()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("publish-copies-artifact-hierarchical", false);
        var sourcePath = Path.Combine(tempDir.DirectoryPath, "poi.sqlite");
        TestFiles.WriteAllText(sourcePath, "dataset bytes");

        var destinationDir = Path.Combine(tempDir.DirectoryPath, "publish");
        var sut = new LocalDatasetPublisher(Logger, TestOptionsFactory.CreatePublisherOptions(destinationDir));

        var request = new DatasetPublishRequest(
            "geofabrik/europe/germany/berlin", "2-abc123", sourcePath);

        // Act
        var result = await sut.PublishAsync(request, CancellationToken.None);

        // Assert
        var expectedDestinationPath = Path.GetFullPath(
            Path.Combine(destinationDir, "geofabrik", "europe", "germany", "berlin", "berlin.2-abc123.sqlite"));

        result.DestinationPath.Should().Be(expectedDestinationPath);
        result.WasSkipped.Should().BeFalse();
        File.Exists(expectedDestinationPath).Should().BeTrue();
        (await File.ReadAllTextAsync(expectedDestinationPath)).Should().Be("dataset bytes");
    }

    [Fact]
    public async Task PublishAsync_CreatesMissingDestinationDirectory()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("publish-creates-destination-dir", false);
        var sourcePath = Path.Combine(tempDir.DirectoryPath, "poi.sqlite");
        TestFiles.WriteAllText(sourcePath, "dataset bytes");

        var destinationDir = Path.Combine(tempDir.DirectoryPath, "nested", "publish", "dir");
        var sut = new LocalDatasetPublisher(Logger, TestOptionsFactory.CreatePublisherOptions(destinationDir));

        // Act
        var result = await sut.PublishAsync(
            new DatasetPublishRequest("berlin", "20260101000000000", sourcePath),
            CancellationToken.None);

        // Assert
        File.Exists(result.DestinationPath).Should().BeTrue();
    }

    [Fact]
    public async Task PublishAsync_LeavesTheSourceFile_InPlace()
    {
        // Arrange: publishing copies the already-canonical render output - it must not
        // remove it, since it still needs to remain the canonical file in outDir.
        await using var tempDir = TestTemporaryDirectories.Create("publish-leaves-source-in-place", false);
        var sourcePath = Path.Combine(tempDir.DirectoryPath, "poi.sqlite");
        TestFiles.WriteAllText(sourcePath, "dataset bytes");

        var destinationDir = Path.Combine(tempDir.DirectoryPath, "publish");
        var sut = new LocalDatasetPublisher(Logger, TestOptionsFactory.CreatePublisherOptions(destinationDir));

        // Act
        await sut.PublishAsync(
            new DatasetPublishRequest("berlin", "20260101000000000", sourcePath),
            CancellationToken.None);

        // Assert
        File.Exists(sourcePath).Should().BeTrue();
    }

    [Fact]
    public async Task PublishAsync_SkipsExistingDestination_WhenOverwritePolicyIsSkip_AndDestinationMatchesSource()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("publish-skips-when-policy-is-skip", false);
        var sourcePath = Path.Combine(tempDir.DirectoryPath, "poi.sqlite");
        TestFiles.WriteAllText(sourcePath, "dataset bytes");

        var destinationDir = Path.Combine(tempDir.DirectoryPath, "publish");
        var sut = new LocalDatasetPublisher(
            Logger,
            TestOptionsFactory.CreatePublisherOptions(destinationDir, DatasetPublishOverwritePolicy.Skip));

        var request = new DatasetPublishRequest("berlin", "20260101000000000", sourcePath);

        var first = await sut.PublishAsync(request, CancellationToken.None);

        // Act
        var second = await sut.PublishAsync(request, CancellationToken.None);

        // Assert
        second.WasSkipped.Should().BeTrue();
        second.DestinationPath.Should().Be(first.DestinationPath);
        (await File.ReadAllTextAsync(second.DestinationPath)).Should().Be("dataset bytes");
    }

    [Fact]
    public async Task PublishAsync_SkipsExistingDestination_WhenOverwritePolicyIsSkip_EvenWhenDestinationDiffersFromSource()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("publish-skips-mismatched-skip-target", false);
        var sourcePath = Path.Combine(tempDir.DirectoryPath, "poi.sqlite");
        TestFiles.WriteAllText(sourcePath, "new dataset bytes");

        var destinationDir = Path.Combine(tempDir.DirectoryPath, "publish");
        var sut = new LocalDatasetPublisher(
            Logger,
            TestOptionsFactory.CreatePublisherOptions(destinationDir, DatasetPublishOverwritePolicy.Skip));

        var request = new DatasetPublishRequest("berlin", "20260101000000000", sourcePath);
        var first = await sut.PublishAsync(request, CancellationToken.None);
        TestFiles.WriteAllText(sourcePath, "changed dataset bytes");

        // Act
        var second = await sut.PublishAsync(request, CancellationToken.None);

        // Assert
        second.WasSkipped.Should().BeTrue();
        second.DestinationPath.Should().Be(first.DestinationPath);
        (await File.ReadAllTextAsync(second.DestinationPath)).Should().Be("new dataset bytes");
    }

    [Fact]
    public async Task PublishAsync_SkipsExistingDestination_WhenOverwritePolicyIsSkipIfIdentical_AndDestinationMatchesSource()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("publish-skip-if-identical-skips-matching-target", false);
        var sourcePath = Path.Combine(tempDir.DirectoryPath, "poi.sqlite");
        TestFiles.WriteAllText(sourcePath, "dataset bytes");

        var destinationDir = Path.Combine(tempDir.DirectoryPath, "publish");
        var sut = new LocalDatasetPublisher(
            Logger,
            TestOptionsFactory.CreatePublisherOptions(destinationDir, DatasetPublishOverwritePolicy.SkipIfIdentical));

        var request = new DatasetPublishRequest("berlin", "20260101000000000", sourcePath);
        var first = await sut.PublishAsync(request, CancellationToken.None);

        // Act
        var second = await sut.PublishAsync(request, CancellationToken.None);

        // Assert
        second.WasSkipped.Should().BeTrue();
        second.DestinationPath.Should().Be(first.DestinationPath);
        (await File.ReadAllTextAsync(second.DestinationPath)).Should().Be("dataset bytes");
    }

    [Fact]
    public async Task PublishAsync_ThrowsIOException_WhenOverwritePolicyIsSkipIfIdentical_ButDestinationDiffersFromSource()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("publish-skip-if-identical-replaces-mismatched-target", false);
        var sourcePath = Path.Combine(tempDir.DirectoryPath, "poi.sqlite");
        TestFiles.WriteAllText(sourcePath, "new dataset bytes");

        var destinationDir = Path.Combine(tempDir.DirectoryPath, "publish");
        var sut = new LocalDatasetPublisher(
            Logger,
            TestOptionsFactory.CreatePublisherOptions(destinationDir, DatasetPublishOverwritePolicy.SkipIfIdentical));

        var request = new DatasetPublishRequest("berlin", "20260101000000000", sourcePath);
        var first = await sut.PublishAsync(request, CancellationToken.None);
        TestFiles.WriteAllText(sourcePath, "changed dataset bytes");

        // Act
        var act = () => sut.PublishAsync(request, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<IOException>()
            .WithMessage("*differs from the source artifact*Overwrite*");
        (await File.ReadAllTextAsync(first.DestinationPath)).Should().Be("new dataset bytes");
    }

    [Fact]
    public async Task PublishAsync_OverwritesExistingDestination_WhenOverwritePolicyIsOverwrite()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("publish-overwrites-when-policy-is-overwrite", false);
        var sourcePath = Path.Combine(tempDir.DirectoryPath, "poi.sqlite");
        TestFiles.WriteAllText(sourcePath, "new dataset bytes");

        var destinationDir = Path.Combine(tempDir.DirectoryPath, "publish");
        var sut = new LocalDatasetPublisher(
            Logger,
            TestOptionsFactory.CreatePublisherOptions(destinationDir, DatasetPublishOverwritePolicy.Overwrite));

        var request = new DatasetPublishRequest("berlin", "20260101000000000", sourcePath);

        var first = await sut.PublishAsync(request, CancellationToken.None);
        TestFiles.WriteAllText(sourcePath, "changed dataset bytes");

        // Act
        var second = await sut.PublishAsync(request, CancellationToken.None);

        // Assert
        second.WasSkipped.Should().BeFalse();
        second.DestinationPath.Should().Be(first.DestinationPath);
        (await File.ReadAllTextAsync(second.DestinationPath)).Should().Be("changed dataset bytes");
    }

    [Fact]
    public async Task PublishAsync_ThrowsIOException_WhenOverwritePolicyIsFail_AndDestinationAlreadyExists()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("publish-throws-when-policy-is-fail", false);
        var sourcePath = Path.Combine(tempDir.DirectoryPath, "poi.sqlite");
        TestFiles.WriteAllText(sourcePath, "dataset bytes");

        var destinationDir = Path.Combine(tempDir.DirectoryPath, "publish");
        var sut = new LocalDatasetPublisher(
            Logger,
            TestOptionsFactory.CreatePublisherOptions(destinationDir, DatasetPublishOverwritePolicy.Fail));

        var request = new DatasetPublishRequest("berlin", "20260101000000000", sourcePath);
        await sut.PublishAsync(request, CancellationToken.None);

        // Act
        var act = () => sut.PublishAsync(request, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<IOException>();
    }

    [Fact]
    public async Task PublishAsync_ThrowsFileNotFoundException_WhenSourceDoesNotExist()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("publish-throws-when-source-missing", false);
        var missingSourcePath = Path.Combine(tempDir.DirectoryPath, "does-not-exist.sqlite");
        var destinationDir = Path.Combine(tempDir.DirectoryPath, "publish");
        var sut = new LocalDatasetPublisher(Logger, TestOptionsFactory.CreatePublisherOptions(destinationDir));

        var request = new DatasetPublishRequest("berlin", "20260101000000000", missingSourcePath);

        // Act
        var act = () => sut.PublishAsync(request, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task PublishAsync_DoesNotLeaveAStagingFile_BehindOnSuccess()
    {
        // Arrange: PublishAsync copies through a ".tmp" staging file before renaming it
        // into place - confirm that staging file is gone once publishing succeeds.
        await using var tempDir = TestTemporaryDirectories.Create("publish-cleans-up-staging-file", false);
        var sourcePath = Path.Combine(tempDir.DirectoryPath, "poi.sqlite");
        TestFiles.WriteAllText(sourcePath, "dataset bytes");

        var destinationDir = Path.Combine(tempDir.DirectoryPath, "publish");
        var sut = new LocalDatasetPublisher(Logger, TestOptionsFactory.CreatePublisherOptions(destinationDir));

        // Act
        var result = await sut.PublishAsync(
            new DatasetPublishRequest("berlin", "20260101000000000", sourcePath),
            CancellationToken.None);

        // Assert
        File.Exists(result.DestinationPath + ".tmp").Should().BeFalse();
    }

    [Fact]
    public async Task PublishAsync_CleansUpTheStagingFile_WhenTheCopyIsCancelled()
    {
        // Arrange: FileMode.Create on the staging FileStream creates/truncates the ".tmp"
        // file immediately, before any bytes are copied - so a cancellation during
        // CopyToAsync still leaves a (partial) staging file on disk unless PublishAsync
        // cleans it up. Regression test for the Copilot review finding that a cancelled or
        // failed copy/move could leave ".tmp" files cluttering the publish directory.
        await using var tempDir = TestTemporaryDirectories.Create("publish-cleans-up-staging-file-on-cancel", false);
        var sourcePath = Path.Combine(tempDir.DirectoryPath, "poi.sqlite");
        TestFiles.WriteAllText(sourcePath, "dataset bytes");

        var destinationDir = Path.Combine(tempDir.DirectoryPath, "publish");
        var sut = new LocalDatasetPublisher(Logger, TestOptionsFactory.CreatePublisherOptions(destinationDir));

        var request = new DatasetPublishRequest("berlin", "20260101000000000", sourcePath);
        var expectedDestinationPath = Path.GetFullPath(
            Path.Combine(destinationDir, "berlin", "berlin.20260101000000000.sqlite"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        var act = () => sut.PublishAsync(request, cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(expectedDestinationPath + ".tmp").Should().BeFalse();
        File.Exists(expectedDestinationPath).Should().BeFalse();
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../escape")]
    [InlineData("berlin/../../etc")]
    [InlineData("berlin/..")]
    [InlineData("berlin/.")]
    [InlineData("/berlin")]
    [InlineData("berlin/")]
    [InlineData("berlin//sub")]
    [InlineData("berlin\\sub")]
    [InlineData("berlin:sub")]
    [InlineData("berlin sub")]
    public async Task PublishAsync_ThrowsArgumentException_WhenRegionIdIsNotASafePathSegment(string regionId)
    {
        // Arrange: RegionId is interpolated directly into the destination directory name
        // and filename - it must never be able to escape DestinationDir or produce an
        // invalid filename, regardless of how it was ultimately produced. Note that
        // "berlin/sub" itself is now a *valid* hierarchical RegionId (ADR 0007) - see
        // PublishAsync_CopiesArtifact_ToNestedDestination_ForHierarchicalRegionId - only
        // a "." or ".." segment, an empty segment (leading/trailing/doubled '/'), or a
        // disallowed character within a segment is rejected.
        await using var tempDir = TestTemporaryDirectories.Create("publish-rejects-unsafe-region-id", false);
        var sourcePath = Path.Combine(tempDir.DirectoryPath, "poi.sqlite");
        TestFiles.WriteAllText(sourcePath, "dataset bytes");

        var destinationDir = Path.Combine(tempDir.DirectoryPath, "publish");
        var sut = new LocalDatasetPublisher(Logger, TestOptionsFactory.CreatePublisherOptions(destinationDir));

        var request = new DatasetPublishRequest(regionId, "20260101000000000", sourcePath);

        // Act
        var act = () => sut.PublishAsync(request, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
        Directory.Exists(destinationDir).Should().BeFalse();
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../escape")]
    [InlineData("1/../../etc")]
    [InlineData("1/2")]
    [InlineData("1\\2")]
    [InlineData("1:2")]
    [InlineData("1 2")]
    public async Task PublishAsync_ThrowsArgumentException_WhenVersionIsNotASafePathSegment(string version)
    {
        // Arrange: Version is interpolated directly into the published filename - same
        // safety requirement as RegionId above.
        await using var tempDir = TestTemporaryDirectories.Create("publish-rejects-unsafe-version", false);
        var sourcePath = Path.Combine(tempDir.DirectoryPath, "poi.sqlite");
        TestFiles.WriteAllText(sourcePath, "dataset bytes");

        var destinationDir = Path.Combine(tempDir.DirectoryPath, "publish");
        var sut = new LocalDatasetPublisher(Logger, TestOptionsFactory.CreatePublisherOptions(destinationDir));

        var request = new DatasetPublishRequest("berlin", version, sourcePath);

        // Act
        var act = () => sut.PublishAsync(request, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
