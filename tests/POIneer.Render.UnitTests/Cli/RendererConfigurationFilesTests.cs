using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using POIneer.Render.Application.Contracts;
using POIneer.Render.Application.Options;
using Xunit;

namespace POIneer.Render.UnitTests.Cli;

public sealed class RendererConfigurationFilesTests
{
    [Theory]
    [InlineData("appsettings.json", "Cli/config/regions.production.json", "geofabrik/europe/germany/berlin")]
    [InlineData("appsettings.Development.json", "Cli/config/regions.local.json", "geofabrik/europe/germany/berlin")]
    [InlineData("appsettings.Production.json", "Cli/config/regions.production.json", null)]
    public void RendererConfiguration_HasExpectedOnlyRegionIdAndExistingRegionsFile(
        string settingsFileName,
        string expectedRegionsJson,
        string? expectedOnlyRegionId)
    {
        var repositoryRoot = FindRepositoryRoot();
        var settingsPath = Path.Combine(repositoryRoot, "src", "POIneer.Render", settingsFileName);

        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var renderer = document.RootElement.GetProperty("Renderer");

        // OnlyRegionId must be set explicitly (even to null) rather than omitted - an
        // omitted key silently falls back to whatever a lower-priority settings file
        // defines instead of actually clearing it (see the Production incident this
        // guards against: appsettings.json defaults OnlyRegionId to "berlin" for local/dev
        // runs, and simply removing the key from appsettings.Production.json left that
        // default in effect in Production too).
        renderer.TryGetProperty("OnlyRegionId", out var onlyRegionIdElement)
            .Should().BeTrue(
                $"{settingsFileName} should set OnlyRegionId explicitly (even to null) instead of omitting it — " +
                "an omitted key silently falls back to whatever a lower-priority settings file defines");

        var actualOnlyRegionId = onlyRegionIdElement.ValueKind == JsonValueKind.Null
            ? null
            : onlyRegionIdElement.GetString();

        actualOnlyRegionId.Should().Be(expectedOnlyRegionId);

        var configuredRegionsPath = renderer.GetProperty("RegionsJson").GetString();

        configuredRegionsPath.Should().NotBeNull();
        configuredRegionsPath
            .Replace('\\', '/')
            .Should()
            .EndWith(expectedRegionsJson);

        if (!Path.IsPathRooted(configuredRegionsPath!))
        {
            var resolvedRegionsPath = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(settingsPath)!,
                configuredRegionsPath!));

            File.Exists(resolvedRegionsPath).Should().BeTrue();
        }
    }

    // Unlike the per-file check above, this builds the actual layered IConfiguration the
    // same way Program.cs does (appsettings.json, then appsettings.{Environment}.json on
    // top) and binds it to RendererOptions - it exercises the real merge behavior instead
    // of each file's raw JSON in isolation. This is the test that would have caught the
    // original bug: a per-file check can be "correct" (Production explicitly nulls the
    // key) while the merged runtime configuration still silently resolves to the base
    // file's "berlin" default if the override is ever dropped again. Environment
    // variables and command-line args are intentionally not layered in here - they are
    // not part of what this regression is about.
    [Theory]
    [InlineData("Development", "geofabrik/europe/germany/berlin")]
    [InlineData("Production", null)]
    public void RendererConfiguration_MergedEnvironmentConfig_ResolvesExpectedOnlyRegionId(
        string environmentName,
        string? expectedOnlyRegionId)
    {
        var repositoryRoot = FindRepositoryRoot();
        var settingsBasePath = Path.Combine(repositoryRoot, "src", "POIneer.Render");

        var configuration = new ConfigurationBuilder()
            .SetBasePath(settingsBasePath)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
            .Build();

        var rendererOptions = configuration.GetSection("Renderer").Get<RendererOptions>();

        rendererOptions.Should().NotBeNull();
        rendererOptions!.OnlyRegionId.Should().Be(expectedOnlyRegionId);
    }

    [Fact]
    public void ProductionRendererConfiguration_PlacesLockFileInsideSharedDataMount()
    {
        var repositoryRoot = FindRepositoryRoot();
        var settingsPath = Path.Combine(repositoryRoot, "src", "POIneer.Render", "appsettings.Production.json");

        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var renderer = document.RootElement.GetProperty("Renderer");

        var lockFilePath = renderer.GetProperty("LockFilePath").GetString();

        lockFilePath.Should().NotBeNullOrWhiteSpace();
        lockFilePath!
            .Replace('\\', '/')
            .Should()
            .StartWith("/opt/poineer-render/data/");
    }

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Development.json")]
    [InlineData("appsettings.Production.json")]
    public void PublisherConfiguration_UsesCurrentSchemaVersion(string settingsFileName)
    {
        var repositoryRoot = FindRepositoryRoot();
        var settingsPath = Path.Combine(repositoryRoot, "src", "POIneer.Render", settingsFileName);

        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var publisher = document.RootElement.GetProperty("Publisher");

        publisher.GetProperty("SchemaVersion").GetString().Should().Be(new PublisherOptions { DestinationDir = "unused" }.SchemaVersion);
    }

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Development.json")]
    [InlineData("appsettings.Production.json")]
    public void PublisherConfiguration_SetsPublisherTargetExplicitly(string settingsFileName)
    {
        var repositoryRoot = FindRepositoryRoot();
        var settingsPath = Path.Combine(repositoryRoot, "src", "POIneer.Render", settingsFileName);

        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var publisher = document.RootElement.GetProperty("Publisher");

        publisher.TryGetProperty("Target", out var targetElement)
            .Should().BeTrue($"{settingsFileName} should choose its publisher target explicitly");

        var target = targetElement.GetString();

        target.Should().NotBeNullOrWhiteSpace();
        Enum.TryParse<DatasetPublisherTarget>(target, ignoreCase: false, out _)
            .Should().BeTrue($"{settingsFileName} should use a known publisher target");
    }

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Development.json")]
    [InlineData("appsettings.Production.json")]
    public void PublisherConfiguration_UsesSkipIfIdenticalOverwritePolicy(string settingsFileName)
    {
        var repositoryRoot = FindRepositoryRoot();
        var settingsPath = Path.Combine(repositoryRoot, "src", "POIneer.Render", settingsFileName);

        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var publisher = document.RootElement.GetProperty("Publisher");

        publisher.GetProperty("OverwritePolicy").GetString()
            .Should().Be(nameof(DatasetPublishOverwritePolicy.SkipIfIdentical));
    }

    [Fact]
    public void LaunchSettings_UsesDevelopmentEnvironment_ForLocalDotnetRun()
    {
        var repositoryRoot = FindRepositoryRoot();
        var launchSettingsPath = Path.Combine(
            repositoryRoot,
            "src",
            "POIneer.Render",
            "Properties",
            "launchSettings.json");

        using var document = JsonDocument.Parse(File.ReadAllText(launchSettingsPath));

        document.RootElement
            .GetProperty("profiles")
            .GetProperty("POIneer.Render")
            .GetProperty("environmentVariables")
            .GetProperty("DOTNET_ENVIRONMENT")
            .GetString()
            .Should().Be("Development");
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "POIneerRender.sln")))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
