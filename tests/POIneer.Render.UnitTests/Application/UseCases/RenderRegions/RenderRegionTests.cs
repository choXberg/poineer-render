using Microsoft.Extensions.Logging;
using NSubstitute;
using POIneer.Render.Application.Contracts;
using POIneer.Render.Application.Mapping;
using POIneer.Render.Application.Ports;
using POIneer.Render.Application.Ports.Model;
using POIneer.Render.Application.UseCases;
using POIneer.Render.Domain.Models;
using POIneer.Render.Ports;
using POIneer.Render.TestHelpers;
using Xunit;

namespace POIneer.Render.UnitTests.Application.UseCases.RenderRegions;

public sealed class RenderRegionTests
{
    private readonly ILogger<RenderRegion> _logger =
        Substitute.For<ILogger<RenderRegion>>();

    private readonly IPolygonCutter _polygonCutter = Substitute.For<IPolygonCutter>();
    private readonly IOsmReader _osmReader = Substitute.For<IOsmReader>();
    private readonly ISqliteDatabaseInitializer _dbInit = Substitute.For<ISqliteDatabaseInitializer>();
    private readonly IExporter _exporter = Substitute.For<IExporter>();
    private readonly IDatasetValidator _datasetValidator = Substitute.For<IDatasetValidator>();
    private readonly IDatasetPublisher _datasetPublisher = Substitute.For<IDatasetPublisher>();
    private readonly IDatasetVersionCalculator _datasetVersionCalculator = Substitute.For<IDatasetVersionCalculator>();
    private readonly IDatasetArtifactMetadataFactory _datasetArtifactMetadataFactory = Substitute.For<IDatasetArtifactMetadataFactory>();
    private readonly IPublishedDatasetVerifier _publishedDatasetVerifier = Substitute.For<IPublishedDatasetVerifier>();
    private readonly IVectorTileGenerator _vectorTileGenerator = Substitute.For<IVectorTileGenerator>();


    private RenderRegion CreateSut(
        ILogger<RenderRegion>? logger = null,
        IPolygonCutter? polygonCutter = null,
        ISqliteDatabaseInitializer? dbInit = null,
        IOsmReader? osmReader = null,
        IExporter? exporter = null,
        IRawPoiMapper? mapper = null,
        IDatasetValidator? datasetValidator = null,
        IDatasetPublisher? datasetPublisher = null,
        IDatasetVersionCalculator? datasetVersionCalculator = null,
        IDatasetArtifactMetadataFactory? datasetArtifactMetadataFactory = null,
        IPublishedDatasetVerifier? publishedDatasetVerifier = null,
        IVectorTileGenerator? vectorTileGenerator = null,
        bool overwriteDatabase = false,
        bool overwritePbf = false,
        bool vectorTilesEnabled = false)
    {
        var validator = datasetValidator ?? _datasetValidator;

        validator
            .ValidateAsync(
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new DatasetValidationResult(
                IsValid: true,
                Errors: []));

        var publisher = datasetPublisher ?? _datasetPublisher;

        publisher
            .PublishAsync(
                Arg.Any<DatasetPublishRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new DatasetPublishResult(
                DestinationPath: "test-publish-destination-path",
                WasSkipped: false));

        var versionCalculator = datasetVersionCalculator ?? _datasetVersionCalculator;

        versionCalculator
            .CalculateAsync(
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns("test-version");

        var metadataFactory = datasetArtifactMetadataFactory ?? _datasetArtifactMetadataFactory;

        metadataFactory
            .CreateAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new DatasetArtifactMetadata(
                RegionId: "test-region",
                Version: "test-version",
                FileName: "poi.sqlite",
                FileSizeBytes: 0,
                CreatedUtc: DateTimeOffset.UtcNow,
                Sha256Checksum: "test-checksum"));

        var verifier = publishedDatasetVerifier ?? _publishedDatasetVerifier;

        verifier
            .VerifyAsync(
                Arg.Any<DatasetArtifactMetadata>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new DatasetVerificationResult(
                IsVerified: true,
                Errors: []));

        return new RenderRegion(
            logger ?? _logger,
            polygonCutter ?? _polygonCutter,
            dbInit ?? _dbInit,
            osmReader ?? _osmReader,
            exporter ?? _exporter,
            mapper ?? Substitute.For<IRawPoiMapper>(),
            TestRendererOptions.Create(overwriteDatabase, overwritePbf),
            validator,
            publisher,
            versionCalculator,
            metadataFactory,
            verifier,
            vectorTileGenerator ?? _vectorTileGenerator,
            TestRendererOptions.CreateVectorTiles(
                enabled: vectorTilesEnabled,
                planetilerJarPath: vectorTilesEnabled ? "planetiler.jar" : null));
    }

    [Fact]
    public async Task RunAsync_ThrowsFileNotFoundException_WhenPbfDoesNotExist()
    {
        // Arrange
        var sut = CreateSut();

        var region = new RegionDto(
            Id: "berlin",
            Name: "Berlin",
            PbfUrl: "http://example.com/berlin.osm.pbf",
            Poly: "dummy.poly"
        );
        await using var tempDir = TestTemporaryDirectories.Create("throw-when-pbf-does-not-exist", false);

        tempDir.CreateSubDir("work"); // empty -> no pbf
        var workDir = tempDir.CreateSubDir("work").DirectoryPath; // empty -> no pbf
        var outDir = tempDir.CreateSubDir("out").DirectoryPath;

        // Act
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            sut.RunAsync(region, workDir, outDir, CancellationToken.None));

        // Assert
        Assert.Contains("PBF not found", ex.Message);

