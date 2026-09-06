using POIneer.Render.Application.Contracts;

namespace POIneer.Render.Application.Ports;

public interface IRegionRenderStateStore
{
    Task<RegionRenderState?> ReadAsync(
        string statePath,
        CancellationToken ct = default);

    Task WriteAsync(
        string statePath,
        RegionRenderState state,
        CancellationToken ct = default);
}
