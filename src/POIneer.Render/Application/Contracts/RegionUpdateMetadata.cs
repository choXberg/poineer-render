namespace POIneer.Render.Application.Contracts;

public sealed record RegionUpdateMetadata(
    string? ETag,
    DateTimeOffset? LastModified,
    long? ContentLength);
