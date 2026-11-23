using System.Diagnostics;
using Airi.Domain;
using HtmlAgilityPack;
using static Airi.Web.MetadataHtml;

namespace Airi.Web;

public sealed class LibreDmmMetaSource : IWebVideoMetaSource
{
    private const string DetailReadySelector = "main .info:has(h1):has(.code-row .code), .detail-title, main h1.page-title";
    private const string SearchReadySelector = "main h1.page-title, a.movie-card-link, .card-group";
    private static readonly string[] ProductLabels = ["品番", "商品番号", "作品番号", "Product ID", "Product code", "ID"];
    private static readonly string[] DateLabels = ["発売日", "Release date", "Released"];
    private static readonly string[] StudioLabels = ["メーカー", "製作会社", "制作会社", "Studio", "Maker", "Producer"];
    private static readonly string[] ActorLabels = ["出演者", "出演", "女優", "俳優", "Cast", "Actors", "Actress"];
    private static readonly string[] GenreLabels = ["ジャンル", "Genre", "Genres"];
    private static readonly string[] TagLabels = ["タグ", "Tag", "Tags"];
    private static readonly string[] DescriptionLabels = ["あらすじ", "作品紹介", "作品説明", "商品説明", "Description", "Synopsis"];
    private readonly string _moviesUrl;
    private readonly Func<ISet<string>, CancellationToken, Task<IMetadataProviderTransport>> _openSession;

    public LibreDmmMetaSource(Uri moviesUrl) : this(moviesUrl, ChromiumMetadataTransport.OpenAsync)
    {
    }

    internal LibreDmmMetaSource(Uri moviesUrl, Func<ISet<string>, CancellationToken, Task<IMetadataProviderTransport>> openSession)
    {
        _moviesUrl = moviesUrl.AbsoluteUri.TrimEnd('/');
        Host = moviesUrl.Host;
        _openSession = openSession;
    }

    public VideoMetadataSourceKind Kind => VideoMetadataSourceKind.LibreDmm;
    public string Name => "LibreDMM";
    public string Host { get; }

