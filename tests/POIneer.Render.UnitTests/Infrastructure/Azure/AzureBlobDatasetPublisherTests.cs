using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using POIneer.Render.Application.Contracts;
using POIneer.Render.Application.Options;
using POIneer.Render.Application.Ports;
using POIneer.Render.Infrastructure.Azure;
using Xunit;

namespace POIneer.Render.UnitTests.Infrastructure.Azure;

public sealed class AzureBlobDatasetPublisherTests
{
    private readonly IAzureBlobDatasetPublishPlanner _planner = Substitute.For<IAzureBlobDatasetPublishPlanner>();
    private readonly IAzureBlobDatasetUploader _uploader = Substitute.For<IAzureBlobDatasetUploader>();
    private readonly IDatasetArtifactMetadataFactory _metadataFactory = Substitute.For<IDatasetArtifactMetadataFactory>();

    [Fact]
    public async Task PublishAsync_SkipsUpload_WhenPlannerSaysDestinationAlreadyMatches()
    {
        await using var source = TestSourceFile.Create();
        var request = CreateRequest(source.Path);
        var sut = CreateSut();

        _planner.PlanAsync(request, Arg.Any<CancellationToken>())
            .Returns(new AzureBlobDatasetPublishDecision(
                "berlin/berlin.2-abc123.sqlite",
                DestinationExists: true,
                ShouldUpload: false,
                "matches"));

        var result = await sut.PublishAsync(request);

        result.DestinationPath.Should().Be("berlin/berlin.2-abc123.sqlite");
        result.WasSkipped.Should().BeTrue();
        await _uploader.DidNotReceiveWithAnyArgs()
            .UploadAsync(default!, default!, default!, default, default);
    }

