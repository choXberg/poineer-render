using FluentAssertions;
using POIneer.Render.Infrastructure.FileSystem;
using POIneer.Render.TestHelpers;
using Xunit;

namespace POIneer.Render.IntegrationTests.Infrastructure.FileSystem;

// Exercises FileHashDatasetVersionCalculator against the real filesystem. The whole point
// of this type is that identical input always produces the identical version - that's what
// lets IDatasetPublisher handle republishing unchanged data idempotently under the
// configured overwrite policy - so these tests focus on that stability property rather than on the
// exact hash algorithm/format.
public sealed class FileHashDatasetVersionCalculatorTests
{
    [Fact]
    public async Task CalculateAsync_ReturnsTheSameVersion_ForByteIdenticalFiles()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("version-calculator-stable-for-identical-content", false);
        var pathA = Path.Combine(tempDir.DirectoryPath, "a.osm.pbf");
        var pathB = Path.Combine(tempDir.DirectoryPath, "b.osm.pbf");
        TestFiles.WriteAllText(pathA, "identical pbf bytes");
        TestFiles.WriteAllText(pathB, "identical pbf bytes");

        var sut = new FileHashDatasetVersionCalculator(TestOptionsFactory.CreatePublisherOptions("unused-destination-dir"));

        // Act
        var versionA = await sut.CalculateAsync(pathA, CancellationToken.None);
        var versionB = await sut.CalculateAsync(pathB, CancellationToken.None);

        // Assert
        versionA.Should().Be(versionB);
    }

    [Fact]
    public async Task CalculateAsync_ReturnsTheSameVersion_AcrossRepeatedCalls_ForTheSameFile()
    {
        // Arrange: this is the scenario the whole feature exists for - a forced re-render
        // of unchanged input must compute the same version both times.
        await using var tempDir = TestTemporaryDirectories.Create("version-calculator-stable-across-repeated-calls", false);
        var path = Path.Combine(tempDir.DirectoryPath, "berlin.osm.pbf");
        TestFiles.WriteAllText(path, "some pbf bytes");

        var sut = new FileHashDatasetVersionCalculator(TestOptionsFactory.CreatePublisherOptions("unused-destination-dir"));

        // Act
        var first = await sut.CalculateAsync(path, CancellationToken.None);
        var second = await sut.CalculateAsync(path, CancellationToken.None);

        // Assert
        first.Should().Be(second);
    }

    [Fact]
    public async Task CalculateAsync_ReturnsADifferentVersion_WhenFileContentDiffers()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("version-calculator-differs-for-different-content", false);
        var pathA = Path.Combine(tempDir.DirectoryPath, "a.osm.pbf");
        var pathB = Path.Combine(tempDir.DirectoryPath, "b.osm.pbf");
        TestFiles.WriteAllText(pathA, "old osm extract");
        TestFiles.WriteAllText(pathB, "new osm extract with an update");

        var sut = new FileHashDatasetVersionCalculator(TestOptionsFactory.CreatePublisherOptions("unused-destination-dir"));

        // Act
        var versionA = await sut.CalculateAsync(pathA, CancellationToken.None);
        var versionB = await sut.CalculateAsync(pathB, CancellationToken.None);

        // Assert
        versionA.Should().NotBe(versionB);
    }

    [Fact]
    public async Task CalculateAsync_ReturnsADifferentVersion_WhenSchemaVersionDiffers_ForTheSameFile()
    {
        // Arrange: a deliberate SchemaVersion bump must force a new version even though the
        // source PBF content is completely unchanged.
        await using var tempDir = TestTemporaryDirectories.Create("version-calculator-differs-for-different-schema-version", false);
        var path = Path.Combine(tempDir.DirectoryPath, "berlin.osm.pbf");
        TestFiles.WriteAllText(path, "same pbf bytes");

        var sutV1 = new FileHashDatasetVersionCalculator(
            TestOptionsFactory.CreatePublisherOptions("unused-destination-dir", schemaVersion: "1"));
        var sutV2 = new FileHashDatasetVersionCalculator(
            TestOptionsFactory.CreatePublisherOptions("unused-destination-dir", schemaVersion: "2"));

        // Act
        var versionV1 = await sutV1.CalculateAsync(path, CancellationToken.None);
        var versionV2 = await sutV2.CalculateAsync(path, CancellationToken.None);

        // Assert
        versionV1.Should().NotBe(versionV2);
    }

    [Fact]
    public async Task CalculateAsync_IncludesTheConfiguredSchemaVersion_InTheResult()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("version-calculator-includes-schema-version", false);
        var path = Path.Combine(tempDir.DirectoryPath, "berlin.osm.pbf");
        TestFiles.WriteAllText(path, "pbf bytes");

        var sut = new FileHashDatasetVersionCalculator(
            TestOptionsFactory.CreatePublisherOptions("unused-destination-dir", schemaVersion: "42"));

        // Act
        var version = await sut.CalculateAsync(path, CancellationToken.None);

        // Assert
        version.Should().StartWith("42-");
    }

    [Fact]
    public async Task CalculateAsync_ThrowsFileNotFoundException_WhenSourceDoesNotExist()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("version-calculator-throws-when-source-missing", false);
        var missingPath = Path.Combine(tempDir.DirectoryPath, "does-not-exist.osm.pbf");
        var sut = new FileHashDatasetVersionCalculator(TestOptionsFactory.CreatePublisherOptions("unused-destination-dir"));

        // Act
        var act = () => sut.CalculateAsync(missingPath, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<FileNotFoundException>();
    }
}
