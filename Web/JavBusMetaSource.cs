using System.Diagnostics;
using Airi.Domain;
using HtmlAgilityPack;
using static Airi.Web.MetadataHtml;

namespace Airi.Web;

public sealed class JavBusMetaSource : IWebVideoMetaSource
{
    private static readonly string[] ProductLabels = ["識別碼", "识别码", "品番", "商品番号", "ID", "Product ID", "Product code"];
    private static readonly string[] DateLabels = ["發行日期", "发行日期", "發售日期", "発売日", "Release date"];
    private static readonly string[] StudioLabels = ["製作商", "制作商", "製作公司", "制作公司", "メーカー", "Studio", "Maker", "Producer"];
    private static readonly string[] ActorLabels = ["演員", "演员", "出演者", "Cast", "Actors", "Actress", "Stars"];
    private static readonly string[] GenreLabels = ["類別", "类别", "ジャンル", "Genre", "Genres"];
    private static readonly string[] TagLabels = ["標籤", "标签", "タグ", "Tag", "Tags"];
    private static readonly string[] DescriptionLabels = ["簡介", "简介", "內容介紹", "内容介绍", "作品紹介", "Description", "Synopsis"];
    private readonly string _catalogUrl;
    private readonly bool _uncensored;
    private readonly Func<ISet<string>, CancellationToken, Task<IMetadataProviderTransport>> _openSession;

    public JavBusMetaSource(Uri catalogBaseUrl, bool uncensored)
        : this(catalogBaseUrl, uncensored, ChromiumMetadataTransport.OpenAsync)
    {
    }

    internal JavBusMetaSource(Uri catalogBaseUrl, bool uncensored,
        Func<ISet<string>, CancellationToken, Task<IMetadataProviderTransport>> openSession)
    {
        _catalogUrl = catalogBaseUrl.AbsoluteUri.TrimEnd('/');
        Host = catalogBaseUrl.Host;
        _uncensored = uncensored;
        _openSession = openSession;
    }

    public VideoMetadataSourceKind Kind => _uncensored ? VideoMetadataSourceKind.JavBusUncensored : VideoMetadataSourceKind.JavBus;
    public string Name => _uncensored ? "JavBus Uncensored" : "JavBus";
    public string Host { get; }