    public async Task<WebVideoMetaResult> FetchAsync(
        MetadataProductCode code, bool includeThumbnail, ISet<string> blockedHosts, CancellationToken cancellationToken)
    {
        var encodedCode = Uri.EscapeDataString(code.Value);
        var directUri = new Uri($"{_moviesUrl}/{encodedCode}");
        cancellationToken.ThrowIfCancellationRequested();
        if (MetadataProviderLookup.RequestFailure(directUri, blockedHosts) is { } failure)
            return new WebVideoMetaResult(null, Detail: failure, Failed: true);

        IMetadataProviderTransport session;
        try
        {
            session = await _openSession(blockedHosts, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (ChromiumMetadataTransport.IsTransportFailure(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var detail = exception is ChromiumMetadataTransport.BrowserNotInstalledException
                ? "설치된 Chrome 또는 Edge를 찾을 수 없음"
                : "LibreDMM 조회용 Chromium을 시작할 수 없음";
            Trace.TraceWarning("{0} ({1}): {2}", detail, exception.GetType().Name, exception.Message);
            return new WebVideoMetaResult(null, Detail: detail, Failed: true);
        }

        await using (session)
        {
            var lookup = new MetadataProviderLookup(session, DetailReadySelector, SearchReadySelector);
            var page = await lookup.GetPageAsync(directUri, blockedHosts, cancellationToken).ConfigureAwait(false);
            var direct = ReadDetail(page, code);
            if (direct.Meta is not null || MetadataProviderLookup.RequestFailure(directUri, blockedHosts) is not null)
                return await lookup.CompleteAsync(direct, includeThumbnail, blockedHosts, cancellationToken).ConfigureAwait(false);

            var search = await lookup.SearchAsync(
                new Uri($"{_moviesUrl}?q={encodedCode}&style=prefix"), code, includeThumbnail, blockedHosts,
                FindCandidates, ReadDetail, cancellationToken, [directUri, page.Uri]).ConfigureAwait(false);
            return direct.Failed ? search with { Failed = true, Detail = JoinDetails(direct.Detail, search.Detail) } : search;
        }
    }

    private MetadataSearchResults? FindCandidates(MetadataPage page, MetadataProductCode code)
    {
        var cards = Nodes(page.Html, $"//a[{HasClass("movie-card-link")}] | //*[{HasClass("card-group")}]//a[.//h5 or .//*[{HasClass("card-title")}]] | //main//a[contains(@href, '/movies/')]")
            .ToArray();
        if (cards.Length == 0 && page.Html!.SelectSingleNode($"//*[{HasClass("card-group")}] | //main//h1[{HasClass("page-title")}]") is null && !IsNoResults(page.Html))
            return null;

        var matches = cards.Select(card =>
            {
                var shown = Text(card.SelectSingleNode($".//*[{HasClass("movie-id")} or {HasClass("code")}]"));
                var title = Text(card.SelectSingleNode($".//*[{HasClass("card-title")}] | .//h5"));
                if (shown.Length == 0 && code.Matches(title))
                    shown = title;
                return MetadataProviderLookup.CandidateLink(page, card, shown, code, Host);
            })
            .OfType<Uri>()
            .Distinct()
            .ToArray();
        return new MetadataSearchResults(matches, cards.Length > 0);
    }

    private static MetadataDetail ReadDetail(MetadataPage page, MetadataProductCode code)
    {
        if (page.Html is null)
            return new MetadataDetail(null, Detail: page.Failure ?? "직접 상세 없음", Failed: page.Failure is not null);

        var root = page.Html;
        var explicitHeading = root.SelectSingleNode($"//*[{HasClass("detail-title")}]");
        var h1 = explicitHeading?.Name == "h1"
            ? explicitHeading
            : explicitHeading?.Ancestors("h1").FirstOrDefault() ?? root.SelectSingleNode("//h1");
        var heading = explicitHeading ?? h1;
        var fields = Nodes(root, "//dl//dt")
            .Select(label => (Label: Label(label), Value: label.SelectSingleNode("following-sibling::*[1][self::dd]")))
            .Where(field => field.Value is not null)
            .ToArray();
        if (heading is null && fields.Length == 0)
            return new MetadataDetail(null, Detail: "LibreDMM 상세 응답 구조를 확인할 수 없음", Failed: true);

        IEnumerable<HtmlNode> Values(string[] labels) =>
            fields.Where(field => IsLabel(field.Label, labels)).Select(field => field.Value!);

        var codeFields = $".//*[{HasClass("movie-id")} or {HasClass("product-code")}]";
        var displayedCode = Nodes(root, $"//main//*[{HasClass("code-row")}]/*[{HasClass("code")}]");
        var productCodes = CleanList(Values(ProductLabels).Select(value => Text(value))
            .Concat(Nodes(h1, codeFields).Concat(Nodes(explicitHeading, codeFields)).Concat(displayedCode).Select(value => Text(value))));
        var firstHeadingSpan = h1?.SelectSingleNode("./span[1]");
        var headingCode = firstHeadingSpan == explicitHeading ? string.Empty : Text(firstHeadingSpan);
        if (MetadataProductCode.IsCodeShaped(headingCode))
            productCodes = [.. productCodes, headingCode];
        if (productCodes.Length == 0 || productCodes.Any(value => !code.Matches(value)))
            return new MetadataDetail(null, Detail: "LibreDMM 상세의 표시 품번이 일치하지 않음");

        var genreSection = root.SelectSingleNode("//*[@id='genres-h']/parent::section");
        var genres = CleanList(Values(GenreLabels).SelectMany(Keywords)
            .Concat(Nodes(genreSection, ".//a").Select(link => Text(link))));
        var tags = CleanList(Values(TagLabels).SelectMany(Keywords));
        var actors = CleanList(Nodes(root, $"//*[{HasClass("actress-grid")}]//a | //*[@id='cast-h']/parent::section//a")
            .Select(link => Text(link)));
        if (actors.Length == 0)
            actors = CleanList(Values(ActorLabels).SelectMany(Keywords));
        var actorNames = actors.Select(JapaneseActorNames.Parse).DistinctBy(actor => actor.OriginalName).ToArray();

        var descriptionFields = Values(DescriptionLabels).ToArray();
        var description = descriptionFields.Length > 0
            ? SingleValue(descriptionFields.Select(Description)) ?? string.Empty
            : Description(root.SelectSingleNode($"//*[{HasClass("movie-description")} or {HasClass("synopsis")}] | //*[@id='desc-h']/parent::section/*[{HasClass("description")}]"));
        var title = heading == h1 && heading is not null && code.Matches(headingCode)
            ? Text(string.Concat(heading.ChildNodes.Where(node => node != firstHeadingSpan).Select(node => node.InnerText)))
            : Title(Text(heading), productCodes[0]);
        var coverImage = root.SelectSingleNode($"//img[{HasClass("detail-cover")}] | //*[{HasClass("detail-cover")}]//img")
            ?? root.SelectSingleNode($"//main//figure[{HasClass("cover")}]//img | //*[{HasClass("col-md-8")}]//img[{HasClass("img-fluid")}]");
        var cover = MetadataProviderLookup.HttpsLink(page.Uri, coverImage?.GetAttributeValue("data-src", null)
            ?? coverImage?.GetAttributeValue("src", null));
        var meta = new VideoMeta(title, Date(SingleValue(Values(DateLabels).Select(value => Text(value))) ?? string.Empty),
            actorNames.Select(actor => actor.OriginalName).ToArray(), string.Empty, tags.Length > 0 ? tags : genres, description)
        {
            ActorNames = actorNames.Where(actor => actor.Reading is not null).ToArray(),
            Studio = SingleValue(Values(StudioLabels).Select(value => Text(value))),
            Genres = genres
        };
        return new MetadataDetail(meta, cover);
    }

    private static string? JoinDetails(string? first, string? second) =>
        first is null || second is null ? first ?? second : $"{first} / {second}";
}
