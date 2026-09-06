using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using POIneer.Render.Adapters.Input;
using POIneer.Render.TestHelpers;
using Xunit;

namespace POIneer.Render.UnitTests.Adapters.Input;

public sealed class GeofabrikRegionSourceTests
{
    [Fact]
    public async Task GetRegionsAsync_ReadsRegionsFromJson_IncludingHierarchicalIdAndDisplayMetadata()
    {
        // Arrange: Country/Category are optional display/filter metadata (issue #175,
        // ADR 0007) - RegionDto now captures them instead of silently dropping them, and
        // Id may be a globally unique, hierarchical identifier such as
        // "geofabrik/europe/germany/berlin".
        await using var tempDir = TestTemporaryDirectories.Create("geofabrik-region-source-reads-json", false);
        var regionsPath = Path.Combine(tempDir.DirectoryPath, "regions.json");
        TestFiles.WriteAllText(
            regionsPath,
            """
            [
              {
                "Id": "geofabrik/europe/germany/berlin",
                "Name": "Berlin",
                "Country": "Germany",
                "Category": "City",
                "PbfUrl": "https://download.geofabrik.de/europe/germany/berlin-latest.osm.pbf",
                "Poly": "berlin.poly"
              },
              {
                "Id": "geofabrik/europe/germany/bayern/mittelfranken",
                "Name": "Mittelfranken",
                "Country": "Germany",
                "Category": "District",
                "PbfUrl": "https://download.geofabrik.de/europe/germany/bayern/mittelfranken-latest.osm.pbf"
              }
            ]
            """);

        var sut = CreateSut();

        // Act
        var regions = await sut.GetRegionsAsync(regionsPath, CancellationToken.None);

        // Assert
        regions.Should().HaveCount(2);
        regions[0].Id.Should().Be("geofabrik/europe/germany/berlin");
        regions[0].Name.Should().Be("Berlin");
        regions[0].Country.Should().Be("Germany");
        regions[0].Category.Should().Be("City");
        regions[0].PbfUrl.Should().Be("https://download.geofabrik.de/europe/germany/berlin-latest.osm.pbf");
        regions[0].Poly.Should().Be("berlin.poly");

        regions[1].Id.Should().Be("geofabrik/europe/germany/bayern/mittelfranken");
        regions[1].Name.Should().Be("Mittelfranken");
        regions[1].Country.Should().Be("Germany");
        regions[1].Category.Should().Be("District");
        regions[1].PbfUrl.Should().Be("https://download.geofabrik.de/europe/germany/bayern/mittelfranken-latest.osm.pbf");
        regions[1].Poly.Should().BeNull();
    }

    [Fact]
    public async Task GetRegionsAsync_FiltersRegionsWithoutPbfUrl()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("geofabrik-region-source-filters-missing-pbf-url", false);
        var regionsPath = Path.Combine(tempDir.DirectoryPath, "regions.json");
        TestFiles.WriteAllText(
            regionsPath,
            """
            [
              {
                "Id": "berlin",
                "Name": "Berlin",
                "PbfUrl": "https://download.geofabrik.de/europe/germany/berlin-latest.osm.pbf"
              },
              {
                "Id": "empty-url",
                "Name": "Empty URL",
                "PbfUrl": ""
              },
              {
                "Id": "whitespace-url",
                "Name": "Whitespace URL",
                "PbfUrl": "   "
              }
            ]
            """);

        var sut = CreateSut();

        // Act
        var regions = await sut.GetRegionsAsync(regionsPath, CancellationToken.None);

        // Assert
        regions.Should().ContainSingle();
        regions[0].Id.Should().Be("berlin");
    }

    [Fact]
    public async Task GetRegionsAsync_ReturnsEmptyList_WhenJsonContainsNull()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("geofabrik-region-source-null-json", false);
        var regionsPath = Path.Combine(tempDir.DirectoryPath, "regions.json");
        TestFiles.WriteAllText(regionsPath, "null");

        var sut = CreateSut();

        // Act
        var regions = await sut.GetRegionsAsync(regionsPath, CancellationToken.None);

        // Assert
        regions.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRegionsAsync_ThrowsFileNotFoundException_WhenFileDoesNotExist()
    {
        // Arrange
        await using var tempDir = TestTemporaryDirectories.Create("geofabrik-region-source-missing-file", false);
        var missingPath = Path.Combine(tempDir.DirectoryPath, "missing-regions.json");
        var sut = CreateSut();

        // Act
        var act = () => sut.GetRegionsAsync(missingPath, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    private static GeofabrikRegionSource CreateSut()
        => new(NullLogger<GeofabrikRegionSource>.Instance);
}
