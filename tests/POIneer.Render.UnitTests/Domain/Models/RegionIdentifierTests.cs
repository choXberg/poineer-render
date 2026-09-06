using FluentAssertions;
using POIneer.Render.Domain.Models;
using Xunit;

namespace POIneer.Render.UnitTests.Domain.Models;

// RegionIdentifier is the shared validation/derivation used by LocalDatasetPublisher,
// AzureBlobDatasetPublishPlanner, RenderRegion, and Runner for globally unique,
// hierarchical region identifiers (ADR 0007). These tests cover it in isolation so the
// allow-list, segment rules, and leaf-segment/path derivation stay correct without
// needing a real filesystem or Azure dependency.
public sealed class RegionIdentifierTests
{
    [Theory]
    [InlineData("berlin", "berlin")]
    [InlineData("geofabrik/europe/germany/berlin", "geofabrik|europe|germany|berlin")]
    [InlineData(
        "geofabrik/europe/germany/bayern/mittelfranken",
        "geofabrik|europe|germany|bayern|mittelfranken")]
    public void ValidateHierarchicalId_ReturnsSegments_ForValidIds(string regionId, string expectedSegmentsJoinedByPipe)
    {
        var segments = RegionIdentifier.ValidateHierarchicalId(regionId, "regionId");

        segments.Should().Equal(expectedSegmentsJoinedByPipe.Split('|'));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../escape")]
    [InlineData("berlin/../../etc")]
    [InlineData("berlin/..")]
    [InlineData("berlin/.")]
    [InlineData("/berlin")]
    [InlineData("berlin/")]
    [InlineData("berlin//sub")]
    [InlineData("berlin\\sub")]
    [InlineData("berlin:sub")]
    [InlineData("berlin sub")]
    public void ValidateHierarchicalId_Throws_ForUnsafeIds(string regionId)
    {
        var act = () => RegionIdentifier.ValidateHierarchicalId(regionId, "regionId");

        act.Should().Throw<ArgumentException>().WithParameterName("regionId");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateHierarchicalId_Throws_ForNullOrWhitespace(string? regionId)
    {
        var act = () => RegionIdentifier.ValidateHierarchicalId(regionId!, "regionId");

        act.Should().Throw<ArgumentException>().WithParameterName("regionId");
    }

    [Theory]
    [InlineData("berlin", "berlin")]
    [InlineData("geofabrik/europe/germany/berlin", "berlin")]
    [InlineData("geofabrik/europe/germany/bayern/mittelfranken", "mittelfranken")]
    public void GetLeafSegment_ReturnsLastSegment(string regionId, string expectedLeaf)
    {
        RegionIdentifier.GetLeafSegment(regionId).Should().Be(expectedLeaf);
    }

    [Fact]
    public void CombinePath_CombinesBaseDirWithEverySegment()
    {
        var path = RegionIdentifier.CombinePath("out", "geofabrik/europe/germany/berlin");

        path.Should().Be(Path.Combine("out", "geofabrik", "europe", "germany", "berlin"));
    }

    [Fact]
    public void CombinePath_MatchesFlatPathCombine_ForNonHierarchicalId()
    {
        // Backward compatibility: for a flat, non-hierarchical id, CombinePath must
        // produce exactly the same path a plain Path.Combine(baseDir, regionId) call
        // would have produced before hierarchical ids were introduced.
        RegionIdentifier.CombinePath("out", "berlin").Should().Be(Path.Combine("out", "berlin"));
    }

    [Theory]
    [InlineData("2-abc123")]
    [InlineData("20260101000000000")]
    public void ValidateSingleSegment_Accepts_NonHierarchicalValues(string version)
    {
        var act = () => RegionIdentifier.ValidateSingleSegment(version, "version");

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("1/2")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../escape")]
    [InlineData("1/../../etc")]
    [InlineData("1\\2")]
    [InlineData("1:2")]
    [InlineData("1 2")]
    public void ValidateSingleSegment_Throws_ForUnsafeOrHierarchicalValues(string version)
    {
        var act = () => RegionIdentifier.ValidateSingleSegment(version, "version");

        act.Should().Throw<ArgumentException>().WithParameterName("version");
    }
}
