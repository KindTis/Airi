using System.IO;
using System.Net;
using System.Net.Http;
using Airi.Web;

namespace Airi.Tests;

public sealed class MetadataProviderTests
{
    private static readonly MetadataProductCode GeneralCode = new("ABP-088", false);
    private static readonly Uri LibreMoviesUrl = new("https://primary.example.test/movies");
    private static readonly Uri JavBusBaseUrl = new("https://catalog.example.test");
    private const string LibreDirect = "https://primary.example.test/movies/ABP-088";
    private const string LibreSearch = "https://primary.example.test/movies?q=ABP-088&style=prefix";
    private const string JavBusSearch = "https://catalog.example.test/search/ABP-088";
    private const string JavBusDetail = "https://catalog.example.test/ABP-088";

    [Fact]
    public async Task LibreDmm_RenderedDetailReadsCodeAndLabeledSections()
    {
        const string cover = "https://covers.example.test/aran117.jpg";
        using var http = new ScriptedHttp(
            ("https://primary.example.test/movies/ARAN-117", () => Html($"""
                <html><head><title>ARAN-117 · Catalogue</title></head><body><main>
                    <figure class="cover"><img src="{cover}"></figure>
                    <div class="info">
                        <div class="code-row"><span class="code big">ARAN-117</span></div>
                        <h1 class="title">Example work</h1>
                        <dl>
                            <div><dt>Released</dt><dd>2026-11-21</dd></div>
                            <div><dt>Maker</dt><dd><a>Example maker</a></dd></div>
                        </dl>
                    </div>
                    <section><h2 id="cast-h">Actresses</h2><ul class="cast"><li><a>Example actor</a></li></ul></section>
                    <section><h2 id="genres-h">Genres</h2><ul class="genres"><li><a>Drama</a></li></ul></section>
                    <section><h2 id="desc-h">Description</h2><p class="description">First line<br>Second line</p></section>
                    <script src="/cdn-cgi/challenge-platform/scripts/jsd/main.js"></script>
                </main></body></html>
                """)),
            (cover, () => new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg") }
                }
            }));

        var result = await new LibreDmmMetaSource(LibreMoviesUrl, http.OpenSessionAsync)
            .FetchAsync(new("ARAN-117", false), true, new HashSet<string>(), default);

        Assert.False(result.Failed);
        var meta = Assert.IsType<Airi.Domain.VideoMeta>(result.Meta);
        Assert.Equal("Example work", meta.Title);
        Assert.Equal(new DateOnly(2026, 11, 21), meta.Date);
        Assert.Equal("Example maker", meta.Studio);
        Assert.Equal(["Example actor"], meta.Actors);
        Assert.Equal(["Drama"], meta.Genres);
        Assert.Equal(["Drama"], meta.Tags);
        Assert.Equal("First line\nSecond line", meta.Description);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.ThumbnailBytes);
        Assert.Contains(".code-row .code", Assert.Single(http.ReadySelectors));
        Assert.Equal(1, http.OpenedSessions);
        Assert.Equal(1, http.DisposedSessions);
        http.AssertConsumed();
    }

    [Fact]
    public void ExternalConfiguration_LoadsAllConfiguredProviders()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                {
                  "libreDmmMoviesUrl": "https://configured-primary.example.test/title-catalog/",
                  "javBusBaseUrl": "https://configured-catalog.example.test/"
                }
                """);
            var sources = MetadataSourceConfiguration.Load(path);
            Assert.Collection(sources,
                source => Assert.Equal((VideoMetadataSourceKind.LibreDmm, "configured-primary.example.test"), (source.Kind, source.Host)),
                source => Assert.Equal((VideoMetadataSourceKind.JavBus, "configured-catalog.example.test"), (source.Kind, source.Host)),
                source => Assert.Equal((VideoMetadataSourceKind.JavBusUncensored, "configured-catalog.example.test"), (source.Kind, source.Host)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{")]
    [InlineData("""{"libreDmmMoviesUrl":"https://primary.example.test/movies"}""")]
    [InlineData("""{"libreDmmMoviesUrl":"/movies","javBusBaseUrl":"https://catalog.example.test"}""")]
    [InlineData("""{"libreDmmMoviesUrl":"https://primary.example.test/movies","javBusBaseUrl":"http://catalog.example.test"}""")]
    public void ExternalConfiguration_MissingOrInvalidFileLeavesWebProvidersDisabled(string? contents)
    {
        var path = Path.GetTempFileName();
        try
        {
            if (contents is null)
                File.Delete(path);
            else
                File.WriteAllText(path, contents);

            Assert.Empty(MetadataSourceConfiguration.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("""<h1 class="detail-title"><span>ABP-088</span> Example &amp; Title</h1>""", false)]
    [InlineData("""<h1><span>ABP-088</span><span class="detail-title">Example &amp; Title</span></h1>""", true)]
    [InlineData("""<h1><span>ABP-088</span></h1><div class="detail-title">Example &amp; Title</div>""", true)]
    public async Task LibreDmm_DirectDetailReturnsExplicitFieldsAndCover(string heading, bool imageHasCoverClass)
    {
        const string cover = "https://covers.example.test/abp088.jpg";
        var coverMarkup = imageHasCoverClass
            ? $"""<img class="detail-cover" src="{cover}">"""
            : $"""<div class="detail-cover"><img src="{cover}"></div>""";
        using var http = new ScriptedHttp(
            (LibreDirect, () => Html($"""
                {heading}
                {coverMarkup}
                <dl>
                    <dt>発売日</dt><dd>2024-01-02</dd>
                    <dt>配信開始日</dt><dd>2024-02-03</dd>
                    <dt>メーカー</dt><dd><a>Example Studio</a></dd>
                    <dt>ジャンル</dt><dd><a>Drama</a> <a>Documentary</a></dd>
                    <dt>タグ</dt><dd><a>Original tag</a></dd>
                    <dt>あらすじ</dt><dd>First paragraph<br>Second paragraph</dd>
                </dl>
                <div class="actress-grid"><a><img src="/actress.png"><span>First Actor</span></a></div>
                """)),
            (cover, () => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }));

        var result = await new LibreDmmMetaSource(LibreMoviesUrl, http.OpenSessionAsync).FetchAsync(GeneralCode, true, new HashSet<string>(), default);

        Assert.False(result.Failed);
        Assert.NotNull(result.Meta);
        Assert.Equal("Example & Title", result.Meta.Title);
        Assert.Equal(new DateOnly(2024, 1, 2), result.Meta.Date);
        Assert.Equal("Example Studio", result.Meta.Studio);
        Assert.Equal(new[] { "First Actor" }, result.Meta.Actors);
        Assert.Equal(new[] { "Drama", "Documentary" }, result.Meta.Genres);
        Assert.Equal(new[] { "Original tag" }, result.Meta.Tags);
        Assert.Equal("First paragraph\nSecond paragraph", result.Meta.Description);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.ThumbnailBytes);
        Assert.Equal(".jpg", result.ThumbnailExtension);
        http.AssertConsumed();
    }

    [Fact]
    public async Task LibreDmm_SearchRecoversWrongDirectDetailWithoutRequestingSimilarCards()
    {
        const string exact = "https://primary.example.test/movies/abp_088";
        using var http = new ScriptedHttp(
            (LibreDirect, () => Html(LibreDetail("ABP-088A", "Similar detail"))),
            (LibreSearch, () => Html("""
                <div class="card-group">
                    <a class="movie-card-link" href="/movies/ABP-088A"><h5 class="card-title"><span class="movie-id">ABP-088A</span> Variant</h5></a>
                    <a class="movie-card-link" href="/movies/ABP-88"><h5 class="card-title"><span class="movie-id">ABP-88</span> Similar</h5></a>
                    <a class="movie-card-link" href="/movies/abp_088"><h5 class="card-title"><span class="movie-id">abp_088</span> Match</h5></a>
                </div>
                """)),
            (exact, () => Html(LibreDetail("abp_088", "Confirmed title"))));

        var result = await new LibreDmmMetaSource(LibreMoviesUrl, http.OpenSessionAsync).FetchAsync(GeneralCode, false, new HashSet<string>(), default);

        Assert.False(result.Failed);
        Assert.Equal("Confirmed title", result.Meta?.Title);
        http.AssertConsumed();
    }

    [Fact]
    public async Task LibreDmm_SearchCardAndUrlDoNotProveDetailIdentity()
    {
        using var http = new ScriptedHttp(
            (LibreDirect, () => new(HttpStatusCode.NotFound)),
            (LibreSearch, () => Html("""
                <a class="movie-card-link" href="/movies/abp_088"><h5 class="card-title">ABP-088</h5></a>
                """)),
            ("https://primary.example.test/movies/abp_088", () => Html("""
                <h1 class="detail-title">ABP-088 mentioned in a long title</h1>
                <dl><dt>メーカー</dt><dd>Example Studio</dd></dl>
                """)));

        var result = await new LibreDmmMetaSource(LibreMoviesUrl, http.OpenSessionAsync).FetchAsync(GeneralCode, false, new HashSet<string>(), default);

        Assert.Null(result.Meta);
        Assert.False(result.Failed);
        Assert.Contains("품번", result.Detail);
        http.AssertConsumed();
    }

    [Fact]
    public async Task LibreDmm_ConflictingDisplayedCodesDoNotConfirmWork()
    {
        using var http = new ScriptedHttp(
            (LibreDirect, () => Html("""
                <h1><span>ABP-088A</span><span class="detail-title">Another edition</span></h1>
                <dl><dt>品番</dt><dd>ABP-088</dd></dl>
                """)),
            (LibreSearch, () => Html("<div class='card-group'></div>")));

        var result = await new LibreDmmMetaSource(LibreMoviesUrl, http.OpenSessionAsync)
            .FetchAsync(GeneralCode, false, new HashSet<string>(), default);

        Assert.Null(result.Meta);
        Assert.False(result.Failed);
        http.AssertConsumed();
    }

    [Fact]
    public async Task JavBus_MultipleConfirmedDetailsRemainAmbiguous()
    {
        using var http = new ScriptedHttp(
            (JavBusSearch, () => Html("""
                <div id="waterfall">
                    <a class="movie-box" href="/ABP-088"><date>ABP-088</date></a>
                    <a class="movie-box" href="/edition-2"><date>ABP-088</date></a>
                </div>
                """)),
            (JavBusDetail, () => Html(JavBusPage("ABP-088", "Edition One"))),
            ("https://catalog.example.test/edition-2", () => Html(JavBusPage("ABP-088", "Edition Two"))));

        var result = await new JavBusMetaSource(JavBusBaseUrl, uncensored: false, http.OpenSessionAsync)
            .FetchAsync(GeneralCode, false, new HashSet<string>(), default);

        Assert.Null(result.Meta);
        Assert.False(result.Failed);
        Assert.Contains("판본", result.Detail);
        http.AssertConsumed();
    }

    [Theory]
    [InlineData(false, "ABP-088", "abp_088", "/search/ABP-088")]
    [InlineData(true, "010203-004", "010203_004", "/uncensored/search/010203-004")]
    public async Task JavBus_UsesSelectedCatalogAndParsesDisplayedDetail(
        bool uncensored, string code, string shownCode, string searchPath)
    {
        var detail = "https://catalog.example.test/" + code;
        using var http = new ScriptedHttp(
            ("https://catalog.example.test" + searchPath, () => Html($"""
                <div id="waterfall"><a class="movie-box" href="/{code}">
                    <span>Search title</span><date>{code}</date><date>2023-09-09</date>
                </a></div>
                """)),
            (detail, () => Html(JavBusPage(shownCode, "Confirmed title"))));

        var result = await new JavBusMetaSource(JavBusBaseUrl, uncensored, http.OpenSessionAsync)
            .FetchAsync(new MetadataProductCode(code, uncensored), false, new HashSet<string>(), default);

        Assert.False(result.Failed);
        Assert.NotNull(result.Meta);
        Assert.Equal("Confirmed title", result.Meta.Title);
        Assert.Equal(new DateOnly(2024, 1, 2), result.Meta.Date);
        Assert.Equal("Example Studio", result.Meta.Studio);
        Assert.Equal(new[] { "Actor One" }, result.Meta.Actors);
        Assert.Equal(new[] { "Drama" }, result.Meta.Genres);
        Assert.Equal(new[] { "Drama" }, result.Meta.Tags);
        Assert.Null(result.ThumbnailBytes);
        http.AssertConsumed();
    }

    [Fact]
    public async Task JavBus_UsesStarLinksOnlyAsActorFallback()
    {
        using var http = new ScriptedHttp(
            (JavBusSearch, () => Html(JavBusCard())),
            (JavBusDetail, () => Html(JavBusPage("ABP-088", "Confirmed title", includeAvatar: false))));

        var result = await new JavBusMetaSource(JavBusBaseUrl, uncensored: false, http.OpenSessionAsync)
            .FetchAsync(GeneralCode, false, new HashSet<string>(), default);

        Assert.NotNull(result.Meta);
        Assert.Equal(new[] { "Actor Two" }, result.Meta.Actors);
        Assert.Equal(new[] { "Drama" }, result.Meta.Genres);
        Assert.Equal(new[] { "Drama" }, result.Meta.Tags);
        http.AssertConsumed();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task JavBus_DetailBlockIsSharedWithTheOtherCatalog(HttpStatusCode status)
    {
        using var http = new ScriptedHttp(
            (JavBusSearch, () => Html(JavBusCard())),
            (JavBusDetail, () => new(status)));
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var first = await new JavBusMetaSource(JavBusBaseUrl, uncensored: false, http.OpenSessionAsync)
            .FetchAsync(GeneralCode, false, blocked, default);
        var second = await new JavBusMetaSource(JavBusBaseUrl, uncensored: true, http.OpenSessionAsync)
            .FetchAsync(GeneralCode, false, blocked, default);

        Assert.True(first.Failed);
        Assert.Null(first.Meta);
        Assert.True(second.Failed);
        Assert.Contains("catalog.example.test", blocked);
        http.AssertConsumed();
    }

    [Fact]
    public async Task LibreDmm_ChallengeResponseStopsRetryOnTheHost()
    {
        using var http = new ScriptedHttp(
            (LibreDirect, () => Html("""
                <!doctype html><body><div id="challenge-form">Verify you are human</div></body>
                """)));
        var blocked = new HashSet<string>();

        var result = await new LibreDmmMetaSource(LibreMoviesUrl, http.OpenSessionAsync).FetchAsync(GeneralCode, false, blocked, default);

        Assert.True(result.Failed);
        Assert.Null(result.Meta);
        Assert.Contains("primary.example.test", blocked);
        http.AssertConsumed();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LibreDmm_CloudflareConfirmationStopsAfterOneRequest(bool useChallengeHeader)
    {
        using var http = new ScriptedHttp((LibreDirect, () =>
        {
            var response = Html(useChallengeHeader
                ? "<html><body>Verify</body></html>"
                : """
                    <!doctype html>
                    <html><head><meta charset="utf-8"></head><body><div></div>
                    <script>window._cf_chl_opt = {};</script></body></html>
                    """);
            response.Headers.Server.ParseAdd("cloudflare");
            if (useChallengeHeader)
                response.Headers.TryAddWithoutValidation("cf-mitigated", "challenge");
            return response;
        }));
        var blocked = new HashSet<string>();

        var result = await new LibreDmmMetaSource(LibreMoviesUrl, http.OpenSessionAsync).FetchAsync(GeneralCode, false, blocked, default);

        Assert.True(result.Failed);
        Assert.Null(result.Meta);
        Assert.Contains("접근 확인 페이지", result.Detail);
        Assert.Equal(new[] { "primary.example.test" }, blocked);
        http.AssertConsumed();
    }

    [Fact]
    public async Task LibreDmm_NormalDetailWithCloudflareScriptRemainsReadable()
    {
        using var http = new ScriptedHttp((LibreDirect, () =>
        {
            var response = Html(LibreDetail("ABP-088", "Confirmed title") + """
                <script src="https://static.cloudflareinsights.com/beacon.min.js"></script>
                <script src="/cdn-cgi/challenge-platform/scripts/jsd/main.js"></script>
                <script>var url = '/cdn-cgi/challenge-platform/example';</script>
                """);
            response.Headers.Server.ParseAdd("cloudflare");
            return response;
        }));
        var blocked = new HashSet<string>();

        var result = await new LibreDmmMetaSource(LibreMoviesUrl, http.OpenSessionAsync).FetchAsync(GeneralCode, false, blocked, default);

        Assert.False(result.Failed);
        Assert.Equal("Confirmed title", result.Meta?.Title);
        Assert.Empty(blocked);
        http.AssertConsumed();
    }

    [Fact]
    public async Task JavBus_CoverLimitRecordsOnlyCoverHostAndPreservesDetail()
    {
        const string cover = "https://covers.example.test/abp088.jpg";
        using var http = new ScriptedHttp(
            (JavBusSearch, () => Html(JavBusCard())),
            (JavBusDetail, () => Html(JavBusPage("ABP-088", "Title"))),
            (cover, () => new(HttpStatusCode.TooManyRequests)));
        var blocked = new HashSet<string>();

        var result = await new JavBusMetaSource(JavBusBaseUrl, uncensored: false, http.OpenSessionAsync)
            .FetchAsync(GeneralCode, true, blocked, default);

        Assert.True(result.Failed);
        Assert.Equal("Title", result.Meta?.Title);
        Assert.Null(result.ThumbnailBytes);
        Assert.Equal(new[] { "covers.example.test" }, blocked);
        http.AssertConsumed();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JavBus_UsesOneSessionForSearchDetailAndCover(bool coverFails)
    {
        const string cover = "https://covers.example.test/abp088.jpg";
        using var http = new ScriptedHttp(
            (JavBusSearch, () => Html(JavBusCard())),
            (JavBusDetail, () => Html(JavBusPage("ABP-088", "Title"))),
            (cover, () => coverFails
                ? throw new IOException()
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }));
        var blocked = new HashSet<string>();

        var result = await new JavBusMetaSource(JavBusBaseUrl, false, http.OpenSessionAsync)
            .FetchAsync(GeneralCode, true, blocked, default);

        Assert.Equal("Title", result.Meta?.Title);
        Assert.Equal(coverFails, result.Failed);
        if (coverFails)
            Assert.Null(result.ThumbnailBytes);
        else
        {
            Assert.Equal(new byte[] { 1, 2, 3 }, result.ThumbnailBytes);
            Assert.Equal(".jpg", result.ThumbnailExtension);
        }
        Assert.Equal(1, http.OpenedSessions);
        Assert.Equal(1, http.DisposedSessions);
        http.AssertConsumed();
    }

    [Fact]
    public async Task JavBus_CancellationDisposesSessionAndStopsBeforeDetail()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = new ScriptedHttp((JavBusSearch, () =>
        {
            cancellation.Cancel();
            return Html(JavBusCard());
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new JavBusMetaSource(JavBusBaseUrl, false, http.OpenSessionAsync)
                .FetchAsync(GeneralCode, true, new HashSet<string>(), cancellation.Token));

        Assert.Equal(1, http.OpenedSessions);
        Assert.Equal(1, http.DisposedSessions);
        http.AssertConsumed();
    }

    [Fact]
    public async Task JavBus_BlockedCatalogSkipsOpeningSession()
    {
        using var http = new ScriptedHttp();
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "catalog.example.test" };

        var result = await new JavBusMetaSource(JavBusBaseUrl, true, http.OpenSessionAsync)
            .FetchAsync(GeneralCode, true, blocked, default);

        Assert.True(result.Failed);
        Assert.Equal(0, http.OpenedSessions);
    }

    [Theory]
    [InlineData("https://catalog.example.test/search/ABP-088", false, true)]
    [InlineData("https://blocked.example.test/redirect", false, false)]
    [InlineData("https://blocked.example.test/script.js", false, false)]
    [InlineData("http://assets.example.test/script.js", false, false)]
    [InlineData("https://assets.example.test/script.js", true, false)]
    public async Task Chromium_RequestGateControlsContinueAndAbort(string url, bool cancel, bool allowed)
    {
        using var cancellation = new CancellationTokenSource();
        if (cancel)
            cancellation.Cancel();
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "blocked.example.test" };
        var continued = 0;
        var aborted = 0;

        await ChromiumMetadataTransport.RouteRequestAsync(url, blocked, cancellation.Token, () =>
        {
            continued++;
            return Task.CompletedTask;
        }, _ =>
        {
            aborted++;
            return Task.CompletedTask;
        });

        Assert.Equal(allowed ? 1 : 0, continued);
        Assert.Equal(allowed ? 0 : 1, aborted);
    }

    [Fact]
    public async Task LibreDmm_DistinguishesEmptySearchAndMismatchedCardsFromUnrecognizedHtml()
    {
        static ScriptedHttp WithSearch(string searchHtml) => new(
            (LibreDirect, () => new(HttpStatusCode.NotFound)),
            (LibreSearch, () => Html(searchHtml)));
        using var empty = WithSearch("""<div class="card-group"></div><p>検索結果がありません</p>""");
        using var variants = WithSearch("""
            <a class="movie-card-link" href="/movies/ABP-088A"><span class="movie-id">ABP-088A</span></a>
            """);
        using var unknown = WithSearch("<html><body>Unexpected response</body></html>");

        var miss = await new LibreDmmMetaSource(LibreMoviesUrl, empty.OpenSessionAsync).FetchAsync(GeneralCode, false, new HashSet<string>(), default);
        var mismatch = await new LibreDmmMetaSource(LibreMoviesUrl, variants.OpenSessionAsync).FetchAsync(GeneralCode, false, new HashSet<string>(), default);
        var failure = await new LibreDmmMetaSource(LibreMoviesUrl, unknown.OpenSessionAsync).FetchAsync(GeneralCode, false, new HashSet<string>(), default);

        Assert.Null(miss.Meta);
        Assert.False(miss.Failed);
        Assert.Equal("검색 결과 없음", miss.Detail);
        Assert.Null(mismatch.Meta);
        Assert.False(mismatch.Failed);
        Assert.Contains("보류", mismatch.Detail);
        Assert.Null(failure.Meta);
        Assert.True(failure.Failed);
        empty.AssertConsumed();
        variants.AssertConsumed();
        unknown.AssertConsumed();
    }

    private static HttpResponseMessage Html(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "text/html")
    };

    private static string LibreDetail(string code, string title) => $"""
        <h1 class="detail-title"><span>{code}</span> {title}</h1>
        """;

    private static string JavBusCard() => """
        <div id="waterfall"><a class="movie-box" href="/ABP-088"><date>ABP-088</date></a></div>
        """;

    private static string JavBusPage(string code, string title, bool includeAvatar = true) => $"""
        <h3>{title}</h3>
        <a class="bigImage" href="https://covers.example.test/abp088.jpg"><img src="/preview.jpg"></a>
        <div class="info">
            <p><span class="header">識別碼:</span> <span>{code}</span></p>
            <p><span class="header">發行日期:</span> 2024-01-02</p>
            <p><span class="header">製作商:</span> <a>Example Studio</a></p>
            <p><span class="header">系列:</span> <a>Not a studio</a></p>
            <p><span class="header">類別:</span></p>
            <p><span class="genre"><a>Drama</a></span></p>
            <p><span class="genre"><a href="/star/actor-two">Actor Two</a></span></p>
            <p><span class="header">標籤:</span><a href="/star/actor-two">Actor Two</a></p>
            <p><span class="genre"><label><input type="checkbox">Not a genre</label></span></p>
        </div>
        """ + (includeAvatar
        ? """<div id="avatar-waterfall"><a class="avatar-box"><span>Actor One</span></a></div>"""
        : string.Empty);

    private sealed class ScriptedHttp : HttpMessageHandler
    {
        private readonly Queue<(string Uri, Func<HttpResponseMessage> Response)> _expected;

        public ScriptedHttp(params (string Uri, Func<HttpResponseMessage> Response)[] expected)
        {
            _expected = new Queue<(string, Func<HttpResponseMessage>)>(expected);
            Client = new HttpClient(this, disposeHandler: false);
        }

        public HttpClient Client { get; }
        public int OpenedSessions { get; private set; }
        public int DisposedSessions { get; private set; }
        public List<string?> ReadySelectors { get; } = new();

        public Task<IMetadataProviderTransport> OpenSessionAsync(ISet<string> blockedHosts, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            OpenedSessions++;
            return Task.FromResult<IMetadataProviderTransport>(new Session(this));
        }

        public void AssertConsumed() => Assert.Empty(_expected);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.NotEmpty(_expected);
            var (uri, response) = _expected.Dequeue();
            Assert.Equal(uri, request.RequestUri!.AbsoluteUri);
            var message = response();
            message.RequestMessage = request;
            return Task.FromResult(message);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Client.Dispose();
            base.Dispose(disposing);
        }

        private sealed class Session(ScriptedHttp owner) : IMetadataProviderTransport
        {
            private readonly MetadataProviderHttpFixture _transport = new(owner.Client);

            public Task<MetadataResource<string>> GetHtmlAsync(Uri uri, ISet<string> blockedHosts, CancellationToken token,
                string? readySelector = null)
            {
                owner.ReadySelectors.Add(readySelector);
                return _transport.GetHtmlAsync(uri, blockedHosts, token);
            }

            public Task<MetadataResource<byte[]>> GetCoverAsync(Uri uri, ISet<string> blockedHosts, CancellationToken token) =>
                _transport.GetCoverAsync(uri, blockedHosts, token);

            public ValueTask DisposeAsync()
            {
                owner.DisposedSessions++;
                return ValueTask.CompletedTask;
            }
        }
    }
}
