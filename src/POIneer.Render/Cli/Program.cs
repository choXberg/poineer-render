using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using POIneer.Render.Adapters.Input;
using POIneer.Render.Adapters.Output;
using POIneer.Render.Application.Contracts;
using POIneer.Render.Application.Mapping;
using POIneer.Render.Application.Options;
using POIneer.Render.Application.Ports;
using POIneer.Render.Application.UseCases;
using POIneer.Render.Infrastructure.Adapters.Osm;
using POIneer.Render.Infrastructure.Azure;
using POIneer.Render.Infrastructure.FileSystem;
using POIneer.Render.Infrastructure.Flyway;
using POIneer.Render.Infrastructure.Pathing;
using POIneer.Render.Infrastructure.Process;
using POIneer.Render.Infrastructure.Sqlite;
using POIneer.Render.Infrastructure.VectorTiles;
using POIneer.Render.Ports;


internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = ContentRootResolver.Resolve()
        });

        builder.Configuration
            .SetBasePath(builder.Environment.ContentRootPath)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables(prefix: "POINEER_RENDER__")
            .AddCommandLine(args);

        var rendererSection = builder.Configuration.GetSection("Renderer");

        IConfiguration bindSource = rendererSection.Exists()
            ? rendererSection
            : builder.Configuration;

        builder.Services
            .AddOptions<RendererOptions>()
            .Bind(bindSource)
            .Validate(RendererOptionsValidation.HasRequiredPaths, RendererOptionsValidation.RequiredPathsMessage)
            .Validate(RendererOptionsValidation.HasValidDownloadTimeout, RendererOptionsValidation.DownloadTimeoutMessage)
            .ValidateOnStart();

        builder.Services
            .AddOptions<PublisherOptions>()
            .Bind(builder.Configuration.GetSection("Publisher"))
            .PostConfigure(options =>
            {
                options.DestinationDir = ConfiguredPathResolver.ResolveOptional(
                    builder.Environment.ContentRootPath,
                    options.DestinationDir);
            })
            .Validate(
                _ => PublisherOptionsValidation.IsDefinedTargetName(builder.Configuration["Publisher:Target"]),
                PublisherOptionsValidation.DefinedTargetMessage)
            .Validate(PublisherOptionsValidation.HasDefinedTarget, PublisherOptionsValidation.DefinedTargetMessage)
            .Validate(PublisherOptionsValidation.HasRequiredDestinationDir, PublisherOptionsValidation.RequiredDestinationDirMessage)
            .ValidateOnStart();

        builder.Services
            .AddOptions<AzureBlobPublisherOptions>()
            .Bind(builder.Configuration.GetSection(AzureBlobPublisherOptions.SectionName))
            .Validate(
                options => IsLocalPublisherTarget(builder.Configuration)
                           || AzureBlobPublisherOptionsValidation.HasAccountNameOrBlobEndpoint(options),
                AzureBlobPublisherOptionsValidation.RequiredAccountOrEndpointMessage)
            .Validate(
                options => IsLocalPublisherTarget(builder.Configuration)
                           || AzureBlobPublisherOptionsValidation.HasContainerName(options),
                AzureBlobPublisherOptionsValidation.RequiredContainerNameMessage)
            .Validate(
                AzureBlobPublisherOptionsValidation.HasPositiveMaxUploadsPerRun,
                AzureBlobPublisherOptionsValidation.PositiveMaxUploadsPerRunMessage)
            .Validate(
                AzureBlobPublisherOptionsValidation.HasPositiveMaxUploadBytesPerRun,
                AzureBlobPublisherOptionsValidation.PositiveMaxUploadBytesPerRunMessage)
            .ValidateOnStart();

        builder.Services
            .AddOptions<FlywayOptions>()
            .Bind(builder.Configuration.GetSection(FlywayOptions.SectionName));

        builder.Services
            .AddOptions<VectorTileOptions>()
            .Bind(builder.Configuration.GetSection(VectorTileOptions.SectionName))
            .Validate(VectorTileOptionsValidation.HasRequiredPlanetilerPaths, VectorTileOptionsValidation.RequiredPlanetilerPathsMessage)
            .Validate(VectorTileOptionsValidation.HasValidZoomRange, VectorTileOptionsValidation.ZoomRangeMessage)
            .ValidateOnStart();

        builder.Services.AddHttpClient<IFileDownloader, HttpFileDownloader>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<RendererOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(options.DownloadTimeoutSeconds);
        });

        builder.Services.AddHttpClient<IRemotePbfMetadataReader, HttpRemotePbfMetadataReader>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<RendererOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(options.DownloadTimeoutSeconds);
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options =>
        {
            options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
            options.SingleLine = true;
            options.IncludeScopes = false;
        });
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Services.Configure<TempOptions>(
            builder.Configuration.GetSection("Temp"));
        builder.Services.AddSingleton<ITemporaryDirectoryFactory, TemporaryDirectoryFactory>();
        builder.Services.AddSingleton<IRegionSource, GeofabrikRegionSource>();
        builder.Services.AddSingleton<IPolygonCutter, OsmiumPolygonCutter>();
        builder.Services.AddSingleton<IOsmReader, OsmPbfReader>();
        builder.Services.AddSingleton<IFlywayInvocationBuilder, FlywayInvocationBuilder>();
        builder.Services.AddSingleton<ISqliteDatabaseInitializer, FlywaySqliteDatabaseInitializer>();
        builder.Services.AddSingleton<IExporter, SqliteExporter>();
        builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
        builder.Services.AddSingleton<IVectorTileGenerator, PlanetilerVectorTileGenerator>();
        builder.Services.AddSingleton<IRenderRegion, RenderRegion>();
        builder.Services.AddSingleton<IRawPoiMapper, RawPoiMapper>();
        builder.Services.AddSingleton<IDatasetValidator, SqliteDatasetValidator>();
        builder.Services.AddSingleton<LocalDatasetPublisher>();
        builder.Services.AddSingleton<AzureBlobDatasetPublisher>();
        builder.Services.AddSingleton<FilePublishedDatasetVerifier>();
        builder.Services.AddSingleton<AzureBlobPublishedDatasetVerifier>();
        builder.Services.AddSingleton<IAzureBlobDatasetMetadataReader, AzureBlobDatasetMetadataReader>();
        builder.Services.AddSingleton<IAzureBlobDatasetPublishPlanner, AzureBlobDatasetPublishPlanner>();
        builder.Services.AddSingleton<IAzureBlobDatasetUploader, AzureBlobDatasetUploader>();
        builder.Services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AzureBlobPublisherOptions>>().Value;
            var serviceClient = new BlobServiceClient(ResolveBlobServiceEndpoint(options), new DefaultAzureCredential());

            if (string.IsNullOrWhiteSpace(options.ContainerName))
            {
                throw new InvalidOperationException(
                    "AzureBlobPublisher:ContainerName must be set.");
            }

            return serviceClient.GetBlobContainerClient(options.ContainerName);
        });
        builder.Services.AddSingleton<IDatasetPublisher>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<PublisherOptions>>().Value;
            return options.Target switch
            {
                DatasetPublisherTarget.Local => sp.GetRequiredService<LocalDatasetPublisher>(),
                DatasetPublisherTarget.AzureBlob => sp.GetRequiredService<AzureBlobDatasetPublisher>(),
                _ => throw new InvalidOperationException($"Unrecognized publisher target: {options.Target}")
            };
        });
        builder.Services.AddSingleton<IDatasetVersionCalculator, FileHashDatasetVersionCalculator>();
        builder.Services.AddSingleton<IDatasetArtifactMetadataFactory, FileDatasetArtifactMetadataFactory>();
        builder.Services.AddSingleton<IPublishedDatasetVerifier>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<PublisherOptions>>().Value;
            return options.Target switch
            {
                DatasetPublisherTarget.Local => sp.GetRequiredService<FilePublishedDatasetVerifier>(),
                DatasetPublisherTarget.AzureBlob => sp.GetRequiredService<AzureBlobPublishedDatasetVerifier>(),
                _ => throw new InvalidOperationException($"Unrecognized publisher target: {options.Target}")
            };
        });
        builder.Services.AddSingleton<ISingleInstanceLockFactory, FileSingleInstanceLockFactory>();
        builder.Services.AddSingleton<IRegionRenderStateStore, FileRegionRenderStateStore>();
        builder.Services.AddSingleton<IRegionUpdateChecker, RegionUpdateChecker>();

        // CLI entry point
        builder.Services.AddSingleton<Runner>();

        var app = builder.Build();

        var runner = app.Services.GetRequiredService<Runner>();
        return await runner.RunAsync(
            app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping
        );
    }

    private static Uri ResolveBlobServiceEndpoint(AzureBlobPublisherOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.BlobEndpoint))
            return new Uri(options.BlobEndpoint);

        if (!string.IsNullOrWhiteSpace(options.AccountName))
            return new Uri($"https://{options.AccountName}.blob.core.windows.net");

        throw new InvalidOperationException(
            "AzureBlobPublisher:AccountName or AzureBlobPublisher:BlobEndpoint must be set.");
    }

    private static bool IsLocalPublisherTarget(IConfiguration configuration)
        => configuration
            .GetSection("Publisher")
            .Get<PublisherOptions>()?
            .Target != DatasetPublisherTarget.AzureBlob;

}
