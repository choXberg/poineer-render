using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using POIneer.Render.Application.Options;
using POIneer.Render.Application.Ports;
using POIneer.Render.Domain.Models;
using POIneer.Render.Infrastructure.Pathing;
using POIneer.Render.Ports;

public sealed class Runner
{
    private readonly IHostEnvironment _hostEnvironment;
    private readonly ILogger<Runner> _logger;
    private readonly IRegionSource _regionSource;
    private readonly IRenderRegion _renderRegionUseCase;
    private readonly IRegionUpdateChecker _regionUpdateChecker;
    private readonly RendererOptions _rendererOptions;
    private readonly ISingleInstanceLockFactory _lockFactory;

    private readonly IFileDownloader _fileDownloader;

    public Runner(
        IHostEnvironment hostEnvironment,
        ILogger<Runner> log,
        IFileDownloader fileDownloader,
        IRegionSource regionSource,
        IRenderRegion renderRegionUseCase,
        IRegionUpdateChecker regionUpdateChecker,
        ISingleInstanceLockFactory lockFactory,
        IOptions<RendererOptions> rendererOptions)
    {
        _hostEnvironment = hostEnvironment;
        _logger = log;
        _fileDownloader = fileDownloader;
        _regionSource = regionSource;
        _renderRegionUseCase = renderRegionUseCase;
        _regionUpdateChecker = regionUpdateChecker;
        _lockFactory = lockFactory;
        _rendererOptions = rendererOptions.Value;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var contentRoot = _hostEnvironment.ContentRootPath;
        var redownloadPbf = _rendererOptions.OverwritePbf;

        var regionsPath = ConfiguredPathResolver.Resolve(contentRoot, _rendererOptions.RegionsJson);
        var workDir = ConfiguredPathResolver.Resolve(contentRoot, _rendererOptions.WorkDir);
        var outDir = ConfiguredPathResolver.Resolve(contentRoot, _rendererOptions.OutDir);
        var lockFilePath = string.IsNullOrWhiteSpace(_rendererOptions.LockFilePath)
            ? Path.Combine(workDir, "poineer-render.lock")
            : ConfiguredPathResolver.Resolve(contentRoot, _rendererOptions.LockFilePath);

        _logger.LogInformation("POIneer.Render starting (env: {Env})", _hostEnvironment.EnvironmentName);
        _logger.LogInformation("Using content root: {ContentRoot}", contentRoot);
        _logger.LogInformation("ContentRoot (raw): {ContentRoot}", _hostEnvironment.ContentRootPath);
        _logger.LogInformation("Regions file: {RegionsPath}", regionsPath);
        _logger.LogInformation("Work directory: {WorkDir}", workDir);
        _logger.LogInformation("Work directory (raw): {WorkDir}", _rendererOptions.WorkDir);
        _logger.LogInformation("Output directory: {OutDir}", outDir);
        _logger.LogInformation("Output directory (raw): {OutDir}", _rendererOptions.OutDir);
        _logger.LogInformation("Lock file: {LockFilePath}", lockFilePath);
        _logger.LogInformation("Dry run: {DryRun}", _rendererOptions.DryRun);

        if (_rendererOptions.DryRun)
        {
            _logger.LogInformation("Dry run enabled, exiting without doing anything.");
            return 0;
        }

        using var instanceLock = _lockFactory.Create(lockFilePath);
        if (!instanceLock.TryAcquire())
        {
            _logger.LogWarning(
                "Skipped execution: another POIneer.Render instance is already running (lock file: {LockFilePath}).",
                lockFilePath);
            return 0;
        }

        _logger.LogInformation("Creating directories (if not exists)...");
        Directory.CreateDirectory(workDir);
        Directory.CreateDirectory(outDir);

        _logger.LogInformation("Directories created (if not exists): {WorkDir}, {OutDir}", workDir, outDir);

        _logger.LogInformation("Using regions file: {RegionsPath}", regionsPath);

        if (!File.Exists(regionsPath))
            throw new FileNotFoundException($"Regions file not found: {regionsPath}");

        var regions = await _regionSource.GetRegionsAsync(regionsPath, ct);
        var failedRegionIds = new List<string>();

        foreach (var r in regions.Where(r => string.IsNullOrEmpty(_rendererOptions.OnlyRegionId) || r.Id == _rendererOptions.OnlyRegionId))
        {
            using (_logger.BeginScope("region:{RegionId}", r.Id))
            {
                try
                {
                    // r.Id may be a globally unique, hierarchical region identifier
                    // (ADR 0007), e.g. "geofabrik/europe/germany/berlin".
                    var regionWorkDir = RegionIdentifier.CombinePath(workDir, r.Id);
                    Directory.CreateDirectory(regionWorkDir);
                    var pbfPath = Path.Combine(regionWorkDir, "osm.pbf");
                    var renderStatePath = Path.Combine(regionWorkDir, "render-state.json");
                    var updateCheck = await _regionUpdateChecker.CheckAsync(r, renderStatePath, ct);

                    if (!File.Exists(pbfPath))
                    {
                        _logger.LogInformation("Downloading PBF for {Region} ... to {TargetPath}", r.Id, pbfPath);
                        await _fileDownloader.DownloadAsync(r.PbfUrl, pbfPath, ct);
                    }
                    else if (redownloadPbf)
                    {
                        _logger.LogInformation("PBF already exists for {Region} at {TargetPath}, but overwrite is enabled, re-downloading.", r.Id, pbfPath);
                        await _fileDownloader.DownloadAsync(r.PbfUrl, pbfPath, ct);
                    }
                    else if (updateCheck.ShouldRedownloadPbf)
                    {
                        _logger.LogInformation(
                            "PBF metadata changed for {Region} ({Reason}), re-downloading to {TargetPath}. ETag={ETag}, LastModified={LastModified}, ContentLength={ContentLength}",
                            r.Id,
                            updateCheck.Reason,
                            pbfPath,
                            updateCheck.RemoteMetadata.ETag,
                            updateCheck.RemoteMetadata.LastModified,
                            updateCheck.RemoteMetadata.ContentLength);
                        await _fileDownloader.DownloadAsync(r.PbfUrl, pbfPath, ct);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "PBF metadata unchanged for {Region}; skipping download. Render output checks will still run. ETag={ETag}, LastModified={LastModified}, ContentLength={ContentLength}",
                            r.Id,
                            updateCheck.RemoteMetadata.ETag,
                            updateCheck.RemoteMetadata.LastModified,
                            updateCheck.RemoteMetadata.ContentLength);
                    }
                    await _renderRegionUseCase.RunAsync(r, workDir, outDir, ct);
                    await _regionUpdateChecker.MarkProcessedAsync(r, renderStatePath, updateCheck.RemoteMetadata, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "({Region}) Region processing failed: {Message}", r.Id, ex.Message);
                    failedRegionIds.Add(r.Id);
                }
            }
        }

        if (failedRegionIds.Count > 0)
        {
            _logger.LogError(
                "Completed with {FailedCount} failed region(s): {FailedRegions}",
                failedRegionIds.Count,
                string.Join(", ", failedRegionIds));
            return 1;
        }

        _logger.LogInformation("All regions processed.");
        return 0;
    }
}
