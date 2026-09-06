using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using POIneer.Render.Application.Contracts;
using POIneer.Render.Application.Mapping;
using POIneer.Render.Application.Options;
using POIneer.Render.Application.Ports;
using POIneer.Render.Application.Ports.Model;
using POIneer.Render.Domain.Models;
using POIneer.Render.Ports;

namespace POIneer.Render.Application.UseCases;

public sealed class RenderRegion : IRenderRegion
{
    private const string StagingFileSuffix = ".tmp";
    private const string PoiDatasetFileName = "poi.sqlite";
    private const string VectorTileFileName = "map.pmtiles";

    private static readonly string[] SqliteSidecarSuffixes = ["-wal", "-shm", "-journal"];

    private readonly ILogger<RenderRegion> _logger;
    private readonly IPolygonCutter _polygonCutter;
    private readonly IOsmReader _osmReader;
    private readonly ISqliteDatabaseInitializer _dbInit;
    private readonly IExporter _exporter;
    private readonly IRawPoiMapper _rawPoiMapper;
    private readonly RendererOptions _rendererOptions;
    private readonly IDatasetValidator _datasetValidator;
    private readonly IDatasetPublisher _datasetPublisher;
    private readonly IDatasetVersionCalculator _datasetVersionCalculator;
    private readonly IDatasetArtifactMetadataFactory _datasetArtifactMetadataFactory;
    private readonly IPublishedDatasetVerifier _publishedDatasetVerifier;
    private readonly IVectorTileGenerator _vectorTileGenerator;
    private readonly VectorTileOptions _vectorTileOptions;

    public RenderRegion(
        ILogger<RenderRegion> log,
        IPolygonCutter polygonCutter,
        ISqliteDatabaseInitializer dbInit,
        IOsmReader osmReader,
        IExporter exporter,
        IRawPoiMapper rawPoiMapper,
        IOptions<RendererOptions> options,
        IDatasetValidator datasetValidator,
        IDatasetPublisher datasetPublisher,
        IDatasetVersionCalculator datasetVersionCalculator,
        IDatasetArtifactMetadataFactory datasetArtifactMetadataFactory,
        IPublishedDatasetVerifier publishedDatasetVerifier,
        IVectorTileGenerator vectorTileGenerator,
        IOptions<VectorTileOptions> vectorTileOptions)
    {
        _logger = log;
        _polygonCutter = polygonCutter;
        _osmReader = osmReader;
        _dbInit = dbInit;
        _exporter = exporter;
        _rawPoiMapper = rawPoiMapper;
        _rendererOptions = options.Value;
        _datasetValidator = datasetValidator;
        _datasetPublisher = datasetPublisher;
        _datasetVersionCalculator = datasetVersionCalculator;
        _datasetArtifactMetadataFactory = datasetArtifactMetadataFactory;
        _publishedDatasetVerifier = publishedDatasetVerifier;
        _vectorTileGenerator = vectorTileGenerator;
        _vectorTileOptions = vectorTileOptions.Value;
    }

