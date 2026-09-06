using FluentAssertions;
using POIneer.Render.Infrastructure.Pathing;
using POIneer.Render.TestHelpers;
using Xunit;

namespace POIneer.Render.UnitTests.Infrastructure.Pathing;

public sealed class ConfiguredPathResolverTests
{
    [Fact]
    public async Task Resolve_ResolvesRelativePathAgainstContentRoot()
    {
        await using var tempDir = TestTemporaryDirectories.Create("configured-path-relative", false);

        var result = ConfiguredPathResolver.Resolve(tempDir.DirectoryPath, "data/dev/renderer-out-dir");

        result.Should().Be(Path.GetFullPath(Path.Combine(
            tempDir.DirectoryPath,
            "data/dev/renderer-out-dir")));
    }

    [Fact]
    public async Task Resolve_KeepsAbsolutePathUnchanged()
    {
        await using var tempDir = TestTemporaryDirectories.Create("configured-path-absolute", false);
        var absolutePath = Path.Combine(tempDir.DirectoryPath, "out");

        var result = ConfiguredPathResolver.Resolve(tempDir.DirectoryPath, absolutePath);

        result.Should().Be(absolutePath);
    }

    [Fact]
    public async Task ResolveOptional_KeepsNullOptionalPath()
    {
        await using var tempDir = TestTemporaryDirectories.Create("configured-path-null", false);

        var result = ConfiguredPathResolver.ResolveOptional(tempDir.DirectoryPath, null);

        result.Should().BeNull();
    }
}
