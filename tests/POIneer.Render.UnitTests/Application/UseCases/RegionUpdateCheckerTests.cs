using FluentAssertions;
using POIneer.Render.Application.Contracts;
using POIneer.Render.Application.Ports;
using POIneer.Render.Application.UseCases;
using Xunit;

namespace POIneer.Render.UnitTests.Application.UseCases;

public sealed class RegionUpdateCheckerTests
{
    private static readonly RegionDto Berlin = new(
        Id: "berlin",
        Name: "Berlin",
        PbfUrl: "https://example.com/berlin.osm.pbf",
        Poly: "berlin.poly");

    [Fact]
    public async Task CheckAsync_ShouldRedownloadPbf_WhenNoStoredStateExists()
    {
        // Arrange
        var metadata = new RegionUpdateMetadata("\"abc\"", DateTimeOffset.Parse("2026-01-01T00:00:00Z"), 42);
        var stateStore = new InMemoryRegionRenderStateStore();
        var sut = new RegionUpdateChecker(new StubRemotePbfMetadataReader(metadata), stateStore);

        // Act
        var result = await sut.CheckAsync(Berlin, "state.json");

        // Assert
        result.ShouldRedownloadPbf.Should().BeTrue();
        result.Reason.Should().Be("No previous render state exists for this region.");
    }

    [Fact]
    public async Task CheckAsync_ShouldNotRedownloadPbf_WhenETagIsUnchanged()
    {
        // Arrange
        var remoteMetadata = new RegionUpdateMetadata("\"abc\"", DateTimeOffset.Parse("2026-01-02T00:00:00Z"), 128);
        var storedMetadata = new RegionUpdateMetadata("\"abc\"", DateTimeOffset.Parse("2026-01-01T00:00:00Z"), 42);
        var stateStore = new InMemoryRegionRenderStateStore(
            new RegionRenderState(Berlin.Id, Berlin.PbfUrl, storedMetadata, DateTimeOffset.UtcNow));
        var sut = new RegionUpdateChecker(new StubRemotePbfMetadataReader(remoteMetadata), stateStore);

        // Act
        var result = await sut.CheckAsync(Berlin, "state.json");

        // Assert
        result.ShouldRedownloadPbf.Should().BeFalse("ETag is preferred over Last-Modified when present");
        result.Reason.Should().Be("Remote PBF metadata is unchanged.");
    }

    [Fact]
    public async Task CheckAsync_ShouldRedownloadPbf_WhenETagChanged()
    {
        // Arrange
        var remoteMetadata = new RegionUpdateMetadata("\"new\"", DateTimeOffset.Parse("2026-01-01T00:00:00Z"), 42);
        var storedMetadata = new RegionUpdateMetadata("\"old\"", DateTimeOffset.Parse("2026-01-01T00:00:00Z"), 42);
        var stateStore = new InMemoryRegionRenderStateStore(
            new RegionRenderState(Berlin.Id, Berlin.PbfUrl, storedMetadata, DateTimeOffset.UtcNow));
        var sut = new RegionUpdateChecker(new StubRemotePbfMetadataReader(remoteMetadata), stateStore);

        // Act
        var result = await sut.CheckAsync(Berlin, "state.json");

        // Assert
        result.ShouldRedownloadPbf.Should().BeTrue();
        result.Reason.Should().Be("Remote ETag changed.");
    }

    [Fact]
    public async Task CheckAsync_ShouldNotRedownloadPbf_WhenLastModifiedIsUnchangedAndETagIsMissing()
    {
        // Arrange
        var lastModified = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var remoteMetadata = new RegionUpdateMetadata(null, lastModified, 128);
        var storedMetadata = new RegionUpdateMetadata(null, lastModified, 42);
        var stateStore = new InMemoryRegionRenderStateStore(
            new RegionRenderState(Berlin.Id, Berlin.PbfUrl, storedMetadata, DateTimeOffset.UtcNow));
        var sut = new RegionUpdateChecker(new StubRemotePbfMetadataReader(remoteMetadata), stateStore);

        // Act
        var result = await sut.CheckAsync(Berlin, "state.json");

        // Assert
        result.ShouldRedownloadPbf.Should().BeFalse("Last-Modified is the fallback when ETag is unavailable");
    }

    [Fact]
    public async Task CheckAsync_ShouldRedownloadPbf_WhenNoReliableRemoteMetadataExists()
    {
        // Arrange
        var remoteMetadata = new RegionUpdateMetadata(null, null, 128);
        var storedMetadata = new RegionUpdateMetadata(null, null, 128);
        var stateStore = new InMemoryRegionRenderStateStore(
            new RegionRenderState(Berlin.Id, Berlin.PbfUrl, storedMetadata, DateTimeOffset.UtcNow));
        var sut = new RegionUpdateChecker(new StubRemotePbfMetadataReader(remoteMetadata), stateStore);

        // Act
        var result = await sut.CheckAsync(Berlin, "state.json");

        // Assert
        result.ShouldRedownloadPbf.Should().BeTrue();
        result.Reason.Should().Be("Remote metadata has no ETag or Last-Modified value.");
    }

    [Fact]
    public async Task MarkProcessedAsync_WritesCurrentMetadataToState()
    {
        // Arrange
        var metadata = new RegionUpdateMetadata("\"abc\"", DateTimeOffset.Parse("2026-01-01T00:00:00Z"), 42);
        var stateStore = new InMemoryRegionRenderStateStore();
        var sut = new RegionUpdateChecker(new StubRemotePbfMetadataReader(metadata), stateStore);

        // Act
        await sut.MarkProcessedAsync(Berlin, "state.json", metadata);

        // Assert
        stateStore.WrittenState.Should().NotBeNull();
        stateStore.WrittenState!.RegionId.Should().Be(Berlin.Id);
        stateStore.WrittenState.PbfUrl.Should().Be(Berlin.PbfUrl);
        stateStore.WrittenState.LastProcessedMetadata.Should().Be(metadata);
        stateStore.WrittenPath.Should().Be("state.json");
    }

    private sealed class StubRemotePbfMetadataReader : IRemotePbfMetadataReader
    {
        private readonly RegionUpdateMetadata _metadata;

        public StubRemotePbfMetadataReader(RegionUpdateMetadata metadata)
            => _metadata = metadata;

        public Task<RegionUpdateMetadata> GetMetadataAsync(
            string pbfUrl,
            CancellationToken ct = default)
            => Task.FromResult(_metadata);
    }

    private sealed class InMemoryRegionRenderStateStore : IRegionRenderStateStore
    {
        private readonly RegionRenderState? _state;

        public InMemoryRegionRenderStateStore(RegionRenderState? state = null)
            => _state = state;

        public string? WrittenPath { get; private set; }
        public RegionRenderState? WrittenState { get; private set; }

        public Task<RegionRenderState?> ReadAsync(
            string statePath,
            CancellationToken ct = default)
            => Task.FromResult(_state);

        public Task WriteAsync(
            string statePath,
            RegionRenderState state,
            CancellationToken ct = default)
        {
            WrittenPath = statePath;
            WrittenState = state;
            return Task.CompletedTask;
        }
    }
}
