using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Airi.Domain;
using Airi.Infrastructure;
using Airi.Services;
using Airi.Web;
using Xunit;

namespace Airi.Tests;

public sealed class WebMetadataServiceTests : IDisposable
{
    private static readonly VideoMeta EmptyMeta = new("", null, Array.Empty<string>(), "", Array.Empty<string>(), "");
    private readonly string _cacheRoot = Path.Combine(AppContext.BaseDirectory, $"MetadataService_{Guid.NewGuid():N}");
    private readonly List<VideoMetadataSourceKind> _calls = new();

    [Fact]
    public async Task FetchEnrichmentAsync_UpdatesUnlockedTitleAndSkipsJavBusWhenLibreDmmIsComplete()
    {
        var service = Service(
            Source(VideoMetadataSourceKind.JavBus, new(null)),
            Source(VideoMetadataSourceKind.LibreDmm, new(
                CompleteMeta, CoverBytes(), ".jpg")));
        var original = Entry(EmptyMeta with { Title = "ABP-088" });

        var result = await service.FetchEnrichmentAsync(original, CancellationToken.None);

        Assert.Equal(MetadataLookupOutcome.Found, result.Status);
        Assert.Equal(CompleteMeta.Title, result.Entry!.Meta.Title);
        Assert.Equal(original.Id, result.Entry.Id);
        Assert.Equal(new[] { "Actress" }, result.Entry.Meta.Actors);
        Assert.Equal("LibreDMM", result.FieldSources[MetadataField.Title]);
        Assert.Equal("LibreDMM", result.FieldSources[MetadataField.Thumbnail]);
        Assert.True(File.Exists(LibraryPathHelper.ResolveToAbsolute(result.Entry.Meta.Thumbnail)));
        Assert.Equal(new[] { VideoMetadataSourceKind.LibreDmm }, _calls);
    }

    [Fact]
    public async Task FetchEnrichmentAsync_PreservesFirstGenreAndTagsAndRetainsActorsWhenCoverIsCorrupt()
    {
        var service = Service(
            Source(VideoMetadataSourceKind.LibreDmm, new(CompleteMeta with
            {
                Actors = Array.Empty<string>(),
                Genres = new[] { "First genre" },
                Tags = Array.Empty<string>()
            })),
            Source(VideoMetadataSourceKind.JavBus, new(CompleteMeta with
            {
                Actors = new[] { "Fallback actor" },
                Genres = new[] { "Later genre" },
                Tags = new[] { "Later tag" }
            }, new byte[] { 1, 2, 3 }, ".jpg")),
            Source(VideoMetadataSourceKind.JavBusUncensored, new(CompleteMeta, CoverBytes(), ".jpg")));

        var result = await service.FetchEnrichmentAsync(Entry(), CancellationToken.None);

        Assert.Equal(MetadataLookupOutcome.Partial, result.Status);
        Assert.Equal(new[] { "First genre" }, result.Entry!.Meta.Genres);
        Assert.Equal(new[] { "First genre" }, result.Entry.Meta.Tags);
        Assert.Equal(new[] { "Fallback actor" }, result.Entry.Meta.Actors);
        Assert.Equal("", result.Entry.Meta.Thumbnail);
        Assert.Equal("LibreDMM", result.FieldSources[MetadataField.Genres]);
        Assert.Equal("LibreDMM", result.FieldSources[MetadataField.Tags]);
        Assert.Equal("JavBus", result.FieldSources[MetadataField.Actors]);
        Assert.False(result.FieldSources.ContainsKey(MetadataField.Thumbnail));
        Assert.Empty(Directory.GetFiles(Path.Combine(_cacheRoot, "cache")));
        Assert.Equal(new[] { VideoMetadataSourceKind.LibreDmm, VideoMetadataSourceKind.JavBus }, _calls);
    }