        await _polygonCutter.DidNotReceiveWithAnyArgs().CutAsync(default!, default!, default);
        _osmReader.DidNotReceiveWithAnyArgs().ReadAmenityNodesAsync(default!, default);
        await _exporter.DidNotReceiveWithAnyArgs().ExportAsync(default!, default!, default);
    }

    [Fact]
    public async Task RunAsync_SkipsRendering_WhenOutputDatabaseAlreadyExists_AndOverwriteDisabled()
    {
        // Arrange
        var sut = CreateSut();

        var region = new RegionDto(
            Id: "berlin",
            Name: "Berlin",
            PbfUrl: "http://example.com/berlin.osm.pbf",
            Poly: "berlin.poly"
        );

        await using var tempDir =
            TestTemporaryDirectories.Create(
                "skips-rendering-when-output-exists",
                false);

        var workDir = tempDir.CreateSubDir("work").DirectoryPath;
        var outDir = tempDir.CreateSubDir("out").DirectoryPath;

        var pbfPath = Path.Combine(workDir, region.Id, "osm.pbf");
        TestFiles.WriteAllText(pbfPath, "dummy");

        var existingOutputPath =
            Path.Combine(outDir, region.Id, "poi.sqlite");

        TestFiles.WriteAllText(existingOutputPath, "existing");

        // Act
        await sut.RunAsync(
            region,
            workDir,
            outDir,
            CancellationToken.None);

        // Assert
        await _polygonCutter
            .DidNotReceiveWithAnyArgs()
            .CutAsync(default!, default!, default);

        _osmReader
            .DidNotReceiveWithAnyArgs()
            .ReadAmenityNodesAsync(default!, default);

        await _dbInit
            .DidNotReceiveWithAnyArgs()
            .InitializeAsync(default!, default);

        await _exporter
            .DidNotReceiveWithAnyArgs()
            .ExportAsync(default!, default!, default);

        await _datasetValidator
            .DidNotReceiveWithAnyArgs()
            .ValidateAsync(default!, default);

        await _datasetPublisher
            .DidNotReceiveWithAnyArgs()
            .PublishAsync(default!, default);

        await _datasetVersionCalculator
            .DidNotReceiveWithAnyArgs()
            .CalculateAsync(default!, default);

        await _datasetArtifactMetadataFactory
            .DidNotReceiveWithAnyArgs()
            .CreateAsync(default!, default!, default!, default);

        await _publishedDatasetVerifier
            .DidNotReceiveWithAnyArgs()
            .VerifyAsync(default!, default!, default);
    }

    [Fact]
    public async Task RunAsync_ReRenders_WhenPbfIsNewerThanExistingOutput_AndOverwriteDisabled()
    {
        // Arrange
        var sut = CreateSut();

        var region = new RegionDto(
            Id: "berlin",
            Name: "Berlin",
            PbfUrl: "http://example.com/berlin.osm.pbf",
            Poly: "berlin.poly");

        await using var tempDir =
            TestTemporaryDirectories.Create(
                "re-renders-when-pbf-is-newer",
                false);

        var workDir = tempDir.CreateSubDir("work").DirectoryPath;
        var outDir = tempDir.CreateSubDir("out").DirectoryPath;

        var pbfPath = Path.Combine(workDir, region.Id, "osm.pbf");
        TestFiles.WriteAllText(pbfPath, "new pbf bytes");

        var existingOutputPath = Path.Combine(outDir, region.Id, "poi.sqlite");
        TestFiles.WriteAllText(existingOutputPath, "existing sqlite bytes");

        File.SetLastWriteTimeUtc(existingOutputPath, DateTime.UtcNow.AddMinutes(-5));
        File.SetLastWriteTimeUtc(pbfPath, DateTime.UtcNow);

        var cutPbfPath = Path.Combine(workDir, region.Id, "cut.osm.pbf");
        _polygonCutter
            .CutAsync(pbfPath, region.Poly, Arg.Any<CancellationToken>())
            .Returns(cutPbfPath);

        _osmReader
            .ReadAmenityNodesAsync(cutPbfPath, Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<RawPoi>());

        _exporter
            .ExportAsync(
                Arg.Any<IAsyncEnumerable<Poi>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                TestFiles.WriteAllText(callInfo.ArgAt<string>(1), "new sqlite bytes");
                return Task.CompletedTask;
            });

        // Act
        await sut.RunAsync(region, workDir, outDir, CancellationToken.None);

        // Assert
        await _polygonCutter
            .Received(1)
            .CutAsync(pbfPath, region.Poly, Arg.Any<CancellationToken>());

        Assert.Equal("new sqlite bytes", File.ReadAllText(existingOutputPath));
    }

    [Fact]
    public async Task RunAsync_CallsPorts_WithExpectedPaths_AndInOrder()
    {
        // Arrange
        var sut = CreateSut();

        var region = new RegionDto(
            Id: "berlin",
            Name: "Berlin",
            PbfUrl: "http://example.com/berlin.osm.pbf",
            Poly: "berlin.poly"
        );

        await using var tempDir = TestTemporaryDirectories.Create("calls-ports-with-expected-paths", false);
        var workDir = tempDir.CreateSubDir("work").DirectoryPath;
        var outDir = tempDir.CreateSubDir("out").DirectoryPath;

        // create input pbf file
        var pbfPath = Path.Combine(workDir, region.Id, "osm.pbf");

        TestFiles.WriteAllText(pbfPath, "dummy");

        var cutPbfPath = Path.Combine(workDir, region.Id, $"cut.osm.pbf");
        _polygonCutter
            .CutAsync(pbfPath, region.Poly, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(cutPbfPath));

        var rawPois = AsyncEnumerable.Empty<RawPoi>();
        _osmReader
            .ReadAmenityNodesAsync(cutPbfPath, Arg.Any<CancellationToken>())
            .Returns(rawPois);

        // The real exporter would create the staging file on disk; simulate that so the
        // post-validation promotion to the canonical path has something to move.
        _exporter
            .ExportAsync(
                Arg.Any<IAsyncEnumerable<Poi>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                TestFiles.WriteAllText(callInfo.ArgAt<string>(1), "dummy sqlite bytes");
                return Task.CompletedTask;
            });

        // Act
        await sut.RunAsync(region, workDir, outDir, CancellationToken.None);

        // Assert: dbInit/exporter operate on the staging path, not the canonical one
        var expectedCanonicalPath = Path.Combine(outDir, region.Id, "poi.sqlite");
        var expectedStagingPath = expectedCanonicalPath + ".tmp";

        Received.InOrder(() =>
        {
            _polygonCutter.CutAsync(pbfPath, region.Poly, Arg.Any<CancellationToken>());
            _osmReader.ReadAmenityNodesAsync(cutPbfPath, Arg.Any<CancellationToken>());
            _dbInit.InitializeAsync(expectedStagingPath, Arg.Any<CancellationToken>());
            _exporter.ExportAsync(
                Arg.Any<IAsyncEnumerable<Poi>>(),
                expectedStagingPath,
                Arg.Any<CancellationToken>());
            _datasetVersionCalculator.CalculateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
            _datasetArtifactMetadataFactory.CreateAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            _datasetPublisher.PublishAsync(Arg.Any<DatasetPublishRequest>(), Arg.Any<CancellationToken>());
            _publishedDatasetVerifier.VerifyAsync(
                Arg.Any<DatasetArtifactMetadata>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        });

        // Once validation passes, the staging file is promoted to the canonical path.
        Assert.True(File.Exists(expectedCanonicalPath));
        Assert.False(File.Exists(expectedStagingPath));
    }

    [Fact]
    public async Task RunAsync_GeneratesVectorTilesFromTheCutPbf_WhenVectorTilesAreEnabled()
    {
        // Arrange
        var vectorTileGenerator = Substitute.For<IVectorTileGenerator>();
        var sut = CreateSut(
            vectorTileGenerator: vectorTileGenerator,
            vectorTilesEnabled: true);

        var region = new RegionDto(
            Id: "berlin",
            Name: "Berlin",
            PbfUrl: "http://example.com/berlin.osm.pbf",
            Poly: "berlin.poly");

        await using var tempDir = TestTemporaryDirectories.Create("generates-vector-tiles", false);
        var workDir = tempDir.CreateSubDir("work").DirectoryPath;
        var outDir = tempDir.CreateSubDir("out").DirectoryPath;

        var pbfPath = Path.Combine(workDir, region.Id, "osm.pbf");
        TestFiles.WriteAllText(pbfPath, "dummy");

        using var cts = new CancellationTokenSource();

        var cutPbfPath = Path.Combine(workDir, region.Id, "cut.osm.pbf");
        _polygonCutter
            .CutAsync(pbfPath, region.Poly, cts.Token)
            .Returns(cutPbfPath);

        _osmReader
            .ReadAmenityNodesAsync(cutPbfPath, cts.Token)
            .Returns(AsyncEnumerable.Empty<RawPoi>());

        _exporter
            .ExportAsync(
                Arg.Any<IAsyncEnumerable<Poi>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                TestFiles.WriteAllText(callInfo.ArgAt<string>(1), "dummy sqlite bytes");
                return Task.CompletedTask;
            });

        vectorTileGenerator
            .GenerateAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                TestFiles.WriteAllText(callInfo.ArgAt<string>(1), "dummy pmtiles bytes");
                return Task.CompletedTask;
            });

        // Act
        await sut.RunAsync(region, workDir, outDir, cts.Token);

        // Assert
        var expectedCanonicalMapPath = Path.Combine(outDir, region.Id, "map.pmtiles");
        var expectedStagingMapPath = Path.Combine(outDir, region.Id, "map.tmp.pmtiles");

        await vectorTileGenerator
            .Received(1)
            .GenerateAsync(cutPbfPath, expectedStagingMapPath, cts.Token);

        Assert.True(File.Exists(expectedCanonicalMapPath));
        Assert.False(File.Exists(expectedStagingMapPath));
    }

    [Fact]
    public async Task RunAsync_DoesNotPromoteOrPublish_WhenVectorTileGenerationFails()
    {
        // Arrange
        var vectorTileGenerator = Substitute.For<IVectorTileGenerator>();
        var sut = CreateSut(
            vectorTileGenerator: vectorTileGenerator,
            vectorTilesEnabled: true);

        var region = new RegionDto(
            Id: "berlin",
            Name: "Berlin",
            PbfUrl: "http://example.com/berlin.osm.pbf",
            Poly: "berlin.poly");

        await using var tempDir = TestTemporaryDirectories.Create("fails-when-vector-tiles-fail", false);
        var workDir = tempDir.CreateSubDir("work").DirectoryPath;
        var outDir = tempDir.CreateSubDir("out").DirectoryPath;

        var pbfPath = Path.Combine(workDir, region.Id, "osm.pbf");
        TestFiles.WriteAllText(pbfPath, "dummy");

        var cutPbfPath = Path.Combine(workDir, region.Id, "cut.osm.pbf");
        _polygonCutter
            .CutAsync(pbfPath, region.Poly, Arg.Any<CancellationToken>())
            .Returns(cutPbfPath);

        _osmReader
            .ReadAmenityNodesAsync(cutPbfPath, Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<RawPoi>());

        _exporter
            .ExportAsync(
                Arg.Any<IAsyncEnumerable<Poi>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                TestFiles.WriteAllText(callInfo.ArgAt<string>(1), "dummy sqlite bytes");
                return Task.CompletedTask;
            });

        vectorTileGenerator
            .GenerateAsync(
                cutPbfPath,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Planetiler failed"));

        // Act
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.RunAsync(region, workDir, outDir, CancellationToken.None));

        // Assert
        Assert.Contains("Planetiler failed", ex.Message);

        Assert.False(File.Exists(Path.Combine(outDir, region.Id, "poi.sqlite")));
        Assert.False(File.Exists(Path.Combine(outDir, region.Id, "map.pmtiles")));

        await _datasetPublisher
            .DidNotReceiveWithAnyArgs()
            .PublishAsync(default!, default);
    }

    [Fact]
    public async Task RunAsync_ReRendersWhenVectorTilesAreEnabled_AndOnlySqliteAlreadyExists()
    {
        // Arrange
        var vectorTileGenerator = Substitute.For<IVectorTileGenerator>();
        var sut = CreateSut(
            vectorTileGenerator: vectorTileGenerator,
            vectorTilesEnabled: true);

        var region = new RegionDto(
            Id: "berlin",
            Name: "Berlin",
            PbfUrl: "http://example.com/berlin.osm.pbf",
            Poly: "berlin.poly");

        await using var tempDir = TestTemporaryDirectories.Create("renders-missing-vector-tiles", false);
        var workDir = tempDir.CreateSubDir("work").DirectoryPath;
        var outDir = tempDir.CreateSubDir("out").DirectoryPath;

        var pbfPath = Path.Combine(workDir, region.Id, "osm.pbf");
        TestFiles.WriteAllText(pbfPath, "dummy");

        var existingSqlitePath = Path.Combine(outDir, region.Id, "poi.sqlite");
        TestFiles.WriteAllText(existingSqlitePath, "existing sqlite bytes");

        _osmReader
            .ReadAmenityNodesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<RawPoi>());

        _exporter
            .ExportAsync(
                Arg.Any<IAsyncEnumerable<Poi>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                TestFiles.WriteAllText(callInfo.ArgAt<string>(1), "new sqlite bytes");
                return Task.CompletedTask;
            });

        vectorTileGenerator
            .GenerateAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                TestFiles.WriteAllText(callInfo.ArgAt<string>(1), "dummy pmtiles bytes");
                return Task.CompletedTask;
            });

        // Act
        await sut.RunAsync(region, workDir, outDir, CancellationToken.None);

        // Assert
        await vectorTileGenerator
            .Received(1)
            .GenerateAsync(Arg.Any<string>(), Path.Combine(outDir, region.Id, "map.tmp.pmtiles"), Arg.Any<CancellationToken>());

        Assert.Equal("new sqlite bytes", File.ReadAllText(existingSqlitePath));
        Assert.True(File.Exists(Path.Combine(outDir, region.Id, "map.pmtiles")));
    }

    [Fact]
    public async Task RunAsync_ForwardsCancellationToken_ToAllPorts()
    {
        // Arrange
        var sut = CreateSut();

        var region = new RegionDto(
            Id: "berlin",
            Name: "Berlin",
            PbfUrl: "http://example.com/berlin.osm.pbf",
            Poly: "berlin.poly"
        );

        await using var tempDir = TestTemporaryDirectories.Create("forwards-cancellation-token", false);
        var workDir = tempDir.CreateSubDir("work").DirectoryPath;
        var outDir = tempDir.CreateSubDir("out").DirectoryPath;

        var pbfPath = Path.Combine(workDir, region.Id, "osm.pbf");

        TestFiles.WriteAllText(pbfPath, "dummy");

        var cutPbfPath = Path.Combine(workDir, region.Id, "cut.osm.pbf");

        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        _polygonCutter.CutAsync(pbfPath, region.Poly, ct)
            .Returns(Task.FromResult(cutPbfPath));

        var pois = AsyncEnumerable.Empty<RawPoi>();
        _osmReader.ReadAmenityNodesAsync(cutPbfPath, ct).Returns(pois);

        _exporter
            .ExportAsync(
                Arg.Any<IAsyncEnumerable<Poi>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                TestFiles.WriteAllText(callInfo.ArgAt<string>(1), "dummy sqlite bytes");
                return Task.CompletedTask;
            });

        // Act
        await sut.RunAsync(region, workDir, outDir, ct);

        // Assert
        await _polygonCutter.Received(1).CutAsync(pbfPath, region.Poly, ct);
        _osmReader.Received(1).ReadAmenityNodesAsync(cutPbfPath, ct);

        var expectedStagingPath = Path.Combine(outDir, region.Id, "poi.sqlite.tmp");
        await _exporter.Received(1).ExportAsync(
            Arg.Any<IAsyncEnumerable<Poi>>(),
            expectedStagingPath,
            ct);

        await _datasetPublisher.Received(1).PublishAsync(Arg.Any<DatasetPublishRequest>(), ct);
        await _datasetVersionCalculator.Received(1).CalculateAsync(Arg.Any<string>(), ct);
        await _datasetArtifactMetadataFactory.Received(1).CreateAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), ct);
        await _publishedDatasetVerifier.Received(1).VerifyAsync(
            Arg.Any<DatasetArtifactMetadata>(), Arg.Any<string>(), ct);
    }

    [Fact]
    public async Task RunAsync_HappyPath_CutsReadsAndExports_WithExpectedPaths()
    {
        // Arrange
        var logger = Substitute.For<ILogger<RenderRegion>>();
        var polygonCutter = Substitute.For<IPolygonCutter>();
        var osmReader = Substitute.For<IOsmReader>();
        var initDb = Substitute.For<ISqliteDatabaseInitializer>();
        var exporter = Substitute.For<IExporter>();

        var sut = CreateSut(
            logger: logger,
            polygonCutter: polygonCutter,
            dbInit: initDb,
            osmReader: osmReader,
            exporter: exporter);

        var region = new RegionDto(
            Id: "berlin",
            Name: "Berlin",
            PbfUrl: "https://download.geofabrik.de/europe/germany/berlin-latest.osm.pbf",
            Poly: "berlin.poly");

        await using var tempDir = TestTemporaryDirectories.Create("calls-ports-with-expected-paths", false);
        var workDir = tempDir.CreateSubDir("work").DirectoryPath;
        var outDir = tempDir.CreateSubDir("out").DirectoryPath;

        // Dummy input PBF (existence is what matters)
        var pbfPath = Path.Combine(workDir, region.Id, "osm.pbf");
        TestFiles.WriteAllText(pbfPath, "dummy");

        // Cut result
        var cutPbfPath = Path.Combine(workDir, region.Id, "cut.osm.pbf");
        polygonCutter
            .CutAsync(pbfPath, region.Poly, Arg.Any<CancellationToken>())
            .Returns(cutPbfPath);

        // Reader returns some POIs
        var pois = new[]
        {
            new RawPoi(
                OsmId: 1,
                Latitude: 52.5,
                Longitude: 13.4,
                Amenity: "cafe",
                Name: "Cafe A",
                Tags: new Dictionary<string, string> { { "amenity", "cafe" }, { "name", "Cafe A" } }),
            new RawPoi(
                OsmId: 2,
                Latitude: 52.6,
                Longitude: 13.5,
                Amenity: "restaurant",
                Name: "Restaurant B",
                Tags: new Dictionary<string, string> { { "amenity", "restaurant" }, { "name", "Restaurant B" } })
        }.ToAsyncEnumerable();

        osmReader
            .ReadAmenityNodesAsync(cutPbfPath, Arg.Any<CancellationToken>())
            .Returns(pois);

        exporter
            .ExportAsync(
                Arg.Any<IAsyncEnumerable<Poi>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                TestFiles.WriteAllText(callInfo.ArgAt<string>(1), "dummy sqlite bytes");
                return Task.CompletedTask;
            });

        // Act
        await sut.RunAsync(region, workDir, outDir, CancellationToken.None);

        // Assert
        var expectedCanonicalPath = Path.Combine(outDir, region.Id, "poi.sqlite");
        var expectedStagingPath = expectedCanonicalPath + ".tmp";

        await exporter.Received(1).ExportAsync(
            Arg.Any<IAsyncEnumerable<Poi>>(),
            expectedStagingPath,
            Arg.Any<CancellationToken>());

        Received.InOrder(() =>
        {
            polygonCutter.CutAsync(pbfPath, region.Poly, Arg.Any<CancellationToken>());
            osmReader.ReadAmenityNodesAsync(cutPbfPath, Arg.Any<CancellationToken>());
            exporter.ExportAsync(
                Arg.Any<IAsyncEnumerable<Poi>>(),
                expectedStagingPath,
                Arg.Any<CancellationToken>());
        });

        Assert.True(File.Exists(expectedCanonicalPath));
        Assert.False(File.Exists(expectedStagingPath));
    }

    [Fact]
    public async Task RunAsync_PublishesTheCanonicalDataset_AfterSuccessfulRender()
    {
        // Arrange
        var sut = CreateSut();

        var region = new RegionDto(
            Id: "berlin",
            Name: "Berlin",
            PbfUrl: "http://example.com/berlin.osm.pbf",
            Poly: "berlin.poly");

        await using var tempDir = TestTemporaryDirectories.Create("publishes-canonical-dataset", false);
        var workDir = tempDir.CreateSubDir("work").DirectoryPath;
        var outDir = tempDir.CreateSubDir("out").DirectoryPath;

        var pbfPath = Path.Combine(workDir, region.Id, "osm.pbf");
        TestFiles.WriteAllText(pbfPath, "dummy");

        _osmReader
            .ReadAmenityNodesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<RawPoi>());

        _exporter
            .ExportAsync(
                Arg.Any<IAsyncEnumerable<Poi>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                TestFiles.WriteAllText(callInfo.ArgAt<string>(1), "dummy sqlite bytes");
                return Task.CompletedTask;
            });

        // Act
        await sut.RunAsync(region, workDir, outDir, CancellationToken.None);

        // Assert
        var expectedCanonicalPath = Path.Combine(outDir, region.Id, "poi.sqlite");

        await _datasetPublisher
            .Received(1)
            .PublishAsync(
                Arg.Is<DatasetPublishRequest>(r =>
                    r.RegionId == region.Id
                    && r.SourcePath == expectedCanonicalPath
                    && !string.IsNullOrWhiteSpace(r.Version)),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_GeneratesArtifactMetadata_ForTheCanonicalDataset_UsingTheCalculatedVersion()
    {
        // Arrange: metadata (issue #130) must describe the promoted canonical artifact,
        // not the staging file, and must reuse the same version computed for publishing.
        var versionCalculator = Substitute.For<IDatasetVersionCalculator>();
        var metadataFactory = Substitute.For<IDatasetArtifactMetadataFactory>();
        var sut = CreateSut(
            datasetVersionCalculator: versionCalculator,
            datasetArtifactMetadataFactory: metadataFactory);

        versionCalculator
            .CalculateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("1-abc123");

        var region = new RegionDto(
            Id: "berlin",
            Name: "Berlin",
            PbfUrl: "http://example.com/berlin.osm.pbf",
            Poly: "berlin.poly");

        await using var tempDir = TestTemporaryDirectories.Create("generates-artifact-metadata", false);
        var workDir = tempDir.CreateSubDir("work").DirectoryPath;
        var outDir = tempDir.CreateSubDir("out").DirectoryPath;

        var pbfPath = Path.Combine(workDir, region.Id, "osm.pbf");
        TestFiles.WriteAllText(pbfPath, "dummy");

        _osmReader
            .ReadAmenityNodesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<RawPoi>());

        _exporter
            .ExportAsync(
                Arg.Any<IAsyncEnumerable<Poi>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                TestFiles.WriteAllText(callInfo.ArgAt<string>(1), "dummy sqlite bytes");
                return Task.CompletedTask;
            });

        // Act
        await sut.RunAsync(region, workDir, outDir, CancellationToken.None);

        // Assert
        var expectedCanonicalPath = Path.Combine(outDir, region.Id, "poi.sqlite");

        await metadataFactory
            .Received(1)
            .CreateAsync(
                region.Id,
                "1-abc123",
                expectedCanonicalPath,
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_CalculatesVersion_FromTheCutPbf_AndUsesItForPublishing()
    {
        // Arrange: the version calculator is meant to hash the actual data that was
        // rendered (the cut PBF), not the raw download - a poly-file change can alter what
        // gets rendered even when the raw PBF download did not change.
        var polygonCutter = Substitute.For<IPolygonCutter>();
        var versionCalculator = Substitute.For<IDatasetVersionCalculator>();
        var sut = CreateSut(polygonCutter: polygonCutter, datasetVersionCalculator: versionCalculator);

        var region = new RegionDto(
            Id: "berlin",
            Name: "Berlin",
            PbfUrl: "http://example.com/berlin.osm.pbf",
            Poly: "berlin.poly");

        await using var tempDir = TestTemporaryDirectories.Create("calculates-version-from-cut-pbf", false);
        var workDir = tempDir.CreateSubDir("work").DirectoryPath;
        var outDir = tempDir.CreateSubDir("out").DirectoryPath;

        var pbfPath = Path.Combine(workDir, region.Id, "osm.pbf");
        TestFiles.WriteAllText(pbfPath, "dummy");

        var cutPbfPath = Path.Combine(workDir, region.Id, "cut.osm.pbf");
        polygonCutter
            .CutAsync(pbfPath, region.Poly, Arg.Any<CancellationToken>())
            .Returns(cutPbfPath);

        versionCalculator
            .CalculateAsync(cutPbfPath, Arg.Any<CancellationToken>())
            .Returns("1-abc123");

        _osmReader
            .ReadAmenityNodesAsync(cutPbfPath, Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<RawPoi>());

        _exporter
            .ExportAsync(
                Arg.Any<IAsyncEnumerable<Poi>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                TestFiles.WriteAllText(callInfo.ArgAt<string>(1), "dummy sqlite bytes");
                return Task.CompletedTask;
            });

        // Act
        await sut.RunAsync(region, workDir, outDir, CancellationToken.None);

        // Assert
        await versionCalculator.Received(1).CalculateAsync(cutPbfPath, Arg.Any<CancellationToken>());

        await _datasetPublisher
            .Received(1)
            .PublishAsync(
                Arg.Is<DatasetPublishRequest>(r => r.Version == "1-abc123"),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_DoesNotPublish_WhenValidationFails()
    {
        // Arrange
        var validator = Substitute.For<IDatasetValidator>();

        var sut = CreateSut(datasetValidator: validator);

        validator
            .ValidateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DatasetValidationResult(
                IsValid: false,
                Errors: ["Required table 'poi' is missing."]));

        var region = new RegionDto(
            Id: "berlin",
            Name: "Berlin",
            PbfUrl: "http://example.com/berlin.osm.pbf",
            Poly: "berlin.poly");

        await using var tempDir = TestTemporaryDirectories.Create("does-not-publish-when-validation-fails", false);
        var workDir = tempDir.CreateSubDir("work").DirectoryPath;
        var outDir = tempDir.CreateSubDir("out").DirectoryPath;

        var pbfPath = Path.Combine(workDir, region.Id, "osm.pbf");
        TestFiles.WriteAllText(pbfPath, "dummy");

        _osmReader
            .ReadAmenityNodesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<RawPoi>());

        _exporter
            .ExportAsync(
                Arg.Any<IAsyncEnumerable<Poi>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                TestFiles.WriteAllText(callInfo.ArgAt<string>(1), "dummy sqlite bytes");
                return Task.CompletedTask;
            });

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.RunAsync(region, workDir, outDir, CancellationToken.None));

        // Assert
        await _datasetPublisher
            .DidNotReceiveWithAnyArgs()
            .PublishAsync(default!, default);

        await _datasetVersionCalculator
            .DidNotReceiveWithAnyArgs()
            .CalculateAsync(default!, default);

        await _datasetArtifactMetadataFactory
            .DidNotReceiveWithAnyArgs()
            .CreateAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task RunAsync_PropagatesException_WhenPublishFails()
    {
        // Arrange: the render itself succeeds and the dataset is promoted to canonical -
        // only the external publish step fails.
        var publisher = Substitute.For<IDatasetPublisher>();
        var sut = CreateSut(datasetPublisher: publisher);

        publisher
            .PublishAsync(Arg.Any<DatasetPublishRequest>(), Arg.Any<CancellationToken>())
            .Returns<DatasetPublishResult>(_ => throw new IOException("Destination filesystem is unavailable."));

        var region = new RegionDto(
            Id: "berlin",
            Name: "Berlin",
            PbfUrl: "http://example.com/berlin.osm.pbf",
            Poly: "berlin.poly");

        await using var tempDir = TestTemporaryDirectories.Create("propagates-exception-when-publish-fails", false);
        var workDir = tempDir.CreateSubDir("work").DirectoryPath;
        var outDir = tempDir.CreateSubDir("out").DirectoryPath;

        var pbfPath = Path.Combine(workDir, region.Id, "osm.pbf");
        TestFiles.WriteAllText(pbfPath, "dummy");

        _osmReader
            .ReadAmenityNodesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<RawPoi>());

        _exporter
            .ExportAsync(
                Arg.Any<IAsyncEnumerable<Poi>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                TestFiles.WriteAllText(callInfo.ArgAt<string>(1), "dummy sqlite bytes");
                return Task.CompletedTask;
            });

        // Act
        await Assert.ThrowsAsync<IOException>(() =>
            sut.RunAsync(region, workDir, outDir, CancellationToken.None));

        // Assert: rendering already succeeded and promoted the dataset before publishing
        // failed, so the canonical file is still in place - only publishing itself failed.
        var expectedCanonicalPath = Path.Combine(outDir, region.Id, "poi.sqlite");
        Assert.True(File.Exists(expectedCanonicalPath));

        // A publish that never completed successfully has nothing to verify.
        await _publishedDatasetVerifier
            .DidNotReceiveWithAnyArgs()
            .VerifyAsync(default!, default!, default);
    }

    [Fact]
    public async Task RunAsync_VerifiesThePublishedDataset_WithTheArtifactMetadataAndDestinationPath()
    {
        // Arrange: verification (issue #135) must compare the metadata already computed for
        // the canonical artifact against what PublishAsync reports as the actual publish
        // destination - not, say, the canonical path itself, since the destination is a
        // separately configured location (see PublisherOptions).
        var publisher = Substitute.For<IDatasetPublisher>();
        var metadataFactory = Substitute.For<IDatasetArtifactMetadataFactory>();
        var verifier = Substitute.For<IPublishedDatasetVerifier>();
        var sut = CreateSut(
            datasetPublisher: publisher,
            datasetArtifactMetadataFactory: metadataFactory,
            publishedDatasetVerifier: verifier);

        var expectedMetadata = new DatasetArtifactMetadata(
            RegionId: "berlin",
            Version: "1-abc123",
            FileName: "poi.sqlite",
            FileSizeBytes: 42,
            CreatedUtc: DateTimeOffset.UtcNow,
            Sha256Checksum: "abc123checksum");

        metadataFactory
            .CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(expectedMetadata);

        publisher
            .PublishAsync(Arg.Any<DatasetPublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DatasetPublishResult(
                DestinationPath: "test-publish-destination-path",
                WasSkipped: false));

        var region = new RegionDto(
            Id: "berlin",
            Name: "Berlin",
            PbfUrl: "http://example.com/berlin.osm.pbf",
            Poly: "berlin.poly");

        await using var tempDir = TestTemporaryDirectories.Create("verifies-published-dataset", false);
        var workDir = tempDir.CreateSubDir("work").DirectoryPath;
        var outDir = tempDir.CreateSubDir("out").DirectoryPath;

        var pbfPath = Path.Combine(workDir, region.Id, "osm.pbf");
        TestFiles.WriteAllText(pbfPath, "dummy");

        _osmReader
            .ReadAmenityNodesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<RawPoi>());

        _exporter
            .ExportAsync(
                Arg.Any<IAsyncEnumerable<Poi>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                TestFiles.WriteAllText(callInfo.ArgAt<string>(1), "dummy sqlite bytes");
                return Task.CompletedTask;
            });

        // Act
        await sut.RunAsync(region, workDir, outDir, CancellationToken.None);

        // Assert
        await verifier
            .Received(1)
            .VerifyAsync(expectedMetadata, "test-publish-destination-path", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_ThrowsAndFailsTheRegion_WhenPublishedDatasetVerificationFails()
    {
        // Arrange: a publish call that returns successfully is not enough - the render must
        // still fail if what actually landed at the destination doesn't match (issue #135).
        var verifier = Substitute.For<IPublishedDatasetVerifier>();
        var sut = CreateSut(publishedDatasetVerifier: verifier);

        var verificationErrors = new[] { "Checksum mismatch at 'test-publish-destination-path': expected abc, found def." };

        verifier
            .VerifyAsync(Arg.Any<DatasetArtifactMetadata>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DatasetVerificationResult(IsVerified: false, Errors: verificationErrors));

        var region = new RegionDto(
            Id: "berlin",
            Name: "Berlin",
            PbfUrl: "http://example.com/berlin.osm.pbf",
            Poly: "berlin.poly");

        await using var tempDir = TestTemporaryDirectories.Create("throws-when-verification-fails", false);
        var workDir = tempDir.CreateSubDir("work").DirectoryPath;
        var outDir = tempDir.CreateSubDir("out").DirectoryPath;

        var pbfPath = Path.Combine(workDir, region.Id, "osm.pbf");
        TestFiles.WriteAllText(pbfPath, "dummy");

        _osmReader
            .ReadAmenityNodesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<RawPoi>());

        _exporter
            .ExportAsync(
                Arg.Any<IAsyncEnumerable<Poi>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                TestFiles.WriteAllText(callInfo.ArgAt<string>(1), "dummy sqlite bytes");
                return Task.CompletedTask;
            });

        // Act
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.RunAsync(region, workDir, outDir, CancellationToken.None));

        // Assert
        Assert.Contains("failed integrity verification", ex.Message);
        Assert.Contains(verificationErrors[0], ex.Message);

        // The canonical dataset itself is still valid and promoted - only the published
        // copy failed verification - so it must not be quarantined or removed.
        var expectedCanonicalPath = Path.Combine(outDir, region.Id, "poi.sqlite");
        Assert.True(File.Exists(expectedCanonicalPath));
    }

    [Fact]
    public async Task RunAsync_VerifiesEvenWhenPublishWasSkipped()
    {
        // Arrange: WasSkipped only means this run didn't need to copy new bytes - it does
        // not mean the destination was already verified by this feature, so verification
        // must still run (issue #135).
        var publisher = Substitute.For<IDatasetPublisher>();
        var verifier = Substitute.For<IPublishedDatasetVerifier>();
        var sut = CreateSut(datasetPublisher: publisher, publishedDatasetVerifier: verifier);

        publisher
            .PublishAsync(Arg.Any<DatasetPublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DatasetPublishResult(
                DestinationPath: "test-publish-destination-path",
                WasSkipped: true));

        var region = new RegionDto(
            Id: "berlin",
            Name: "Berlin",
            PbfUrl: "http://example.com/berlin.osm.pbf",
            Poly: "berlin.poly");

        await using var tempDir = TestTemporaryDirectories.Create("verifies-even-when-skipped", false);
        var workDir = tempDir.CreateSubDir("work").DirectoryPath;
        var outDir = tempDir.CreateSubDir("out").DirectoryPath;

        var pbfPath = Path.Combine(workDir, region.Id, "osm.pbf");
        TestFiles.WriteAllText(pbfPath, "dummy");

        _osmReader
            .ReadAmenityNodesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<RawPoi>());

        _exporter
            .ExportAsync(
                Arg.Any<IAsyncEnumerable<Poi>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                TestFiles.WriteAllText(callInfo.ArgAt<string>(1), "dummy sqlite bytes");
                return Task.CompletedTask;
            });

        // Act
        await sut.RunAsync(region, workDir, outDir, CancellationToken.None);

        // Assert
        await verifier
            .Received(1)
            .VerifyAsync(Arg.Any<DatasetArtifactMetadata>(), "test-publish-destination-path", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_QuarantinesInvalidDataset_AndThrows_WhenValidationFails()
    {
        // Arrange
        var validator = Substitute.For<IDatasetValidator>();
        var validationErrors = new[] { "Required table 'poi' is missing." };

        var sut = CreateSut(datasetValidator: validator);

        // CreateSut wires the validator to report a valid dataset by default; override
        // that here to exercise the failure path.
        validator
            .ValidateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DatasetValidationResult(IsValid: false, Errors: validationErrors));

        var region = new RegionDto(
            Id: "berlin",
            Name: "Berlin",
            PbfUrl: "http://example.com/berlin.osm.pbf",
            Poly: "berlin.poly");

        await using var tempDir = TestTemporaryDirectories.Create("quarantines-invalid-dataset", false);
        var workDir = tempDir.CreateSubDir("work").DirectoryPath;
        var outDir = tempDir.CreateSubDir("out").DirectoryPath;

        var pbfPath = Path.Combine(workDir, region.Id, "osm.pbf");
        TestFiles.WriteAllText(pbfPath, "dummy");

        _osmReader
            .ReadAmenityNodesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<RawPoi>());

        // The real exporter would create the output file; simulate that so quarantine
        // has an actual file to move.
        _exporter
            .ExportAsync(
                Arg.Any<IAsyncEnumerable<Poi>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                TestFiles.WriteAllText(callInfo.ArgAt<string>(1), "dummy sqlite bytes");
                return Task.CompletedTask;
            });

        // Act
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.RunAsync(region, workDir, outDir, CancellationToken.None));

        // Assert
        Assert.Contains("invalid", ex.Message);
        Assert.Contains("quarantined", ex.Message);

        var canonicalOutputPath = Path.Combine(outDir, region.Id, "poi.sqlite");
        Assert.False(File.Exists(canonicalOutputPath));

        var quarantineDir = Path.Combine(outDir, region.Id, "_failed");
        Assert.True(Directory.Exists(quarantineDir));
        Assert.Single(Directory.GetFiles(quarantineDir, "poi.*.sqlite"));
    }

    [Fact]
    public async Task RunAsync_KeepsPreviousCanonicalDataset_WhenReRenderValidationFails()
    {
        // Arrange: a previous, valid render is already published at the canonical path.
        var validator = Substitute.For<IDatasetValidator>();
        var sut = CreateSut(datasetValidator: validator, overwriteDatabase: true);

        validator
            .ValidateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DatasetValidationResult(
                IsValid: false,
                Errors: ["Required table 'poi' is missing."]));

        var region = new RegionDto(
            Id: "berlin",
            Name: "Berlin",
            PbfUrl: "http://example.com/berlin.osm.pbf",
            Poly: "berlin.poly");

        await using var tempDir = TestTemporaryDirectories.Create("keeps-previous-dataset-on-failed-revalidation", false);
        var workDir = tempDir.CreateSubDir("work").DirectoryPath;
        var outDir = tempDir.CreateSubDir("out").DirectoryPath;

        var pbfPath = Path.Combine(workDir, region.Id, "osm.pbf");
        TestFiles.WriteAllText(pbfPath, "dummy");

        var canonicalPath = Path.Combine(outDir, region.Id, "poi.sqlite");
        TestFiles.WriteAllText(canonicalPath, "previous good data");

        _osmReader
            .ReadAmenityNodesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<RawPoi>());

        _exporter
            .ExportAsync(
                Arg.Any<IAsyncEnumerable<Poi>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                TestFiles.WriteAllText(callInfo.ArgAt<string>(1), "broken re-render");
                return Task.CompletedTask;
            });

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.RunAsync(region, workDir, outDir, CancellationToken.None));

        // Assert: the previously published, valid dataset is untouched - only the failed
        // re-render attempt (in staging) was quarantined.
        Assert.True(File.Exists(canonicalPath));
        Assert.Equal("previous good data", File.ReadAllText(canonicalPath));

        var quarantineDir = Path.Combine(outDir, region.Id, "_failed");
        Assert.Single(Directory.GetFiles(quarantineDir, "poi.*.sqlite"));
    }

    [Fact]
    public async Task RunAsync_RemovesStaleStagingFile_LeftBehindByAPreviouslyInterruptedRun()
    {
        // Arrange: no canonical file exists yet, so this is a normal first-time render -
        // but a stale staging file from an earlier interrupted run is already sitting there.
        var sut = CreateSut();

        var region = new RegionDto(
            Id: "berlin",
            Name: "Berlin",
            PbfUrl: "http://example.com/berlin.osm.pbf",
            Poly: "berlin.poly");

        await using var tempDir = TestTemporaryDirectories.Create("removes-stale-staging-file", false);
        var workDir = tempDir.CreateSubDir("work").DirectoryPath;
        var outDir = tempDir.CreateSubDir("out").DirectoryPath;

        var pbfPath = Path.Combine(workDir, region.Id, "osm.pbf");
        TestFiles.WriteAllText(pbfPath, "dummy");

        var canonicalPath = Path.Combine(outDir, region.Id, "poi.sqlite");
        var stagingPath = canonicalPath + ".tmp";
        var stagingSidecarPath = stagingPath + "-wal";

        // Simulate a process that crashed mid-export on a previous run, leaving a
        // half-written staging file (and sidecar) behind.
        TestFiles.WriteAllText(stagingPath, "half-written from a crashed run");
        TestFiles.WriteAllText(stagingSidecarPath, "leftover wal");

        _osmReader
            .ReadAmenityNodesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<RawPoi>());

        _exporter
            .ExportAsync(
                Arg.Any<IAsyncEnumerable<Poi>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                TestFiles.WriteAllText(callInfo.ArgAt<string>(1), "freshly rendered data");
                return Task.CompletedTask;
            });

        // Act
        await sut.RunAsync(region, workDir, outDir, CancellationToken.None);

        // Assert: the stale staging sidecar is gone, and the canonical dataset reflects
        // the fresh render, not the leftover from the crashed attempt.
        Assert.False(File.Exists(stagingSidecarPath));
        Assert.True(File.Exists(canonicalPath));
        Assert.Equal("freshly rendered data", File.ReadAllText(canonicalPath));
    }

    [Fact]
    public async Task RunAsync_RecreatesDatabase_WhenOutputExistsAndOverwritePbfIsEnabled()
    {
        // Arrange
        var sut = CreateSut(overwritePbf: true);

        var region = new RegionDto(
            Id: "berlin",
            Name: "Berlin",
            PbfUrl: "http://example.com/berlin.osm.pbf",
            Poly: "berlin.poly");

        await using var tempDir = TestTemporaryDirectories.Create("recreate-db-when-overwrite-pbf-is-enabled", false);
        var workDir = tempDir.CreateSubDir("work").DirectoryPath;
        var outDir = tempDir.CreateSubDir("out").DirectoryPath;

        var pbfPath = Path.Combine(workDir, region.Id, "osm.pbf");
        TestFiles.WriteAllText(pbfPath, "dummy");

        var existingOutputPath = Path.Combine(outDir, region.Id, "poi.sqlite");
        TestFiles.WriteAllText(existingOutputPath, "existing");

        var cutPbfPath = Path.Combine(workDir, region.Id, "cut.osm.pbf");
        _polygonCutter
            .CutAsync(pbfPath, region.Poly, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(cutPbfPath));

        _osmReader
            .ReadAmenityNodesAsync(cutPbfPath, Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<RawPoi>());

        _exporter
            .ExportAsync(
                Arg.Any<IAsyncEnumerable<Poi>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                TestFiles.WriteAllText(callInfo.ArgAt<string>(1), "new content");
                return Task.CompletedTask;
            });

        var expectedStagingPath = existingOutputPath + ".tmp";

        // Act
        await sut.RunAsync(region, workDir, outDir, CancellationToken.None);

        // Assert: dbInit/export run against the staging file, not the still-existing
        // old canonical file directly
        await _dbInit.Received(1).InitializeAsync(expectedStagingPath, Arg.Any<CancellationToken>());
        await _exporter.Received(1).ExportAsync(
            Arg.Any<IAsyncEnumerable<Poi>>(),
            expectedStagingPath,
            Arg.Any<CancellationToken>());

        // The old canonical file is only replaced once the new one has passed validation
        Assert.False(File.Exists(expectedStagingPath));
        Assert.True(File.Exists(existingOutputPath));
        Assert.Equal("new content", File.ReadAllText(existingOutputPath));
    }

    [Theory]
    [InlineData("-wal")]
    [InlineData("-shm")]
    [InlineData("-journal")]
    public async Task RunAsync_DeletesSqliteSidecarFiles_WhenRecreatingDatabase(string sidecarSuffix)
    {
        // Arrange
        var sut = CreateSut(overwritePbf: true);

        var region = new RegionDto(
            Id: "berlin",
            Name: "Berlin",
            PbfUrl: "http://example.com/berlin.osm.pbf",
            Poly: "berlin.poly");

        await using var tempDir = TestTemporaryDirectories.Create("recreate-db-deletes-sidecars", false);
        var workDir = tempDir.CreateSubDir("work").DirectoryPath;
        var outDir = tempDir.CreateSubDir("out").DirectoryPath;

        var pbfPath = Path.Combine(workDir, region.Id, "osm.pbf");
        TestFiles.WriteAllText(pbfPath, "dummy");

        var existingOutputPath = Path.Combine(outDir, region.Id, "poi.sqlite");
        TestFiles.WriteAllText(existingOutputPath, "existing");

        var sidecarPath = existingOutputPath + sidecarSuffix;
        TestFiles.WriteAllText(sidecarPath, "stale sidecar");

        var cutPbfPath = Path.Combine(workDir, region.Id, "cut.osm.pbf");
        _polygonCutter
            .CutAsync(pbfPath, region.Poly, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(cutPbfPath));

        _osmReader
            .ReadAmenityNodesAsync(cutPbfPath, Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<RawPoi>());

        _exporter
            .ExportAsync(
                Arg.Any<IAsyncEnumerable<Poi>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                TestFiles.WriteAllText(callInfo.ArgAt<string>(1), "new content");
                return Task.CompletedTask;
            });

        // Act
        await sut.RunAsync(region, workDir, outDir, CancellationToken.None);

        // Assert: the stale sidecar next to the old canonical file is cleaned up as part
        // of promoting the newly validated staging database
        Assert.False(File.Exists(sidecarPath));
        Assert.True(File.Exists(existingOutputPath));
    }
}
