namespace POIneer.Render.Application.Contracts;

public sealed record RegionRenderState(
    string RegionId,
    string PbfUrl,
    RegionUpdateMetadata LastProcessedMetadata,
    DateTimeOffset ProcessedAtUtc);