    public async Task<WebVideoMetaResult> FetchAsync(
        MetadataProductCode code, bool includeThumbnail, ISet<string> blockedHosts, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var catalog = _uncensored ? "/uncensored" : string.Empty;
        var searchUri = new Uri($"{_catalogUrl}{catalog}/search/{Uri.EscapeDataString(code.Value)}");
        if (MetadataProviderLookup.RequestFailure(searchUri, blockedHosts) is { } failure)
            return new WebVideoMetaResult(null, Detail: failure, Failed: true);

        IMetadataProviderTransport session;
        try
        {
            session = await _openSession(blockedHosts, cancellationToken);
        }
        catch (Exception exception) when (ChromiumMetadataTransport.IsTransportFailure(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var detail = exception is ChromiumMetadataTransport.BrowserNotInstalledException
                ? "설치된 Chrome 또는 Edge를 찾을 수 없음"
                : "JavBus 조회용 Chromium을 시작할 수 없음";
            Trace.TraceWarning("{0} ({1}): {2}", detail, exception.GetType().Name, exception.Message);
            return new WebVideoMetaResult(null, Detail: detail, Failed: true);
        }

        WebVideoMetaResult result;
        await using (session)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lookup = new MetadataProviderLookup(session);
            result = await lookup.SearchAsync(
                searchUri, code, includeThumbnail, blockedHosts, FindCandidates, ReadDetail, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private MetadataSearchResults? FindCandidates(MetadataPage page, MetadataProductCode code)
    {
        var cards = Nodes(page.Html, $"//a[{HasClass("movie-box")}]").ToArray();
        if (cards.Length == 0 && page.Html!.SelectSingleNode("//*[@id='waterfall']") is null && !IsNoResults(page.Html))
            return null;

        var matches = cards.Select(card => MetadataProviderLookup.CandidateLink(
                page, card, Text(card.SelectSingleNode(".//date[1]")), code, Host))
            .OfType<Uri>()
            .Distinct()
            .ToArray();
        return new MetadataSearchResults(matches, cards.Length > 0);
    }

    private static MetadataDetail ReadDetail(MetadataPage page, MetadataProductCode code)
    {
        if (page.Html is null)
            return new MetadataDetail(null, Detail: page.Failure ?? "검색 상세 없음", Failed: page.Failure is not null);

        var root = page.Html;
        var info = root.SelectSingleNode($"//div[{HasClass("info")} and .//p/*[{HasClass("header")}]]");
        if (info is null)
            return new MetadataDetail(null, Detail: "JavBus 상세 응답 구조를 확인할 수 없음", Failed: true);

        var fields = Nodes(info, ".//p")
            .Select(paragraph => (Paragraph: paragraph, Header: paragraph.SelectSingleNode($"./*[{HasClass("header")}]")))
            .Where(field => field.Header is not null)
            .Select(field =>
            {
                var label = Label(field.Header);
                var value = field.Paragraph.CloneNode(true);
                value.SelectSingleNode($"./*[{HasClass("header")}]").Remove();
                return (Label: label, Value: value);
            })
            .ToArray();
        IEnumerable<HtmlNode> Values(string[] labels) =>
            fields.Where(field => IsLabel(field.Label, labels)).Select(field => field.Value);

        var productCodes = CleanList(Values(ProductLabels).Select(value => Text(value)));
        if (productCodes.Length == 0 || productCodes.Any(value => !code.Matches(value)))
            return new MetadataDetail(null, Detail: "JavBus 상세의 표시 품번이 일치하지 않음");

        var genreLinks = Nodes(info, $".//span[{HasClass("genre")}]//a").ToArray();
        bool IsStarLink(HtmlNode link)
        {
            var uri = MetadataProviderLookup.HttpsLink(page.Uri, link.GetAttributeValue("href", null));
            return uri is not null && uri.Host == page.Uri.Host
                && uri.AbsolutePath.Split('/').Contains("star", StringComparer.OrdinalIgnoreCase);
        }

        IEnumerable<string> TopicKeywords(HtmlNode value)
        {
            var links = Nodes(value, ".//a").ToArray();
            return links.Length > 0 ? links.Where(link => !IsStarLink(link)).Select(link => Text(link)) : Keywords(value);
        }

        var actors = CleanList(Nodes(root, "//*[@id='avatar-waterfall']//a").Select(link => Text(link)));
        if (actors.Length == 0)
            actors = CleanList(Values(ActorLabels).SelectMany(Keywords));
        if (actors.Length == 0)
            actors = CleanList(genreLinks.Where(IsStarLink).Select(link => Text(link)));

        var genres = CleanList(genreLinks.Where(link => !IsStarLink(link)).Select(link => Text(link))
            .Concat(Values(GenreLabels).SelectMany(TopicKeywords)));
        var tags = CleanList(Values(TagLabels).SelectMany(TopicKeywords));
        var description = SingleValue(Values(DescriptionLabels).Select(Description)) ?? string.Empty;
        var heading = root.SelectSingleNode($"//div[{HasClass("container")}]/h3") ?? root.SelectSingleNode("//h3");
        var anchor = root.SelectSingleNode($"//a[{HasClass("bigImage")}]");
        var coverImage = anchor?.SelectSingleNode(".//img") ?? root.SelectSingleNode($"//*[ {HasClass("screencap")} ]//img");
        var cover = MetadataProviderLookup.HttpsLink(page.Uri, anchor?.GetAttributeValue("href", null))
            ?? MetadataProviderLookup.HttpsLink(page.Uri, coverImage?.GetAttributeValue("src", null));

        var meta = new VideoMeta(Title(Text(heading), productCodes[0]),
            Date(SingleValue(Values(DateLabels).Select(value => Text(value))) ?? string.Empty),
            actors, string.Empty, tags.Length > 0 ? tags : genres, description)
        {
            Studio = SingleValue(Values(StudioLabels).Select(value => Text(value))),
            Genres = genres
        };
        return new MetadataDetail(meta, cover);
    }
}
