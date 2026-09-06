using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using POIneer.Render.Application.Contracts;
using POIneer.Render.Application.Options;
using POIneer.Render.Application.Ports;
using POIneer.Render.Domain.Models;

namespace POIneer.Render.Infrastructure.FileSystem;

// Local filesystem implementation of IDatasetPublisher (issue #132). Copies a validated
// dataset artifact into a configurable destination directory, laid out as
// "{DestinationDir}/{RegionId segments.../}/{leaf}.{Version}{extension}" so the region
// and dataset version stay identifiable from the file layout alone - e.g. when browsing
// the destination directly on the VPS. RegionId may be a globally unique, hierarchical
// identifier such as "geofabrik/europe/germany/berlin" (ADR 0007): each '/'-separated
// segment becomes its own nested directory, while the file itself is named after only
// the last ("leaf") segment so the file name stays short instead of repeating the whole
// hierarchy the surrounding directories already encode. The destination is always driven
// by PublisherOptions.DestinationDir; the same implementation works unchanged for a local
// development folder and for a filesystem location available on the VPS.
//
// Out of scope (see issue #132): Azure Blob Storage, FTP/SFTP, CDN integration,
// VPS/Azure synchronization, public download URLs.
public sealed class LocalDatasetPublisher : IDatasetPublisher
{
    private const int CopyBufferSize = 81920;
    private const string StagingFileSuffix = ".tmp";

    private readonly ILogger<LocalDatasetPublisher> _logger;
    private readonly PublisherOptions _options;

    public LocalDatasetPublisher(ILogger<LocalDatasetPublisher> logger, IOptions<PublisherOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public async Task<DatasetPublishResult> PublishAsync(
        DatasetPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RegionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Version);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);

        // RegionId and Version are interpolated directly into the destination directory
        // name and filename below. Restrict both to a small allow-list of characters
        // rather than only checking for path separators or "..", so that neither value -
        // however it was ultimately produced (region config, a future caller, or a
        // misconfiguration) - can ever cause the publisher to write outside DestinationDir
        // or produce a filename that is invalid on some platforms. RegionId may be
        // hierarchical (ADR 0007), e.g. "geofabrik/europe/germany/berlin" - each segment
        // is validated individually and becomes its own nested directory; Version is not
        // hierarchical and may not contain the '/' segment separator.
        var regionIdSegments = RegionIdentifier.ValidateHierarchicalId(request.RegionId, nameof(request.RegionId));
        RegionIdentifier.ValidateSingleSegment(request.Version, nameof(request.Version));

        if (!File.Exists(request.SourcePath))
            throw new FileNotFoundException($"Dataset artifact not found: {request.SourcePath}", request.SourcePath);

        if (string.IsNullOrWhiteSpace(_options.DestinationDir))
            throw new InvalidOperationException("Publisher:DestinationDir must be set when using the local dataset publisher.");

        var destinationDir = RegionIdentifier.CombinePath(_options.DestinationDir, request.RegionId);
        Directory.CreateDirectory(destinationDir);

        // The published file is named after only the leaf segment (e.g. "berlin", not
        // "geofabrik/europe/germany/berlin") - the surrounding directory structure
        // already encodes the full hierarchy, so repeating it in the file name would
        // just make an already-long, hierarchical id even longer for no benefit.
        var leafRegionId = regionIdSegments[^1];
        var extension = Path.GetExtension(request.SourcePath);
        var destinationPath = Path.GetFullPath(
            Path.Combine(destinationDir, $"{leafRegionId}.{request.Version}{extension}"));

        if (File.Exists(destinationPath))
        {
            switch (_options.OverwritePolicy)
            {
                case DatasetPublishOverwritePolicy.Skip:
                    _logger.LogInformation(
                        "Publish target already exists at {DestinationPath}, skipping (overwrite policy: Skip).",
                        destinationPath);
                    return new DatasetPublishResult(destinationPath, WasSkipped: true);

                case DatasetPublishOverwritePolicy.SkipIfIdentical:
                    if (await FilesAreEqualAsync(request.SourcePath, destinationPath, cancellationToken))
                    {
                        _logger.LogInformation(
                            "Publish target already exists at {DestinationPath} and matches the source artifact, skipping (overwrite policy: SkipIfIdentical).",
                            destinationPath);
                        return new DatasetPublishResult(destinationPath, WasSkipped: true);
                    }

                    throw new IOException(
                        $"Publish target already exists at {destinationPath}, but differs from the source artifact. Use overwrite policy Overwrite to replace it.");

                case DatasetPublishOverwritePolicy.Fail:
                    throw new IOException(
                        $"Publish target already exists at {destinationPath} and the overwrite policy is Fail.");

                case DatasetPublishOverwritePolicy.Overwrite:
                    _logger.LogInformation(
                        "Publish target already exists at {DestinationPath}, replacing it (overwrite policy: Overwrite).",
                        destinationPath);
                    break;

                default:
                    throw new InvalidOperationException($"Unrecognized overwrite policy: {_options.OverwritePolicy}");
            }
        }

        // Copy through a staging file and rename into place: if the copy is interrupted
        // (cancellation, crash, disk full), the half-written bytes land in the ".tmp" file,
        // never at destinationPath - so a later run's existence check for the overwrite
        // policy above never mistakes a partial file for a real, previously published one.
        var stagingDestinationPath = destinationPath + StagingFileSuffix;

        try
        {
            await using (var source = new FileStream(
                request.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true))
            await using (var destination = new FileStream(
                stagingDestinationPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

            File.Move(stagingDestinationPath, destinationPath, overwrite: true);
        }
        catch
        {
            // Best effort: if the copy was cancelled/failed or the move itself failed, do
            // not leave the half-written (or fully-written-but-unmoved) ".tmp" file sitting
            // in the destination directory - nothing else ever revisits it, so it would
            // otherwise accumulate indefinitely and confuse an operator inspecting the
            // publish destination. The original exception always wins over a cleanup
            // failure, so this never masks the real error.
            TryDeleteStagingFile(stagingDestinationPath);
            throw;
        }

        _logger.LogInformation(
            "Published dataset for region {RegionId} (version {Version}) to {DestinationPath}.",
            request.RegionId,
            request.Version,
            destinationPath);

        return new DatasetPublishResult(destinationPath, WasSkipped: false);
    }

    private void TryDeleteStagingFile(string stagingDestinationPath)
    {
        try
        {
            if (File.Exists(stagingDestinationPath))
            {
                File.Delete(stagingDestinationPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort only - see the catch block above that calls this. Failing to
            // delete a leftover staging file is unfortunate but must never surface as the
            // error the caller sees instead of the real failure that triggered cleanup.
            _logger.LogWarning(
                ex,
                "Failed to clean up leftover staging file {StagingPath} after a failed publish attempt.",
                stagingDestinationPath);
        }
    }

    private static async Task<bool> FilesAreEqualAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var sourceInfo = new FileInfo(sourcePath);
        var destinationInfo = new FileInfo(destinationPath);

        if (sourceInfo.Length != destinationInfo.Length)
            return false;

        var sourceHash = await ComputeSha256Async(sourcePath, cancellationToken);
        var destinationHash = await ComputeSha256Async(destinationPath, cancellationToken);

        return CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash);
    }

    private static async Task<byte[]> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true);

        return await SHA256.HashDataAsync(stream, cancellationToken);
    }
}
