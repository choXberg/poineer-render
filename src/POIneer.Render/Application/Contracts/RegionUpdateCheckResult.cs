namespace POIneer.Render.Application.Contracts;

public sealed record RegionUpdateCheckResult(
    bool ShouldRedownloadPbf,
    string Reason,
    RegionUpdateMetadata RemoteMetadata,
    RegionRenderState? StoredState);
