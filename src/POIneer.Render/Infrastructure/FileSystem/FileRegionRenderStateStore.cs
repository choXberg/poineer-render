using System.Text.Json;
using Microsoft.Extensions.Logging;
using POIneer.Render.Application.Contracts;
using POIneer.Render.Application.Ports;

namespace POIneer.Render.Infrastructure.FileSystem;

public sealed class FileRegionRenderStateStore : IRegionRenderStateStore
{
    public FileRegionRenderStateStore(ILogger<FileRegionRenderStateStore> logger)
    {
        _logger = logger;
    }

    private readonly ILogger<FileRegionRenderStateStore> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<RegionRenderState?> ReadAsync(
        string statePath,
        CancellationToken ct = default)
    {
        if (!File.Exists(statePath))
            return null;

        try
        {
            await using var stream = File.OpenRead(statePath);
            var state = await JsonSerializer.DeserializeAsync<RegionRenderState>(
                stream,
                JsonOptions,
                ct);

            if (state is not null &&
                !string.IsNullOrWhiteSpace(state.RegionId) &&
                !string.IsNullOrWhiteSpace(state.PbfUrl) &&
                state.LastProcessedMetadata is not null)
            {
                return state;
            }

            _logger.LogWarning("Render state from {StatePath} is incomplete", statePath);
            return null;
        }
        catch (JsonException)
        {
            // Failed to deserialize the render state, possibly due to corruption or format changes.
            _logger.LogWarning("Failed to deserialize render state from {StatePath}", statePath);
            return null;
        }
    }

    public async Task WriteAsync(
        string statePath,
        RegionRenderState state,
        CancellationToken ct = default)
    {
        var stateDirectory = Path.GetDirectoryName(statePath);
        if (!string.IsNullOrEmpty(stateDirectory))
        {
            Directory.CreateDirectory(stateDirectory);
        }

        var tempPath = $"{statePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    state,
                    JsonOptions,
                    ct);

                await stream.FlushAsync(ct);
            }

            File.Move(tempPath, statePath, overwrite: true);
        }
        catch
        {
            DeleteTemporaryStateFile(tempPath);
            throw;
        }
    }

    private void DeleteTemporaryStateFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Failed to delete temporary render state file {TempPath}",
                tempPath);
        }
    }
}
