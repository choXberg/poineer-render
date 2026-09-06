using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using POIneer.Render.Application.Options;
using POIneer.Render.Application.Ports;

namespace POIneer.Render.Infrastructure.FileSystem;

// Default IDatasetVersionCalculator: hashes the source PBF file's content (SHA-256, first
// 16 hex chars) and combines it with the configured PublisherOptions.SchemaVersion. Two
// renders from byte-identical PBF input with the same SchemaVersion always produce the
// identical version string - so a forced re-render of otherwise-unchanged data publishes
// to the same destination filename and can be handled idempotently by IDatasetPublisher's
// configured overwrite policy, instead of accumulating a new file on every run. A genuinely updated
// OSM extract, or a deliberate SchemaVersion bump after a POIneer.Render release changes
// the exported schema/mapping, both produce a new version and a new published file.
public sealed class FileHashDatasetVersionCalculator : IDatasetVersionCalculator
{
    private const int HashPrefixLength = 16;
    private const int CopyBufferSize = 81920;

    private readonly PublisherOptions _options;

    public FileHashDatasetVersionCalculator(IOptions<PublisherOptions> options)
    {
        _options = options.Value;
    }

    public async Task<string> CalculateAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        await using var stream = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true);

        var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken);
        var hashPrefix = Convert.ToHexString(hashBytes)[..HashPrefixLength].ToLowerInvariant();

        return $"{_options.SchemaVersion}-{hashPrefix}";
    }
}
