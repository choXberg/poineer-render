using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using POIneer.Render.Application.Contracts;
using POIneer.Render.Infrastructure.FileSystem;
using POIneer.Render.TestHelpers;
using Xunit;

namespace POIneer.Render.UnitTests.Infrastructure.FileSystem;

public sealed class FileRegionRenderStateStoreTests
{
    public static ILogger<FileRegionRenderStateStore> Logger { get; } = NullLogger<FileRegionRenderStateStore>.Instance;

    [Fact]
    public async Task ReadAsync_ReturnsNull_WhenStateFileDoesNotExist()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("missing-render-state", false);
        var sut = new FileRegionRenderStateStore(Logger);

        // Act
        var state = await sut.ReadAsync(Path.Combine(tempDir.DirectoryPath, "render-state.json"));

        // Assert
        state.Should().BeNull();
    }

    [Fact]
    public async Task ReadAsync_ReturnsNull_WhenStateFileContainsInvalidJson()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("corrupt-render-state", false);
        var statePath = Path.Combine(tempDir.DirectoryPath, "render-state.json");
        TestFiles.WriteAllText(statePath, "{ partially-written");
        var sut = new FileRegionRenderStateStore(Logger);

        // Act
        var state = await sut.ReadAsync(statePath);

        // Assert
        state.Should().BeNull();
    }

    [Fact]
    public async Task ReadAsync_ReturnsNull_WhenStateFileIsIncomplete()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("incomplete-render-state", false);
        var statePath = Path.Combine(tempDir.DirectoryPath, "render-state.json");
        TestFiles.WriteAllText(statePath, "{}");
        var sut = new FileRegionRenderStateStore(Logger);

        // Act
        var state = await sut.ReadAsync(statePath);

        // Assert
        state.Should().BeNull();
    }

    [Fact]
    public async Task WriteAsync_AndReadAsync_RoundTripState()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("roundtrip-render-state", false);
        var statePath = Path.Combine(tempDir.DirectoryPath, "berlin", "render-state.json");
        var expectedState = new RegionRenderState(
            RegionId: "berlin",
            PbfUrl: "https://example.com/berlin.osm.pbf",
            LastProcessedMetadata: new RegionUpdateMetadata(
                ETag: "\"abc\"",
                LastModified: DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                ContentLength: 42),
            ProcessedAtUtc: DateTimeOffset.Parse("2026-01-02T00:00:00Z"));
        var sut = new FileRegionRenderStateStore(Logger);

        // Act
        await sut.WriteAsync(statePath, expectedState);
        var actualState = await sut.ReadAsync(statePath);

        // Assert
        actualState.Should().Be(expectedState);
    }

    [Fact]
    public async Task WriteAsync_ReplacesExistingState_AndDoesNotLeaveTemporaryFile()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("replace-render-state", false);
        var statePath = Path.Combine(tempDir.DirectoryPath, "berlin", "render-state.json");
        var oldState = new RegionRenderState(
            RegionId: "berlin",
            PbfUrl: "https://example.com/berlin-old.osm.pbf",
            LastProcessedMetadata: new RegionUpdateMetadata(
                ETag: "\"old\"",
                LastModified: DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                ContentLength: 42),
            ProcessedAtUtc: DateTimeOffset.Parse("2026-01-02T00:00:00Z"));
        var newState = oldState with
        {
            PbfUrl = "https://example.com/berlin-new.osm.pbf",
            LastProcessedMetadata = oldState.LastProcessedMetadata with { ETag = "\"new\"" },
            ProcessedAtUtc = DateTimeOffset.Parse("2026-01-03T00:00:00Z")
        };
        var sut = new FileRegionRenderStateStore(Logger);

        // Act
        await sut.WriteAsync(statePath, oldState);
        await sut.WriteAsync(statePath, newState);
        var actualState = await sut.ReadAsync(statePath);

        // Assert
        actualState.Should().Be(newState);
        Directory.GetFiles(Path.GetDirectoryName(statePath)!, "*.tmp").Should().BeEmpty();
    }
}