    [Theory]
    [InlineData("JUR-459.mp4", VideoMetadataSourceKind.JavBus, VideoMetadataSourceKind.JavBusUncensored)]
    [InlineData("HEYZO-1234.mp4", VideoMetadataSourceKind.JavBusUncensored, VideoMetadataSourceKind.JavBus)]
    public async Task FetchEnrichmentAsync_UsesCodeFormatForFallbackOrder(
        string fileName, VideoMetadataSourceKind preferred, VideoMetadataSourceKind last)
    {
        var service = Service(
            Source(last, new(CompleteMeta, CoverBytes(), ".jpg")),
            Source(preferred, new(null)),
            Source(VideoMetadataSourceKind.LibreDmm, new(null)));

        var result = await service.FetchEnrichmentAsync(Entry(fileName: fileName), CancellationToken.None);

        Assert.Equal(MetadataLookupOutcome.Found, result.Status);
        Assert.Equal(new[] { VideoMetadataSourceKind.LibreDmm, preferred, last }, _calls);
    }

    [Theory]
    [InlineData(false, false, MetadataLookupOutcome.NotFound)]
    [InlineData(false, true, MetadataLookupOutcome.Unchanged)]
    [InlineData(true, false, MetadataLookupOutcome.Failed)]
    public async Task FetchEnrichmentAsync_DescriptionOnlyStopsAfterLibreDmm(
        bool failed, bool matched, MetadataLookupOutcome expected)
    {
        var original = Entry(CompleteMeta with { Description = "", Thumbnail = "existing-cover.jpg",
            ManualFieldLocks = Enum.GetValues<MetadataField>().Where(field => field != MetadataField.Description).ToArray() });
        var service = Service(
            Source(VideoMetadataSourceKind.LibreDmm, new(matched ? EmptyMeta : null, Failed: failed)),
            Source(VideoMetadataSourceKind.JavBus, new(CompleteMeta)));

        var result = await service.FetchEnrichmentAsync(original, CancellationToken.None);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Entry);
        Assert.Equal(new[] { VideoMetadataSourceKind.LibreDmm }, _calls);
    }

    [Fact]
    public async Task FetchEnrichmentAsync_AllLockedOrUnidentifiedEntryMakesNoRequests()
    {
        var service = Service(Source(VideoMetadataSourceKind.LibreDmm, new(CompleteMeta)));

        var complete = await service.FetchEnrichmentAsync(Entry(CompleteMeta with { ManualFieldLocks = Enum.GetValues<MetadataField>() }), CancellationToken.None);
        var unidentified = await service.FetchEnrichmentAsync(Entry(fileName: "ABP-088A.mp4"), CancellationToken.None);

        Assert.Equal(MetadataLookupOutcome.Unchanged, complete.Status);
        Assert.Equal(MetadataLookupOutcome.Unidentified, unidentified.Status);
        Assert.Empty(_calls);
    }

    [Theory]
    [InlineData(false, MetadataLookupOutcome.Failed)]
    [InlineData(true, MetadataLookupOutcome.Partial)]
    public async Task FetchEnrichmentAsync_HostRestrictionSkipsLastPathButPreservesPreviousFields(
        bool hasFirstCandidate, MetadataLookupOutcome expected)
    {
        var service = Service(
            Source(VideoMetadataSourceKind.LibreDmm, new(hasFirstCandidate ? EmptyMeta with { Studio = "First studio" } : null)),
            SourceWith(VideoMetadataSourceKind.JavBus, (_, _, blockedHosts, _) =>
            {
                blockedHosts.Add("catalog.example.test");
                return Task.FromResult(new WebVideoMetaResult(null, Detail: "요청 제한", Failed: true));
            }),
            Source(VideoMetadataSourceKind.JavBusUncensored, new(CompleteMeta)));

        var result = await service.FetchEnrichmentAsync(Entry(), CancellationToken.None);

        Assert.Equal(expected, result.Status);
        Assert.Contains("요청 제한", result.Detail);
        Assert.Equal(hasFirstCandidate ? "First studio" : null, result.Entry?.Meta.Studio);
        Assert.Equal(new[] { VideoMetadataSourceKind.LibreDmm, VideoMetadataSourceKind.JavBus }, _calls);
    }

    [Fact]
    public async Task FetchEnrichmentAsync_ProviderTimeoutAllowsOtherHostToProvideCandidate()
    {
        var service = Service(
            SourceWith(VideoMetadataSourceKind.LibreDmm, (_, _, _, _) => throw new TaskCanceledException()),
            Source(VideoMetadataSourceKind.JavBus, new(EmptyMeta with { Studio = "Fallback studio" })));

        var result = await service.FetchEnrichmentAsync(Entry(), CancellationToken.None);

        Assert.Equal(MetadataLookupOutcome.Partial, result.Status);
        Assert.Equal("Fallback studio", result.Entry!.Meta.Studio);
        Assert.Contains("시간 초과", result.Detail);
    }

    [Fact]
    public async Task FetchEnrichmentAsync_UpdatesUnlockedValuesAndPreservesLockedOrMissingValues()
    {
        var translation = new StubTranslationService(_ => Task.FromResult<string?>("Translated"));
        var service = Service(translation, Source(VideoMetadataSourceKind.LibreDmm, new(CompleteMeta)));
        var original = Entry(EmptyMeta with
        {
            Title = "Existing title",
            RawDate = "existing raw date",
            Description = "Existing description",
            Thumbnail = "existing.jpg",
            ManualFieldLocks = new[] { MetadataField.Studio, MetadataField.Tags }
        });

        var result = await service.FetchEnrichmentAsync(original, CancellationToken.None);

        Assert.Equal(MetadataLookupOutcome.Partial, result.Status);
        Assert.Equal(CompleteMeta.Title, result.Entry!.Meta.Title);
        Assert.Equal(CompleteMeta.Date, result.Entry.Meta.Date);
        Assert.Null(result.Entry.Meta.RawDate);
        Assert.Equal(CompleteMeta.Description, result.Entry.Meta.Description);
        Assert.Equal("Translated", result.Entry.Meta.Korean?.Description);
        Assert.Equal("existing.jpg", result.Entry.Meta.Thumbnail);
        Assert.Null(result.Entry.Meta.Studio);
        Assert.Empty(result.Entry.Meta.Tags);
        Assert.Equal(original.Meta.ManualFieldLocks, result.Entry.Meta.ManualFieldLocks);
        Assert.Equal(1, translation.CallCount);
    }

    [Theory]
    [InlineData(false, "Translated description")]
    [InlineData(true, "First description")]
    public async Task FetchEnrichmentAsync_TranslatesSelectedDescriptionOnceAndPreservesItOnTimeout(bool timeout, string expected)
    {
        var translation = new StubTranslationService(_ => timeout
            ? throw new TaskCanceledException()
            : Task.FromResult<string?>("Translated description"));
        var service = Service(translation,
            Source(VideoMetadataSourceKind.LibreDmm, new(EmptyMeta with { Description = "First description" })),
            Source(VideoMetadataSourceKind.JavBus, new(CompleteMeta with { Description = "Second description" })));

        var result = await service.FetchEnrichmentAsync(Entry(), CancellationToken.None);

        Assert.Equal(MetadataLookupOutcome.Partial, result.Status);
        Assert.Equal("First description", result.Entry!.Meta.Description);
        Assert.Equal(expected, result.Entry.Meta.Korean?.Description ?? result.Entry.Meta.Description);
        Assert.Equal(new[] { "Actress" }, result.Entry.Meta.Actors);
        Assert.Equal(1, translation.CallCount);
        Assert.Equal("First description", translation.Input);
        Assert.Equal("KO", translation.Target);
    }

    [Fact]
    public async Task FetchEnrichmentAsync_KeepsOriginalsAndRestoresNamesAndEnglishTokens()
    {
        const string name = "一二三ゆぅり";
        var actor = JapaneseActorNames.Parse("一 一二三ゆぅり うたかねゆぅり");
        var original = CompleteMeta with
        {
            Title = "捜査官 " + name, Actors = [name], ActorNames = [actor], Description = "説明",
            Genres = ["4K", "拘束", "Blu-ray（ブルーレイ）", "4K対応"], Tags = ["4K", "拘束"]
        };
        var translation = new LocalizationTranslation();
        var service = Service(translation, Source(VideoMetadataSourceKind.LibreDmm, new(original)));
        var result = await service.FetchEnrichmentAsync(Entry(), CancellationToken.None);

        Assert.Equal("ABP-088", result.Entry!.ProductCode);
        Assert.Equal(original.Title, result.Entry.Meta.Title);
        Assert.Equal(original.Description, result.Entry.Meta.Description);
        Assert.Equal(original.Genres, result.Entry.Meta.Genres);
        Assert.Equal(name, actor.OriginalName);
        Assert.Equal("うたかねゆぅり", actor.Reading);
        Assert.Equal("우타카네 유우리", actor.KoreanName);
        var korean = Assert.IsType<KoreanMetadata>(result.Entry.Meta.Korean);
        Assert.Equal("수사관 우타카네 유우리", korean.Title);
        Assert.Equal("구속", korean.Genres!["拘束"]);
        Assert.Equal("구속", korean.Tags!["拘束"]);
        Assert.Equal("Blu-ray(블루레이)", korean.Genres["Blu-ray（ブルーレイ）"]);
        Assert.False(korean.Genres.ContainsKey("4K"));
        Assert.False(korean.Genres.ContainsKey("4K対応"));
        Assert.Single(translation.Requests, item => item == "拘束");
        Assert.DoesNotContain("4K", translation.Requests);
        Assert.Equal("설명", korean.Description);
    }

    [Fact]
    public async Task TranslateDescriptionAsync_TranslatesCurrentInputAndPreservesOtherFields()
    {
        var translation = new StubTranslationService(_ => Task.FromResult<string?>("Translated description"));
        var service = Service(translation);
        var current = CompleteMeta with { Description = "Edited current description", Thumbnail = "cover.jpg" };

        var result = await service.TranslateDescriptionAsync(current, CancellationToken.None);

        Assert.Equal("Edited current description", translation.Input);
        Assert.Equal(current with { Description = "Translated description" }, result);
    }

    [Fact]
    public async Task TranslateDescriptionAsync_WhenTranslatorDisabledReturnsOriginalDescription()
    {
        var service = Service();
        var result = await service.TranslateDescriptionAsync(CompleteMeta, CancellationToken.None);

        Assert.Same(CompleteMeta, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Changed studio")]
    public async Task FetchEnrichmentAsync_ExcludesLockedStudioAndReusesIdenticalCover(string? changedStudio)
    {
        Directory.CreateDirectory(_cacheRoot);
        var existingPath = Path.Combine(_cacheRoot, "existing.jpg");
        var cover = CoverBytes();
        await File.WriteAllBytesAsync(existingPath, cover);
        var original = Entry(CompleteMeta with
        {
            Thumbnail = existingPath,
            ManualFieldLocks = new[] { MetadataField.Studio }
        });
        var service = Service(Source(VideoMetadataSourceKind.LibreDmm, new(
            CompleteMeta with
            {
                Studio = changedStudio ?? CompleteMeta.Studio,
                Actors = new[] { "Actress" },
                Genres = new[] { "Genre" }
            },
            cover, ".jpg")));

        var result = await service.FetchEnrichmentAsync(original, CancellationToken.None);

        Assert.Equal(MetadataLookupOutcome.Found, result.Status);
        Assert.NotNull(result.Entry);
        Assert.Equal(CompleteMeta.Studio, result.Entry.Meta.Studio);
        Assert.Equal(CompleteMeta.Title, result.Entry.Meta.Title);
        Assert.Equal(new[] { "Actress" }, result.Entry.Meta.Actors);
        Assert.Equal(existingPath, result.Entry.Meta.Thumbnail);
        Assert.Equal(Enum.GetValues<MetadataField>().Length - 1, result.FieldSources.Count);
        Assert.False(result.FieldSources.ContainsKey(MetadataField.Studio));
        Assert.Empty(Directory.GetFiles(Path.Combine(_cacheRoot, "cache")));
        Assert.True(File.Exists(existingPath));
    }

    [Fact]
    public async Task FetchEnrichmentAsync_LateTranslationAfterUserCancellationDiscardsOwnedCover()
    {
        var enteredTranslation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTranslation = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var translation = new StubTranslationService(_ =>
        {
            enteredTranslation.TrySetResult();
            return releaseTranslation.Task;
        });
        var service = Service(translation, Source(VideoMetadataSourceKind.LibreDmm, new(CompleteMeta, CoverBytes(), ".jpg")));
        using var cancellation = new CancellationTokenSource();
        var pending = service.FetchEnrichmentAsync(Entry(), cancellation.Token);

        await enteredTranslation.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(Directory.GetFiles(Path.Combine(_cacheRoot, "cache")));
        cancellation.Cancel();
        releaseTranslation.SetResult("Late translated description");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(Directory.GetFiles(Path.Combine(_cacheRoot, "cache")));
    }

    private static VideoMeta CompleteMeta { get; } = new("Work title", new DateOnly(2024, 1, 2),
        new[] { "Actress" }, "", new[] { "Genre" }, "Work description")
    {
        Genres = new[] { "Genre" },
        Studio = "Studio"
    };

    private static VideoEntry Entry(VideoMeta? meta = null, string fileName = "ABP-088.mp4") =>
        new($"./Videos/{fileName}", meta ?? EmptyMeta);

    private static byte[] CoverBytes() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "resources", "noimage.jpg"));

    private WebMetadataService Service(params IWebVideoMetaSource[] sources) => Service(null, sources);

    private WebMetadataService Service(ITextTranslationService? translation, params IWebVideoMetaSource[] sources) =>
        new(sources, new ThumbnailCache(_cacheRoot), translation);

    private StubSource Source(VideoMetadataSourceKind kind, WebVideoMetaResult result) =>
        SourceWith(kind, (_, _, _, _) => Task.FromResult(result));

    private StubSource SourceWith(VideoMetadataSourceKind kind,
        Func<MetadataProductCode, bool, ISet<string>, CancellationToken, Task<WebVideoMetaResult>> fetch) =>
        new(kind, (code, thumbnail, blocked, token) =>
        {
            _calls.Add(kind);
            return fetch(code, thumbnail, blocked, token);
        });

    public void Dispose()
    {
        if (!Directory.Exists(_cacheRoot)) return;
        foreach (var file in Directory.EnumerateFiles(_cacheRoot))
        {
            File.Delete(file);
        }
        var cacheDirectory = Path.Combine(_cacheRoot, "cache");
        if (Directory.Exists(cacheDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(cacheDirectory))
            {
                File.Delete(file);
            }
            Directory.Delete(cacheDirectory);
        }
        Directory.Delete(_cacheRoot);
    }

    private sealed class StubSource(
        VideoMetadataSourceKind kind,
        Func<MetadataProductCode, bool, ISet<string>, CancellationToken, Task<WebVideoMetaResult>> fetch) : IWebVideoMetaSource
    {
        public VideoMetadataSourceKind Kind { get; } = kind;
        public string Name => Kind == VideoMetadataSourceKind.LibreDmm ? "LibreDMM" : Kind.ToString();
        public string Host => Kind == VideoMetadataSourceKind.LibreDmm ? "primary.example.test" : "catalog.example.test";

        public Task<WebVideoMetaResult> FetchAsync(MetadataProductCode code, bool includeThumbnail,
            ISet<string> blockedHosts, CancellationToken cancellationToken) => fetch(code, includeThumbnail, blockedHosts, cancellationToken);
    }

    private sealed class StubTranslationService(Func<CancellationToken, Task<string?>> translate) : ITextTranslationService
    {
        public bool IsEnabled => true;
        public int CallCount { get; private set; }
        public string? Input { get; private set; }
        public string? Target { get; private set; }

        public Task<string?> TranslateAsync(string text, string? sourceLanguageCode, string targetLanguageCode, CancellationToken cancellationToken)
        {
            CallCount++;
            Input = text;
            Target = targetLanguageCode;
            return translate(cancellationToken);
        }
    }

    private sealed class LocalizationTranslation : ITextTranslationService
    {
        public bool IsEnabled => true;
        public List<string> Requests { get; } = new();

        public Task<string?> TranslateAsync(string text, string? sourceLanguageCode, string targetLanguageCode, CancellationToken cancellationToken)
        {
            Assert.Equal("JA", sourceLanguageCode);
            Assert.Equal("KO", targetLanguageCode);
            Requests.Add(text);
            return Task.FromResult<string?>(text switch
            {
                "捜査官 <keep>0</keep>" => "수사관 <keep>0</keep>",
                "拘束" => "구속",
                "<keep>0</keep>（ブルーレイ）" => "<keep>0</keep>(블루레이)",
                "<keep>0</keep>対応" => "지원",
                "説明" => "설명",
                _ => null
            });
        }
    }
}
