using System.IO;
using System.Net;
using System.Net.Http;

namespace Airi.Web;

internal sealed class MetadataProviderHttpFixture(HttpClient client) : IMetadataProviderTransport
{
    private const int MaxRedirects = 5;

    public Task<MetadataResource<string>> GetHtmlAsync(Uri uri, ISet<string> blockedHosts, CancellationToken token,
        string? readySelector = null) =>
        GetAsync(uri, blockedHosts, static (content, cancellation) => content.ReadAsStringAsync(cancellation), token);

    public Task<MetadataResource<byte[]>> GetCoverAsync(Uri uri, ISet<string> blockedHosts, CancellationToken token) =>
        GetAsync(uri, blockedHosts, static (content, cancellation) => content.ReadAsByteArrayAsync(cancellation), token);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<MetadataResource<T>> GetAsync<T>(
        Uri uri, ISet<string> blockedHosts, Func<HttpContent, CancellationToken, Task<T>> read, CancellationToken token)
        where T : class
    {
        var visited = new HashSet<Uri>();
        for (var redirect = 0; redirect <= MaxRedirects; redirect++)
        {
            token.ThrowIfCancellationRequested();
            if (MetadataProviderLookup.RequestFailure(uri, blockedHosts) is { } preflightFailure)
                return new MetadataResource<T>(uri, Failure: preflightFailure);
            if (!visited.Add(uri))
                break;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await client.SendAsync(request, token);
                token.ThrowIfCancellationRequested();
                uri = response.RequestMessage?.RequestUri ?? uri;

                var challenge = response.Headers.TryGetValues("cf-mitigated", out var mitigation)
                    && mitigation.Contains("challenge", StringComparer.OrdinalIgnoreCase);
                if (MetadataProviderLookup.RecordAccessRestriction(uri, response.StatusCode, challenge, blockedHosts) is { } failure)
                    return new MetadataResource<T>(uri, Failure: failure);

                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                    return new MetadataResource<T>(uri);

                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                    or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    var location = MetadataProviderLookup.HttpsLink(uri, response.Headers.Location?.OriginalString);
                    if (location is null)
                        return new MetadataResource<T>(uri, Failure: $"이동할 HTTPS 주소를 확인할 수 없음 ({uri.Host})");
                    uri = location;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    return new MetadataResource<T>(uri, Failure: $"HTTP {(int)response.StatusCode} 응답 ({uri.Host})");

                var content = await read(response.Content, token);
                token.ThrowIfCancellationRequested();
                return new MetadataResource<T>(uri, content, response.Content.Headers.ContentType?.MediaType);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return new MetadataResource<T>(uri, Failure: $"응답 시간 초과 ({uri.Host})");
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                token.ThrowIfCancellationRequested();
                return new MetadataResource<T>(uri, Failure: $"HTTP 응답을 읽을 수 없음 ({uri.Host})");
            }
        }

        return new MetadataResource<T>(uri, Failure: $"응답 이동 한도 초과 또는 순환 이동 ({uri.Host})");
    }
}
