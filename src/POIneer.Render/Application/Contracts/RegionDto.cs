namespace POIneer.Render.Application.Contracts;

// Data Transfer Object for Regions. Id and Name are kept deliberately separate (issue
// #175): Id is the stable, globally unique, hierarchical technical identifier used in
// paths and published artifact names (e.g. "geofabrik/europe/germany/berlin", see ADR
// 0007 and POIneer.Render.Domain.Models.RegionIdentifier), while Name is the
// human-readable display name (e.g. "Berlin"). Country and Category are optional
// display/filter metadata, not part of the technical identifier - already present in
// the region config JSON files, captured here so they are not silently dropped by
// deserialization.
public sealed record RegionDto(
    string Id,
    string Name,
    string PbfUrl,
    string? Poly,
    string? Country = null,
    string? Category = null);