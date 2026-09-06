using POIneer.Render.Application.Contracts;
using Xunit;

namespace POIneer.Render.UnitTests.Application.Contracts;

public class DtoSmokeTests
{
    [Fact]
    public void RegionDto_Roundtrip_Properties()
    {
        var dto = new RegionDto("geofabrik/europe/germany/berlin", "Berlin", "https://download.geofabrik.de/europe/germany/berlin-latest.osm.pbf", null);
        Assert.Equal("geofabrik/europe/germany/berlin", dto.Id);
        Assert.Equal("Berlin", dto.Name);
        Assert.Equal("https://download.geofabrik.de/europe/germany/berlin-latest.osm.pbf", dto.PbfUrl);
        Assert.Null(dto.Country);
        Assert.Null(dto.Category);
    }

    // Country/Category are optional display/filter metadata (issue #175) - already
    // present in the region config JSON files, kept here so they roundtrip through
    // RegionDto instead of being silently dropped.
    [Fact]
    public void RegionDto_Roundtrip_OptionalCountryAndCategory()
    {
        var dto = new RegionDto(
            "geofabrik/europe/germany/berlin",
            "Berlin",
            "https://download.geofabrik.de/europe/germany/berlin-latest.osm.pbf",
            Poly: null,
            Country: "Germany",
            Category: "City");

        Assert.Equal("Germany", dto.Country);
        Assert.Equal("City", dto.Category);
    }

    [Fact]
    public void PoiDto_Roundtrip_Properties()
    {
        var dto = new PoiDto(1, "Cafe", "amenity", 13.4, 52.5);
        Assert.Equal(1, dto.OsmId);
        Assert.Equal("Cafe", dto.Name);
        Assert.Equal("amenity", dto.Amenity);
        Assert.Equal(13.4, dto.Latitude);
        Assert.Equal(52.5, dto.Longitude);
    }
}
