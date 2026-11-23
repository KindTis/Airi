using System.IO;
using System.Net;
using System.Text;
using Airi.Domain;
using HtmlAgilityPack;

namespace Airi.Web;

internal sealed record MetadataPage(Uri Uri, HtmlNode? Html, string? Failure = null);
internal sealed record MetadataDetail(VideoMeta? Meta, Uri? Cover = null, string? Detail = null, bool Failed = false);
internal sealed record MetadataSearchResults(IReadOnlyList<Uri> Matches, bool HasCards);

internal sealed record MetadataResource<T>(Uri Uri, T? Content = null, string? MediaType = null, string? Failure = null)
    where T : class;

internal interface IMetadataProviderTransport : IAsyncDisposable
{
    Task<MetadataResource<string>> GetHtmlAsync(Uri uri, ISet<string> blockedHosts, CancellationToken token,
        string? readySelector = null);
    Task<MetadataResource<byte[]>> GetCoverAsync(Uri uri, ISet<string> blockedHosts, CancellationToken token);
}

internal sealed class MetadataProviderLookup(
    IMetadataProviderTransport transport, string? detailReadySelector = null, string? searchReadySelector = null)
{
    public Task<MetadataPage> GetPageAsync(Uri uri, ISet<string> blockedHosts, CancellationToken token) =>
        GetPageAsync(uri, blockedHosts, token, detailReadySelector);

    private async Task<MetadataPage> GetPageAsync(
        Uri uri, ISet<string> blockedHosts, CancellationToken token, string? readySelector)
    {
        var response = await transport.GetHtmlAsync(uri, blockedHosts, token, readySelector);
        if (response.Content is null)
            return new MetadataPage(response.Uri, null, response.Failure);

        var html = LoadHtml(response.Content);
        if (IsChallenge(html))
        {
            RecordBlockedHost(blockedHosts, response.Uri.Host);
            return new MetadataPage(response.Uri, null, $"접근 확인 페이지 ({response.Uri.Host})");
        }

        return new MetadataPage(response.Uri, html);
    }

    public async Task<WebVideoMetaResult> SearchAsync(
        Uri searchUri,
        MetadataProductCode code,
        bool includeThumbnail,
        ISet<string> blockedHosts,
        Func<MetadataPage, MetadataProductCode, MetadataSearchResults?> findCandidates,
        Func<MetadataPage, MetadataProductCode, MetadataDetail> readDetail,
        CancellationToken token,
        HashSet<Uri>? visited = null)
    {
        var page = await GetPageAsync(searchUri, blockedHosts, token, searchReadySelector);
        if (page.Html is null)
            return new WebVideoMetaResult(null, Detail: page.Failure ?? "검색 결과 없음", Failed: page.Failure is not null);

        var candidates = findCandidates(page, code);
        if (candidates is null)
            return new WebVideoMetaResult(null, Detail: "검색 응답 구조를 확인할 수 없음", Failed: true);

        visited ??= [];
        MetadataDetail? confirmed = null;
        string? failure = null;
        foreach (var uri in candidates.Matches)
        {
            token.ThrowIfCancellationRequested();
            if (RequestFailure(uri, blockedHosts) is { } reason)
            {
                failure ??= reason;
                break;
            }
            if (!visited.Add(uri))
                continue;

            var detailPage = await GetPageAsync(uri, blockedHosts, token);
            if (detailPage.Uri != uri && !visited.Add(detailPage.Uri))
                continue;
            var detail = readDetail(detailPage, code);
            if (detail.Failed)
                failure ??= detail.Detail;
            if (detail.Meta is null)
                continue;

            if (confirmed is not null)
                return new WebVideoMetaResult(null, Detail: "같은 품번의 상세가 여러 개여서 판본을 확인할 수 없음");
            confirmed = detail;
        }

        if (failure is not null)
            return new WebVideoMetaResult(null, Detail: failure, Failed: true);
        if (confirmed is not null)
            return await CompleteAsync(confirmed, includeThumbnail, blockedHosts, token);

        var detailReason = candidates.Matches.Count > 0
            ? "검색 결과에서 동일 상세 품번을 확인할 수 없음"
            : candidates.HasCards ? "검색 목록에서 동일 품번을 확인할 수 없어 보류" : "검색 결과 없음";
        return new WebVideoMetaResult(null, Detail: detailReason);
    }

    public async Task<WebVideoMetaResult> CompleteAsync(
        MetadataDetail detail, bool includeThumbnail, ISet<string> blockedHosts, CancellationToken token)
    {
        if (!includeThumbnail || detail.Cover is null)
            return new WebVideoMetaResult(detail.Meta, Detail: detail.Detail, Failed: detail.Failed);

        var response = await transport.GetCoverAsync(detail.Cover, blockedHosts, token);
        if (response.Content is null || response.Content.Length == 0)
            return new WebVideoMetaResult(detail.Meta, Detail: response.Failure ?? "작품 표지를 내려받지 못함", Failed: true);

        if (string.Equals(response.MediaType, "text/html", StringComparison.OrdinalIgnoreCase))
        {
            if (IsChallenge(LoadHtml(Encoding.UTF8.GetString(response.Content))))
                RecordBlockedHost(blockedHosts, response.Uri.Host);
            return new WebVideoMetaResult(detail.Meta, Detail: $"표지 요청에 HTML 응답이 반환됨 ({response.Uri.Host})", Failed: true);
        }

        var extension = response.MediaType?.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            _ => Path.GetExtension(response.Uri.AbsolutePath)
        };
        return new WebVideoMetaResult(detail.Meta, response.Content, extension);
    }

    public static Uri? HttpsLink(Uri page, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(page, HtmlEntity.DeEntitize(value.Trim()), out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length > 0)
            return null;

        return uri.Fragment.Length == 0 ? uri : new UriBuilder(uri) { Fragment = string.Empty }.Uri;
    }

    public static Uri? CandidateLink(MetadataPage page, HtmlNode link, string? shownCode, MetadataProductCode expected, string catalogHost)
    {
        var uri = HttpsLink(page.Uri, link.GetAttributeValue("href", string.Empty));
        if (uri is null || !string.Equals(uri.Host, catalogHost, StringComparison.OrdinalIgnoreCase))
            return null;

        var hint = string.IsNullOrWhiteSpace(shownCode)
            ? Uri.UnescapeDataString(uri.AbsolutePath.TrimEnd('/').Split('/')[^1])
            : shownCode;
        return expected.Matches(hint) ? uri : null;
    }

    private static HtmlNode LoadHtml(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        return document.DocumentNode;
    }

    internal static bool IsChallengeDocument(string html) => IsChallenge(LoadHtml(html));

    private static bool IsChallenge(HtmlNode html)
    {
        if (html.SelectSingleNode("//*[@id='challenge-form' or @id='cf-challenge-running' or @id='challenge-stage']") is not null)
            return true;

        var title = MetadataHtml.Text(html.SelectSingleNode("//title"));
        // A regular document can load Cloudflare JavaScript before the catalogue is rendered.
        return MetadataHtml.IsLabel(title, "Just a moment...", "Attention Required! | Cloudflare", "Access Denied", "Too Many Requests")
            || html.InnerHtml.Contains("window._cf_chl_opt", StringComparison.Ordinal);
    }

    internal static string? RequestFailure(Uri uri, ISet<string> blockedHosts)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0)
            return "유효한 HTTPS 요청 주소를 확인할 수 없음";

        lock (blockedHosts)
            return blockedHosts.Contains(uri.Host) ? $"접근 제한으로 요청을 건너뜀 ({uri.Host})" : null;
    }

    internal static string? RecordAccessRestriction(Uri uri, HttpStatusCode status, bool challenge, ISet<string> blockedHosts)
    {
        if (status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
            or HttpStatusCode.Unauthorized or HttpStatusCode.UnavailableForLegalReasons)
        {
            RecordBlockedHost(blockedHosts, uri.Host);
            return $"HTTP {(int)status} 접근 제한 ({uri.Host})";
        }

        if (challenge)
        {
            RecordBlockedHost(blockedHosts, uri.Host);
            return $"접근 확인 페이지 ({uri.Host})";
        }

        return null;
    }

    private static void RecordBlockedHost(ISet<string> blockedHosts, string host)
    {
        lock (blockedHosts)
            blockedHosts.Add(host);
    }
}
