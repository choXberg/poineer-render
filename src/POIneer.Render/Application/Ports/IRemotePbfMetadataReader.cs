using POIneer.Render.Application.Contracts;

namespace POIneer.Render.Application.Ports;

public interface IRemotePbfMetadataReader
{
    Task<RegionUpdateMetadata> GetMetadataAsync(
        string pbfUrl,
        CancellationToken ct = default);
}