    [Fact]
    public async Task PublishAsync_UploadsBlobWithMetadata_WhenDestinationIsMissing()
    {
        await using var source = TestSourceFile.Create();
        var request = CreateRequest(source.Path);
        var metadata = CreateMetadata(fileSizeBytes: source.Length);
        var sut = CreateSut();

        _planner.PlanAsync(request, Arg.Any<CancellationToken>())
            .Returns(new AzureBlobDatasetPublishDecision(
                "berlin/berlin.2-abc123.sqlite",
                DestinationExists: false,
                ShouldUpload: true,
                "missing"));
        _metadataFactory.CreateAsync(request.RegionId, request.Version, request.SourcePath, Arg.Any<CancellationToken>())
            .Returns(metadata);

        var result = await sut.PublishAsync(request);

        result.DestinationPath.Should().Be("berlin/berlin.2-abc123.sqlite");
        result.WasSkipped.Should().BeFalse();
        await _uploader.Received(1).UploadAsync(
            "berlin/berlin.2-abc123.sqlite",
            request.SourcePath,
            Arg.Is<IReadOnlyDictionary<string, string>>(actual =>
                actual[AzureBlobDatasetMetadataKeys.RegionId] == "berlin"
                && actual[AzureBlobDatasetMetadataKeys.Version] == "2-abc123"
                && actual[AzureBlobDatasetMetadataKeys.FileName] == "poi.sqlite"
                && actual[AzureBlobDatasetMetadataKeys.FileSizeBytes] == source.Length.ToString(CultureInfo.InvariantCulture)
                && actual[AzureBlobDatasetMetadataKeys.CreatedUtc] == "2026-09-03T10:00:00.0000000+00:00"
                && actual[AzureBlobDatasetMetadataKeys.Sha256Checksum] == "sha256"),
            overwriteExisting: false,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishAsync_SkipsExistingDestination_WhenOverwritePolicyIsSkip()
    {
        await using var source = TestSourceFile.Create();
        var request = CreateRequest(source.Path);
        var sut = CreateSut(overwritePolicy: DatasetPublishOverwritePolicy.Skip);

        _planner.PlanAsync(request, Arg.Any<CancellationToken>())
            .Returns(new AzureBlobDatasetPublishDecision(
                "berlin/berlin.2-abc123.sqlite",
                DestinationExists: true,
                ShouldUpload: true,
                "differs"));

        var result = await sut.PublishAsync(request);

        result.WasSkipped.Should().BeTrue();
        await _uploader.DidNotReceiveWithAnyArgs()
            .UploadAsync(default!, default!, default!, default, default);
    }

    [Fact]
    public async Task PublishAsync_Throws_WhenOverwritePolicyIsFailAndDestinationExists()
    {
        await using var source = TestSourceFile.Create();
        var request = CreateRequest(source.Path);
        var sut = CreateSut(overwritePolicy: DatasetPublishOverwritePolicy.Fail);

        _planner.PlanAsync(request, Arg.Any<CancellationToken>())
            .Returns(new AzureBlobDatasetPublishDecision(
                "berlin/berlin.2-abc123.sqlite",
                DestinationExists: true,
                ShouldUpload: true,
                "differs"));

        var act = async () => await sut.PublishAsync(request);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*overwrite policy is Fail*");
        await _uploader.DidNotReceiveWithAnyArgs()
            .UploadAsync(default!, default!, default!, default, default);
    }

    [Fact]
    public async Task PublishAsync_Throws_WhenOverwritePolicyIsSkipIfIdenticalAndDestinationDiffers()
    {
        await using var source = TestSourceFile.Create();
        var request = CreateRequest(source.Path);
        var sut = CreateSut(overwritePolicy: DatasetPublishOverwritePolicy.SkipIfIdentical);

        _planner.PlanAsync(request, Arg.Any<CancellationToken>())
            .Returns(new AzureBlobDatasetPublishDecision(
                "berlin/berlin.2-abc123.sqlite",
                DestinationExists: true,
                ShouldUpload: true,
                "differs"));

        var act = async () => await sut.PublishAsync(request);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not match the source artifact metadata*Overwrite*");
        await _uploader.DidNotReceiveWithAnyArgs()
            .UploadAsync(default!, default!, default!, default, default);
    }

    [Fact]
    public async Task PublishAsync_OverwritesExistingDestination_WhenOverwritePolicyIsOverwrite()
    {
        await using var source = TestSourceFile.Create();
        var request = CreateRequest(source.Path);
        var metadata = CreateMetadata(fileSizeBytes: source.Length);
        var sut = CreateSut(overwritePolicy: DatasetPublishOverwritePolicy.Overwrite);

        _planner.PlanAsync(request, Arg.Any<CancellationToken>())
            .Returns(new AzureBlobDatasetPublishDecision(
                "berlin/berlin.2-abc123.sqlite",
                DestinationExists: true,
                ShouldUpload: true,
                "differs"));
        _metadataFactory.CreateAsync(request.RegionId, request.Version, request.SourcePath, Arg.Any<CancellationToken>())
            .Returns(metadata);

        var result = await sut.PublishAsync(request);

        result.WasSkipped.Should().BeFalse();
        await _uploader.Received(1)
            .UploadAsync(
                "berlin/berlin.2-abc123.sqlite",
                request.SourcePath,
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                overwriteExisting: true,
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishAsync_Throws_WhenMaxUploadsPerRunWouldBeExceeded()
    {
        await using var source = TestSourceFile.Create();
        var request = CreateRequest(source.Path);
        var metadata = CreateMetadata(fileSizeBytes: source.Length);
        var sut = CreateSut(maxUploadsPerRun: 1);

        _planner.PlanAsync(request, Arg.Any<CancellationToken>())
            .Returns(new AzureBlobDatasetPublishDecision("berlin/berlin.2-abc123.sqlite", false, true, "missing"));
        _metadataFactory.CreateAsync(request.RegionId, request.Version, request.SourcePath, Arg.Any<CancellationToken>())
            .Returns(metadata);

        await sut.PublishAsync(request);
        var act = async () => await sut.PublishAsync(request);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*MaxUploadsPerRun*");
    }

    [Fact]
    public async Task PublishAsync_Throws_WhenMaxUploadBytesPerRunWouldBeExceeded()
    {
        await using var source = TestSourceFile.Create();
        var request = CreateRequest(source.Path);
        var sut = CreateSut(maxUploadBytesPerRun: source.Length - 1);

        _planner.PlanAsync(request, Arg.Any<CancellationToken>())
            .Returns(new AzureBlobDatasetPublishDecision("berlin/berlin.2-abc123.sqlite", false, true, "missing"));
        _metadataFactory.CreateAsync(request.RegionId, request.Version, request.SourcePath, Arg.Any<CancellationToken>())
            .Returns(CreateMetadata(fileSizeBytes: source.Length));

        var act = async () => await sut.PublishAsync(request);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*MaxUploadBytesPerRun*");
        await _uploader.DidNotReceiveWithAnyArgs()
            .UploadAsync(default!, default!, default!, default, default);
    }

    [Fact]
    public async Task PublishAsync_ReleasesUploadCapacity_WhenUploadFails()
    {
        await using var source = TestSourceFile.Create();
        var request = CreateRequest(source.Path);
        var metadata = CreateMetadata(fileSizeBytes: source.Length);
        var sut = CreateSut(maxUploadsPerRun: 1);
        var uploadAttempts = 0;

        _planner.PlanAsync(request, Arg.Any<CancellationToken>())
            .Returns(new AzureBlobDatasetPublishDecision("berlin/berlin.2-abc123.sqlite", false, true, "missing"));
        _metadataFactory.CreateAsync(request.RegionId, request.Version, request.SourcePath, Arg.Any<CancellationToken>())
            .Returns(metadata);
        _uploader
            .UploadAsync(
                "berlin/berlin.2-abc123.sqlite",
                request.SourcePath,
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                overwriteExisting: false,
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                uploadAttempts++;
                if (uploadAttempts == 1)
                {
                    throw new InvalidOperationException("Transient upload failure.");
                }

                return Task.CompletedTask;
            });

        var firstAct = async () => await sut.PublishAsync(request);
        await firstAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Transient upload failure.");

        var result = await sut.PublishAsync(request);

        result.WasSkipped.Should().BeFalse();
        uploadAttempts.Should().Be(2);
        await _uploader.Received(2)
            .UploadAsync(
                "berlin/berlin.2-abc123.sqlite",
                request.SourcePath,
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                overwriteExisting: false,
                Arg.Any<CancellationToken>());
    }

    private AzureBlobDatasetPublisher CreateSut(
        DatasetPublishOverwritePolicy overwritePolicy = DatasetPublishOverwritePolicy.SkipIfIdentical,
        int maxUploadsPerRun = 1,
        long maxUploadBytesPerRun = 1_073_741_824)
        => new(
            _planner,
            _uploader,
            _metadataFactory,
            NullLogger<AzureBlobDatasetPublisher>.Instance,
            Options.Create(new PublisherOptions
            {
                Target = DatasetPublisherTarget.AzureBlob,
                OverwritePolicy = overwritePolicy
            }),
            Options.Create(new AzureBlobPublisherOptions
            {
                ContainerName = "regions",
                MaxUploadsPerRun = maxUploadsPerRun,
                MaxUploadBytesPerRun = maxUploadBytesPerRun
            }));

    private static DatasetPublishRequest CreateRequest(string sourcePath)
        => new("berlin", "2-abc123", sourcePath);

    private static DatasetArtifactMetadata CreateMetadata(long fileSizeBytes)
        => new(
            "berlin",
            "2-abc123",
            "poi.sqlite",
            fileSizeBytes,
            DateTimeOffset.Parse("2026-09-03T10:00:00Z"),
            "sha256");

    private sealed class TestSourceFile : IAsyncDisposable
    {
        private TestSourceFile(string path)
        {
            Path = path;
            Length = new FileInfo(path).Length;
        }

        public string Path { get; }

        public long Length { get; }

        public static TestSourceFile Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlite");
            File.WriteAllText(path, "sqlite");
            return new TestSourceFile(path);
        }

        public ValueTask DisposeAsync()
        {
            if (File.Exists(Path))
                File.Delete(Path);

            return ValueTask.CompletedTask;
        }
    }
}