    public async Task RunAsync(
        RegionDto regionDto,
        string workDir,
        string outDir,
        CancellationToken ct = default)
    {
        // regionDto.Id may be a globally unique, hierarchical region identifier (ADR
        // 0007), e.g. "geofabrik/europe/germany/berlin" - RegionIdentifier turns each
        // '/'-separated segment into its own nested directory and validates the id is
        // safe to use as a filesystem path.
        var pbfPath = GetPbfPath(workDir, regionDto.Id);
        var recreateDatabase = _rendererOptions.OverwriteDatabase || _rendererOptions.OverwritePbf;

        if (!File.Exists(pbfPath))
            throw new FileNotFoundException($"PBF not found: {pbfPath}");

        var regionOutDir = RegionIdentifier.CombinePath(outDir, regionDto.Id);
        Directory.CreateDirectory(regionOutDir);

        var canonicalPath = Path.GetFullPath(Path.Combine(regionOutDir, PoiDatasetFileName));
        var canonicalVectorTilePath = Path.GetFullPath(Path.Combine(regionOutDir, VectorTileFileName));
        var pbfChangedSinceOutput = File.Exists(canonicalPath)
            && File.GetLastWriteTimeUtc(pbfPath) > File.GetLastWriteTimeUtc(canonicalPath);

        if (File.Exists(canonicalPath))
        {
            if (!recreateDatabase && !pbfChangedSinceOutput && (!_vectorTileOptions.Enabled || File.Exists(canonicalVectorTilePath)))
            {
                _logger.LogInformation("({Id}) Output SQLite already exists at {Out}, skipping rendering (overwrite disabled).", regionDto.Id, canonicalPath);
                return;
            }

            _logger.LogInformation(
                "({Id}) Output SQLite already exists at {Out}, but re-rendering is required (overwrite: {Overwrite}, pbfChangedSinceOutput: {PbfChangedSinceOutput}). It will only be replaced once the new dataset passes validation.",
                regionDto.Id,
                canonicalPath,
                recreateDatabase,
                pbfChangedSinceOutput);
        }

        var stagingPath = canonicalPath + StagingFileSuffix;
        var stagingVectorTilePath = GetVectorTileStagingPath(canonicalVectorTilePath);

        if (File.Exists(stagingPath))
        {
            _logger.LogWarning(
                "({Id}) Found a leftover staging database from a previous interrupted run at {Staging}, removing it before re-rendering.",
                regionDto.Id,
                stagingPath);
            DeleteSqliteDatabaseWithSidecars(stagingPath);
        }

        if (File.Exists(stagingVectorTilePath))
        {
            _logger.LogWarning(
                "({Id}) Found a leftover staging PMTiles file from a previous interrupted run at {Staging}, removing it before re-rendering.",
                regionDto.Id,
                stagingVectorTilePath);
            File.Delete(stagingVectorTilePath);
        }

        _logger.LogInformation("({Id}) Cutting polygon...", regionDto.Id);
        var cutPbf = await _polygonCutter.CutAsync(pbfPath, regionDto.Poly, ct);

        _logger.LogInformation("({Id}) Reading OSM → POIs...", regionDto.Id);
        var rawPois = _osmReader.ReadAmenityNodesAsync(cutPbf, ct);

        _logger.LogInformation("({Id}) Initializing SQLite database: {Out}", regionDto.Id, stagingPath);
        await _dbInit.InitializeAsync(
            stagingPath,
            ct);

        _logger.LogInformation("({Id}) Exporting to SQLite: {Out}", regionDto.Id, stagingPath);
        await _exporter.ExportAsync(
            MapRawPoisAsync(rawPois, ct),
            stagingPath,
            ct);

        _logger.LogInformation("({Id}) Validating generated dataset: {Out}", regionDto.Id, stagingPath);
        var result = await _datasetValidator.ValidateAsync(
            stagingPath,
            ct);

        if (!result.IsValid)
        {
            var quarantinePath = QuarantineInvalidDataset(outDir, regionDto.Id, stagingPath);

            _logger.LogError(
                "({Id}) Generated dataset failed validation and was moved to quarantine: {QuarantinePath}. Errors: {Errors}",
                regionDto.Id,
                quarantinePath,
                string.Join("; ", result.Errors));

            throw new InvalidOperationException(
                $"Generated dataset for region '{regionDto.Id}' is invalid and was quarantined at {quarantinePath}: {string.Join(", ", result.Errors)}");
        }

        if (_vectorTileOptions.Enabled)
        {
            _logger.LogInformation(
                "({Id}) Generating vector tile dataset: {Out}",
                regionDto.Id,
                stagingVectorTilePath);

            await _vectorTileGenerator.GenerateAsync(
                cutPbf,
                stagingVectorTilePath,
                ct);
        }

        _logger.LogInformation("({Id}) Promoting validated dataset to canonical location: {Out}", regionDto.Id, canonicalPath);
        PromoteStagingToCanonical(stagingPath, canonicalPath);

        if (_vectorTileOptions.Enabled)
        {
            _logger.LogInformation(
                "({Id}) Promoting vector tile dataset to canonical location: {Out}",
                regionDto.Id,
                canonicalVectorTilePath);
            PromoteVectorTileToCanonical(stagingVectorTilePath, canonicalVectorTilePath);
        }

        // Hash-based, not wall-clock: a forced re-render of byte-identical PBF input (same
        // SchemaVersion too) computes the same version and therefore the same destination
        // filename, so IDatasetPublisher's configured overwrite policy can make
        // republishing unchanged data a no-op instead of accumulating a new file on every run. Hashing
        // cutPbf (not the original pbfPath) so a poly-file change is also picked up, since
        // it can change what actually gets rendered even when the raw PBF download did not.
        var version = await _datasetVersionCalculator.CalculateAsync(cutPbf, ct);

        // Metadata is generated from the canonical artifact - only after validation and
        // promotion have both succeeded - and stays independent from IDatasetPublisher, so
        // it describes the artifact itself rather than a specific publish destination
        // (issue #130).
        var artifactMetadata = await _datasetArtifactMetadataFactory.CreateAsync(
            regionDto.Id,
            version,
            canonicalPath,
            ct);

        _logger.LogInformation(
            "({Id}) Dataset artifact metadata: fileName={FileName}, sizeBytes={SizeBytes}, sha256={Sha256}, createdUtc={CreatedUtc}.",
            regionDto.Id,
            artifactMetadata.FileName,
            artifactMetadata.FileSizeBytes,
            artifactMetadata.Sha256Checksum,
            artifactMetadata.CreatedUtc);

        var publishResult = await _datasetPublisher.PublishAsync(
            new DatasetPublishRequest(regionDto.Id, version, canonicalPath),
            ct);

        _logger.LogInformation(
            "({Id}) Published dataset version {Version} to {DestinationPath} (skipped: {WasSkipped}).",
            regionDto.Id,
            version,
            publishResult.DestinationPath,
            publishResult.WasSkipped);

        // Verified on every publish call, not only when WasSkipped is false: a publish is
        // only considered successful once what is actually present at the destination is
        // confirmed to match, regardless of whether this run wrote new bytes there or found
        // a matching file already published by an earlier run (issue #135).
        var verificationResult = await _publishedDatasetVerifier.VerifyAsync(
            artifactMetadata,
            publishResult.DestinationPath,
            ct);

        if (!verificationResult.IsVerified)
        {
            _logger.LogError(
                "({Id}) Published dataset failed integrity verification at {DestinationPath} and will not be marked as successfully published. Errors: {Errors}",
                regionDto.Id,
                publishResult.DestinationPath,
                string.Join("; ", verificationResult.Errors));

            throw new InvalidOperationException(
                $"Published dataset for region '{regionDto.Id}' failed integrity verification at {publishResult.DestinationPath}: {string.Join(", ", verificationResult.Errors)}");
        }

        _logger.LogInformation(
            "({Id}) Published dataset verified successfully at {DestinationPath}.",
            regionDto.Id,
            publishResult.DestinationPath);

        _logger.LogInformation("({Id}) Done.", regionDto.Id);
    }

