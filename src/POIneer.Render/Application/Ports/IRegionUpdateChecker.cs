using POIneer.Render.Application.Contracts;

namespace POIneer.Render.Application.Ports;

public interface IRegionUpdateChecker
{
    Task<RegionUpdateCheckResult> CheckAsync(
        RegionDto region,
        string statePath,
        CancellationToken ct = default);

    Task MarkProcessedAsync(
        RegionDto region,
        string statePath,
        RegionUpdateMetadata metadata,
        CancellationToken ct = default);
}
