using FluentAssertions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using POIneer.Render.Application.Contracts;
using POIneer.Render.Application.Options;
using POIneer.Render.Application.Ports;
using POIneer.Render.Ports;
using POIneer.Render.TestHelpers;
using Xunit;

namespace POIneer.Render.UnitTests.Cli;

public sealed class RunnerTests
{
    private readonly IFileDownloader _fileDownloader = Substitute.For<IFileDownloader>();
    private readonly IRegionSource _regionSource = Substitute.For<IRegionSource>();
    private readonly IRenderRegion _renderRegion = Substitute.For<IRenderRegion>();
    private readonly IRegionUpdateChecker _regionUpdateChecker = Substitute.For<IRegionUpdateChecker>();
    private readonly ISingleInstanceLockFactory _lockFactory = Substitute.For<ISingleInstanceLockFactory>();
    private readonly ISingleInstanceLock _instanceLock = Substitute.For<ISingleInstanceLock>();

    public RunnerTests()
    {
        // By default the lock is always available, so existing rendering behavior is unaffected.
        _instanceLock.TryAcquire().Returns(true);
        _lockFactory.Create(Arg.Any<string>()).Returns(_instanceLock);
        _regionUpdateChecker
            .CheckAsync(Arg.Any<RegionDto>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(ChangedResult(callInfo.ArgAt<RegionDto>(0))));
    }

    [Fact]
    public async Task RunAsync_ReturnsSuccessAndDoesNothing_WhenDryRunIsEnabled()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("runner-dry-run", false);
        var sut = CreateSut(tempDir.DirectoryPath, CreateOptions(dryRun: true));

        // Act
        var result = await sut.RunAsync(CancellationToken.None);

        // Assert
        result.Should().Be(0);
        await _regionSource.DidNotReceiveWithAnyArgs().GetRegionsAsync(default!, default);
        await _regionUpdateChecker.DidNotReceiveWithAnyArgs().CheckAsync(default!, default!, default);
        await _fileDownloader.DidNotReceiveWithAnyArgs().DownloadAsync(default!, default!, default);
        await _renderRegion.DidNotReceiveWithAnyArgs().RunAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task RunAsync_ThrowsFileNotFoundException_WhenRegionsFileDoesNotExist()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("runner-missing-regions", false);
        var options = CreateOptions(regionsJson: "missing-regions.json");
        var expectedRegionsPath = Path.GetFullPath(Path.Combine(tempDir.DirectoryPath, options.RegionsJson));
        var sut = CreateSut(tempDir.DirectoryPath, options);

        // Act
        var act = () => sut.RunAsync(CancellationToken.None);

        // Assert
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(act);
        ex.Message.Should().Contain(expectedRegionsPath);

