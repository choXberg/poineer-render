using System.Net;
using FluentAssertions;
using POIneer.Render.Adapters.Input;
using Xunit;

namespace POIneer.Render.UnitTests.Adapters.Input;

public sealed class HttpRemotePbfMetadataReaderTests
{
    [Fact]
    public async Task GetMetadataAsync_SendsHeadRequestAndReadsMetadataHeaders()
    {
        // Arrange
        using var handler = new StubHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var sut = new HttpRemotePbfMetadataReader(httpClient);

        // Act
        var metadata = await sut.GetMetadataAsync("https://example.com/berlin.osm.pbf");

        // Assert
        handler.RequestMethods.Should().Equal(HttpMethod.Head);
        handler.RequestUri.Should().Be("https://example.com/berlin.osm.pbf");
        metadata.ETag.Should().Be("\"abc\"");
        metadata.LastModified.Should().Be(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        metadata.ContentLength.Should().Be(42);
    }

    [Fact]
    public async Task GetMetadataAsync_FallsBackToRangedGet_WhenHeadIsNotSupported()
    {
        // Arrange
        using var handler = new StubHttpMessageHandler(HttpStatusCode.MethodNotAllowed);
        using var httpClient = new HttpClient(handler);
        var sut = new HttpRemotePbfMetadataReader(httpClient);

        // Act
        var metadata = await sut.GetMetadataAsync("https://example.com/berlin.osm.pbf");

        // Assert
        handler.RequestMethods.Should().Equal(HttpMethod.Head, HttpMethod.Get);
        handler.Range.Should().Be("bytes=0-0");
        metadata.ContentLength.Should().Be(42);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode? _headStatusCode;

        public StubHttpMessageHandler(HttpStatusCode? headStatusCode = null)
            => _headStatusCode = headStatusCode;

        public List<HttpMethod> RequestMethods { get; } = [];
        public string? RequestUri { get; private set; }
        public string? Range { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestMethods.Add(request.Method);
            RequestUri = request.RequestUri?.ToString();
            Range = request.Headers.Range?.ToString();

            if (request.Method == HttpMethod.Head && _headStatusCode.HasValue)
                return Task.FromResult(new HttpResponseMessage(_headStatusCode.Value));

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([])
            };
            response.Headers.ETag = new("\"abc\"");
            response.Content.Headers.LastModified = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
            response.Content.Headers.ContentLength = 42;

            return Task.FromResult(response);
        }
    }
}
