using POIneer.Render.Application.Contracts;
using POIneer.Render.Application.Ports;

namespace POIneer.Render.Application.UseCases;

public sealed class RegionUpdateChecker : IRegionUpdateChecker
{
    private readonly IRemotePbfMetadataReader _metadataReader;
    private readonly IRegionRenderStateStore _stateStore;

    public RegionUpdateChecker(
        IRemotePbfMetadataReader metadataReader,
        IRegionRenderStateStore stateStore)
    {
        _metadataReader = metadataReader;
        _stateStore = stateStore;
    }

    public async Task<RegionUpdateCheckResult> CheckAsync(
        RegionDto region,
        string statePath,
        CancellationToken ct = default)
    {
        var remoteMetadata = await _metadataReader.GetMetadataAsync(region.PbfUrl, ct);
        var storedState = await _stateStore.ReadAsync(statePath, ct);
        var reason = GetChangeReason(region, remoteMetadata, storedState);

        return new RegionUpdateCheckResult(
            ShouldRedownloadPbf: reason is not null,
            Reason: reason ?? "Remote PBF metadata is unchanged.",
            RemoteMetadata: remoteMetadata,
            StoredState: storedState);
    }

    public Task MarkProcessedAsync(
        RegionDto region,
        string statePath,
        RegionUpdateMetadata metadata,
        CancellationToken ct = default)
    {
        var state = new RegionRenderState(
            region.Id,
            region.PbfUrl,
            metadata,
            DateTimeOffset.UtcNow);

        return _stateStore.WriteAsync(statePath, state, ct);
    }

    private static string? GetChangeReason(
        RegionDto region,
        RegionUpdateMetadata remoteMetadata,
        RegionRenderState? storedState)
    {
        if (storedState is null)
            return "No previous render state exists for this region.";

        if (!StringComparer.Ordinal.Equals(storedState.RegionId, region.Id))
            return "Stored render state belongs to a different region.";

        if (!StringComparer.Ordinal.Equals(storedState.PbfUrl, region.PbfUrl))
            return "Configured PBF URL changed.";

        if (!string.IsNullOrWhiteSpace(remoteMetadata.ETag))
        {
            return StringComparer.Ordinal.Equals(remoteMetadata.ETag, storedState.LastProcessedMetadata.ETag)
                ? null
                : "Remote ETag changed.";
        }

        if (remoteMetadata.LastModified.HasValue)
        {
            return remoteMetadata.LastModified == storedState.LastProcessedMetadata.LastModified
                ? null
                : "Remote Last-Modified changed.";
        }

        return "Remote metadata has no ETag or Last-Modified value.";
    }
}