    /// <summary>
    /// Atomically promotes a validated staging database to become the canonical output at
    /// <paramref name="canonicalPath"/>. This is a purely local, in-place move within
    /// outDir - distinct from IDatasetPublisher, which afterwards copies that already-
    /// canonical file out to a separately configured publish destination (see
    /// PublisherOptions). A crash or an invalid render can never leave a partial or
    /// unvalidated file behind at the canonical location - a previously promoted dataset
    /// stays in place until a new one is confirmed valid.
    /// </summary>
    private static void PromoteStagingToCanonical(string stagingPath, string canonicalPath)
    {
        foreach (var suffix in SqliteSidecarSuffixes)
        {
            var canonicalSidecarPath = canonicalPath + suffix;
            if (File.Exists(canonicalSidecarPath))
            {
                File.Delete(canonicalSidecarPath);
            }

            var stagingSidecarPath = stagingPath + suffix;
            if (File.Exists(stagingSidecarPath))
            {
                File.Move(stagingSidecarPath, canonicalSidecarPath, overwrite: true);
            }
        }

        File.Move(stagingPath, canonicalPath, overwrite: true);
    }

    private static void PromoteVectorTileToCanonical(string stagingPath, string canonicalPath)
        => File.Move(stagingPath, canonicalPath, overwrite: true);

    private static string GetVectorTileStagingPath(string canonicalPath)
    {
        var directory = Path.GetDirectoryName(canonicalPath);
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(canonicalPath);
        var extension = Path.GetExtension(canonicalPath);
        var stagingFileName = $"{fileNameWithoutExtension}{StagingFileSuffix}{extension}";

        return string.IsNullOrWhiteSpace(directory)
            ? stagingFileName
            : Path.Combine(directory, stagingFileName);
    }

    /// <summary>
    /// Moves an invalid dataset out of the staging location into a per-region quarantine
    /// folder so it doesn't linger next to the canonical output, while still being
    /// preserved for post-mortem inspection.
    /// </summary>
    private static string QuarantineInvalidDataset(string outDir, string regionId, string invalidDatasetPath)
    {
        var quarantineDir = Path.Combine(RegionIdentifier.CombinePath(outDir, regionId), "_failed");
        Directory.CreateDirectory(quarantineDir);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        var quarantinePath = Path.Combine(quarantineDir, $"poi.{timestamp}.sqlite");

        File.Move(invalidDatasetPath, quarantinePath, overwrite: true);

        foreach (var suffix in SqliteSidecarSuffixes)
        {
            var sidecarPath = invalidDatasetPath + suffix;
            if (File.Exists(sidecarPath))
            {
                File.Move(sidecarPath, quarantinePath + suffix, overwrite: true);
            }
        }

        return quarantinePath;
    }

    /// <summary>
    /// Deletes a SQLite database file together with any leftover -wal/-shm/-journal
    /// sidecar files, so a stale sidecar can't cause recovery/lock confusion the next
    /// time this path is opened.
    /// </summary>
    private static void DeleteSqliteDatabaseWithSidecars(string sqliteFilePath)
    {
        File.Delete(sqliteFilePath);

        foreach (var suffix in SqliteSidecarSuffixes)
        {
            var sidecarPath = sqliteFilePath + suffix;
            if (File.Exists(sidecarPath))
            {
                File.Delete(sidecarPath);
            }
        }
    }

    private static string GetPbfPath(string workDir, string regionId) =>
        Path.Combine(RegionIdentifier.CombinePath(workDir, regionId), "osm.pbf");

    private async IAsyncEnumerable<Poi> MapRawPoisAsync(IAsyncEnumerable<RawPoi> rawPois, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var rawPoi in rawPois.WithCancellation(ct))
        {
            yield return _rawPoiMapper.Map(rawPoi);
        }
    }
}
