using System.Net;
using System.Net.Http.Headers;
using POIneer.Render.Application.Contracts;
using POIneer.Render.Application.Ports;

namespace POIneer.Render.Adapters.Input;

public sealed class HttpRemotePbfMetadataReader : IRemotePbfMetadataReader
{
    private readonly HttpClient _httpClient;

    public HttpRemotePbfMetadataReader(HttpClient httpClient)
        => _httpClient = httpClient;

    public async Task<RegionUpdateMetadata> GetMetadataAsync(
        string pbfUrl,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, pbfUrl);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);

        if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
            return await GetMetadataWithGetAsync(pbfUrl, ct);

        response.EnsureSuccessStatusCode();

        return ToMetadata(response);
    }

    private async Task<RegionUpdateMetadata> GetMetadataWithGetAsync(string pbfUrl, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pbfUrl);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);

        response.EnsureSuccessStatusCode();

        return ToMetadata(response);
    }

    private static RegionUpdateMetadata ToMetadata(HttpResponseMessage response)
        => new(
            response.Headers.ETag?.Tag,
            response.Content.Headers.LastModified,
            response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength);
}