        await _regionSource.DidNotReceiveWithAnyArgs().GetRegionsAsync(default!, default);
        await _regionUpdateChecker.DidNotReceiveWithAnyArgs().CheckAsync(default!, default!, default);
    }

    [Fact]
    public async Task RunAsync_ResolvesRelativePathsAgainstContentRoot_AndProcessesRegion()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("runner-resolves-relative-paths", false);
        var options = CreateOptions(
            workDir: "work",
            outDir: "out",
            regionsJson: "config/regions.json");
        var sut = CreateSut(tempDir.DirectoryPath, options);

        var regionsPath = Path.GetFullPath(Path.Combine(tempDir.DirectoryPath, options.RegionsJson));
        TestFiles.WriteAllText(regionsPath, "[]");

        var region = BerlinRegion();
        _regionSource
            .GetRegionsAsync(regionsPath, Arg.Any<CancellationToken>())
            .Returns(new[] { region });

        var workDir = Path.GetFullPath(Path.Combine(tempDir.DirectoryPath, options.WorkDir));
        var outDir = Path.GetFullPath(Path.Combine(tempDir.DirectoryPath, options.OutDir));
        var expectedPbfPath = Path.Combine(workDir, region.Id, "osm.pbf");
        _fileDownloader
            .DownloadAsync(region.PbfUrl, expectedPbfPath, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                TestFiles.WriteAllText(expectedPbfPath, "downloaded pbf");
                return Task.FromResult(expectedPbfPath);
            });

        // Act
        var result = await sut.RunAsync(CancellationToken.None);

        // Assert
        result.Should().Be(0);
        File.Exists(expectedPbfPath).Should().BeTrue();
        await _fileDownloader.Received(1).DownloadAsync(region.PbfUrl, expectedPbfPath, Arg.Any<CancellationToken>());
        await _renderRegion.Received(1).RunAsync(region, workDir, outDir, Arg.Any<CancellationToken>());
        await _regionUpdateChecker.Received(1).MarkProcessedAsync(
            region,
            Path.Combine(workDir, region.Id, "render-state.json"),
            Arg.Any<RegionUpdateMetadata>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_SkipsDownloadButRunsRenderUseCase_WhenPbfAlreadyExistsAndRemoteMetadataIsUnchanged()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("runner-skip-existing-pbf", false);
        var options = CreateOptions(overwriteDatabase: true, overwritePbf: false);
        var sut = CreateSut(tempDir.DirectoryPath, options);

        var regionsPath = Path.GetFullPath(Path.Combine(tempDir.DirectoryPath, options.RegionsJson));
        TestFiles.WriteAllText(regionsPath, "[]");

        var region = BerlinRegion();
        _regionSource.GetRegionsAsync(regionsPath, Arg.Any<CancellationToken>())
            .Returns(new[] { region });

        var workDir = Path.GetFullPath(Path.Combine(tempDir.DirectoryPath, options.WorkDir));
        var pbfPath = Path.Combine(workDir, region.Id, "osm.pbf");
        var statePath = Path.Combine(workDir, region.Id, "render-state.json");
        TestFiles.WriteAllText(pbfPath, "existing pbf");
        var metadata = new RegionUpdateMetadata("\"abc\"", DateTimeOffset.Parse("2026-01-01T00:00:00Z"), 42);
        _regionUpdateChecker
            .CheckAsync(region, statePath, Arg.Any<CancellationToken>())
            .Returns(new RegionUpdateCheckResult(
                ShouldRedownloadPbf: false,
                Reason: "Remote PBF metadata is unchanged.",
                RemoteMetadata: metadata,
                StoredState: new RegionRenderState(region.Id, region.PbfUrl, metadata, DateTimeOffset.UtcNow)));

        // Act
        await sut.RunAsync(CancellationToken.None);

        // Assert
        await _fileDownloader.DidNotReceiveWithAnyArgs().DownloadAsync(default!, default!, default);
        await _renderRegion.Received(1).RunAsync(
            region,
            workDir,
            Path.GetFullPath(Path.Combine(tempDir.DirectoryPath, options.OutDir)),
            Arg.Any<CancellationToken>());
        await _regionUpdateChecker.Received(1).MarkProcessedAsync(
            region,
            statePath,
            metadata,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_RedownloadsPbf_WhenPbfAlreadyExistsAndOverwritePbfIsEnabled()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("runner-redownload-existing-pbf", false);
        var options = CreateOptions(overwritePbf: true);
        var sut = CreateSut(tempDir.DirectoryPath, options);

        var regionsPath = Path.GetFullPath(Path.Combine(tempDir.DirectoryPath, options.RegionsJson));
        TestFiles.WriteAllText(regionsPath, "[]");

        var region = BerlinRegion();
        _regionSource.GetRegionsAsync(regionsPath, Arg.Any<CancellationToken>())
            .Returns(new[] { region });

        var workDir = Path.GetFullPath(Path.Combine(tempDir.DirectoryPath, options.WorkDir));
        var pbfPath = Path.Combine(workDir, region.Id, "osm.pbf");
        TestFiles.WriteAllText(pbfPath, "existing pbf");

        // Act
        await sut.RunAsync(CancellationToken.None);

        // Assert
        await _fileDownloader.Received(1).DownloadAsync(region.PbfUrl, pbfPath, Arg.Any<CancellationToken>());
        await _renderRegion.Received(1).RunAsync(
            region,
            workDir,
            Path.GetFullPath(Path.Combine(tempDir.DirectoryPath, options.OutDir)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_ProcessesOnlyMatchingRegion_WhenOnlyRegionIdIsConfigured()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("runner-only-region", false);
        var options = CreateOptions(onlyRegionId: "berlin");
        var sut = CreateSut(tempDir.DirectoryPath, options);

        var regionsPath = Path.GetFullPath(Path.Combine(tempDir.DirectoryPath, options.RegionsJson));
        TestFiles.WriteAllText(regionsPath, "[]");

        var berlin = BerlinRegion();
        var hamburg = new RegionDto(
            Id: "hamburg",
            Name: "Hamburg",
            PbfUrl: "https://example.com/hamburg.osm.pbf",
            Poly: "hamburg.poly");

        _regionSource.GetRegionsAsync(regionsPath, Arg.Any<CancellationToken>())
            .Returns(new[] { berlin, hamburg });

        var workDir = Path.GetFullPath(Path.Combine(tempDir.DirectoryPath, options.WorkDir));
        var outDir = Path.GetFullPath(Path.Combine(tempDir.DirectoryPath, options.OutDir));

        // Act
        await sut.RunAsync(CancellationToken.None);

        // Assert
        await _fileDownloader.Received(1).DownloadAsync(
            berlin.PbfUrl,
            Path.Combine(workDir, berlin.Id, "osm.pbf"),
            Arg.Any<CancellationToken>());

        await _renderRegion.Received(1).RunAsync(berlin, workDir, outDir, Arg.Any<CancellationToken>());
        await _renderRegion.DidNotReceive().RunAsync(hamburg, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_ContinuesWithRemainingRegions_AndReturnsNonZero_WhenARegionFails()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("runner-continues-after-region-failure", false);
        var options = CreateOptions();
        var sut = CreateSut(tempDir.DirectoryPath, options);

        var regionsPath = Path.GetFullPath(Path.Combine(tempDir.DirectoryPath, options.RegionsJson));
        TestFiles.WriteAllText(regionsPath, "[]");

        var berlin = BerlinRegion();
        var hamburg = new RegionDto(
            Id: "hamburg",
            Name: "Hamburg",
            PbfUrl: "https://example.com/hamburg.osm.pbf",
            Poly: "hamburg.poly");

        _regionSource.GetRegionsAsync(regionsPath, Arg.Any<CancellationToken>())
            .Returns(new[] { berlin, hamburg });

        var workDir = Path.GetFullPath(Path.Combine(tempDir.DirectoryPath, options.WorkDir));
        var outDir = Path.GetFullPath(Path.Combine(tempDir.DirectoryPath, options.OutDir));

        _renderRegion
            .RunAsync(berlin, workDir, outDir, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Generated dataset is invalid"));

        // Act
        var result = await sut.RunAsync(CancellationToken.None);

        // Assert
        result.Should().Be(1);
        await _renderRegion.Received(1).RunAsync(berlin, workDir, outDir, Arg.Any<CancellationToken>());
        await _renderRegion.Received(1).RunAsync(hamburg, workDir, outDir, Arg.Any<CancellationToken>());
        await _regionUpdateChecker.DidNotReceive().MarkProcessedAsync(
            berlin,
            Arg.Any<string>(),
            Arg.Any<RegionUpdateMetadata>(),
            Arg.Any<CancellationToken>());
        await _regionUpdateChecker.Received(1).MarkProcessedAsync(
            hamburg,
            Arg.Any<string>(),
            Arg.Any<RegionUpdateMetadata>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_ForwardsCancellationToken_ToCollaborators()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("runner-forwards-cancellation-token", false);
        var options = CreateOptions();
        var sut = CreateSut(tempDir.DirectoryPath, options);

        var regionsPath = Path.GetFullPath(Path.Combine(tempDir.DirectoryPath, options.RegionsJson));
        TestFiles.WriteAllText(regionsPath, "[]");

        var region = BerlinRegion();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        _regionSource.GetRegionsAsync(regionsPath, ct)
            .Returns(new[] { region });

        // Act
        await sut.RunAsync(ct);

        // Assert
        await _regionSource.Received(1).GetRegionsAsync(regionsPath, ct);
        await _fileDownloader.Received(1).DownloadAsync(
            region.PbfUrl,
            Arg.Any<string>(),
            ct);
        await _regionUpdateChecker.Received(1).CheckAsync(
            region,
            Arg.Any<string>(),
            ct);
        await _renderRegion.Received(1).RunAsync(
            region,
            Arg.Any<string>(),
            Arg.Any<string>(),
            ct);
    }

    [Fact]
    public async Task RunAsync_SkipsExecutionAndReturnsZero_WhenAnotherInstanceHoldsTheLock()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("runner-skips-when-locked", false);
        var options = CreateOptions();
        _instanceLock.TryAcquire().Returns(false);
        var sut = CreateSut(tempDir.DirectoryPath, options);

        // Act
        var result = await sut.RunAsync(CancellationToken.None);

        // Assert
        result.Should().Be(0);
        await _regionSource.DidNotReceiveWithAnyArgs().GetRegionsAsync(default!, default);
        await _regionUpdateChecker.DidNotReceiveWithAnyArgs().CheckAsync(default!, default!, default);
        await _fileDownloader.DidNotReceiveWithAnyArgs().DownloadAsync(default!, default!, default);
        await _renderRegion.DidNotReceiveWithAnyArgs().RunAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task RunAsync_DoesNotAcquireLock_WhenDryRunIsEnabled()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("runner-dry-run-no-lock", false);
        var sut = CreateSut(tempDir.DirectoryPath, CreateOptions(dryRun: true));

        // Act
        await sut.RunAsync(CancellationToken.None);

        // Assert
        _lockFactory.DidNotReceiveWithAnyArgs().Create(default!);
    }

    [Fact]
    public async Task RunAsync_DisposesTheLock_AfterProcessingCompletes()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("runner-disposes-lock", false);
        var options = CreateOptions();
        var sut = CreateSut(tempDir.DirectoryPath, options);

        var regionsPath = Path.GetFullPath(Path.Combine(tempDir.DirectoryPath, options.RegionsJson));
        TestFiles.WriteAllText(regionsPath, "[]");
        _regionSource.GetRegionsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RegionDto>());

        // Act
        await sut.RunAsync(CancellationToken.None);

        // Assert
        _instanceLock.Received(1).Dispose();
    }

    [Fact]
    public async Task RunAsync_UsesWorkDirDefaultLockFile_WhenLockFilePathIsNotConfigured()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("runner-default-lock-path", false);
        var options = CreateOptions(workDir: "work");
        var sut = CreateSut(tempDir.DirectoryPath, options);

        var regionsPath = Path.GetFullPath(Path.Combine(tempDir.DirectoryPath, options.RegionsJson));
        TestFiles.WriteAllText(regionsPath, "[]");
        _regionSource.GetRegionsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RegionDto>());

        var workDir = Path.GetFullPath(Path.Combine(tempDir.DirectoryPath, options.WorkDir));
        var expectedLockFilePath = Path.Combine(workDir, "poineer-render.lock");

        // Act
        await sut.RunAsync(CancellationToken.None);

        // Assert
        _lockFactory.Received(1).Create(expectedLockFilePath);
    }

    [Fact]
    public async Task RunAsync_ProcessesRegion_WhenPbfExistsButRemoteMetadataChanged()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("runner-render-changed-region", false);
        var options = CreateOptions(overwritePbf: false);
        var sut = CreateSut(tempDir.DirectoryPath, options);

        var regionsPath = Path.GetFullPath(Path.Combine(tempDir.DirectoryPath, options.RegionsJson));
        TestFiles.WriteAllText(regionsPath, "[]");

        var region = BerlinRegion();
        _regionSource.GetRegionsAsync(regionsPath, Arg.Any<CancellationToken>())
            .Returns(new[] { region });

        var workDir = Path.GetFullPath(Path.Combine(tempDir.DirectoryPath, options.WorkDir));
        var pbfPath = Path.Combine(workDir, region.Id, "osm.pbf");
        TestFiles.WriteAllText(pbfPath, "existing pbf");

        // Act
        var result = await sut.RunAsync(CancellationToken.None);

        // Assert
        result.Should().Be(0);
        await _fileDownloader.Received(1).DownloadAsync(region.PbfUrl, pbfPath, Arg.Any<CancellationToken>());
        await _renderRegion.Received(1).RunAsync(
            region,
            workDir,
            Path.GetFullPath(Path.Combine(tempDir.DirectoryPath, options.OutDir)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_ResolvesConfiguredLockFilePath_AgainstContentRoot()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("runner-configured-lock-path", false);
        var options = CreateOptions(lockFilePath: "locks/render.lock");
        var sut = CreateSut(tempDir.DirectoryPath, options);

        var regionsPath = Path.GetFullPath(Path.Combine(tempDir.DirectoryPath, options.RegionsJson));
        TestFiles.WriteAllText(regionsPath, "[]");
        _regionSource.GetRegionsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RegionDto>());

        var expectedLockFilePath = Path.GetFullPath(Path.Combine(tempDir.DirectoryPath, options.LockFilePath!));

        // Act
        await sut.RunAsync(CancellationToken.None);

        // Assert
        _lockFactory.Received(1).Create(expectedLockFilePath);
    }

    private Runner CreateSut(
        string contentRoot,
        RendererOptions options)
        => new(
            new FakeHostEnvironment
            {
                ContentRootPath = contentRoot
            },
            NullLogger<Runner>.Instance,
            _fileDownloader,
            _regionSource,
            _renderRegion,
            _regionUpdateChecker,
            _lockFactory,
            Options.Create(options));

    private static RendererOptions CreateOptions(
        string workDir = "work",
        string outDir = "out",
        string regionsJson = "regions.json",
        bool overwriteDatabase = false,
        bool overwritePbf = false,
        bool dryRun = false,
        string? onlyRegionId = null,
        string? lockFilePath = null)
        => new()
        {
            WorkDir = workDir,
            OutDir = outDir,
            RegionsJson = regionsJson,
            OverwriteDatabase = overwriteDatabase,
            OverwritePbf = overwritePbf,
            DryRun = dryRun,
            OnlyRegionId = onlyRegionId,
            DownloadTimeoutSeconds = 600,
            LockFilePath = lockFilePath
        };

    private static RegionDto BerlinRegion()
        => new(
            Id: "berlin",
            Name: "Berlin",
            PbfUrl: "https://example.com/berlin.osm.pbf",
            Poly: "berlin.poly");

    private static RegionUpdateCheckResult ChangedResult(RegionDto region)
    {
        var metadata = new RegionUpdateMetadata("\"etag\"", DateTimeOffset.Parse("2026-01-01T00:00:00Z"), 1024);
        return new RegionUpdateCheckResult(
            ShouldRedownloadPbf: true,
            Reason: "Remote ETag changed.",
            RemoteMetadata: metadata,
            StoredState: new RegionRenderState(
                region.Id,
                region.PbfUrl,
                new RegionUpdateMetadata("\"old-etag\"", DateTimeOffset.Parse("2025-01-01T00:00:00Z"), 512),
                DateTimeOffset.Parse("2025-01-01T00:00:00Z")));
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "POIneer.Render.UnitTests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
