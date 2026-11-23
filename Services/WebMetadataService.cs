using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Airi.Domain;
using Airi.Infrastructure;
using Airi.Web;

namespace Airi.Services;

public enum MetadataLookupOutcome
{
    Unidentified,
    NotFound,
    Failed,
    Partial,
    Found,
    Unchanged
}

public sealed record MetadataEnrichmentResult(
    MetadataLookupOutcome Status,
    VideoEntry? Entry,
    string? Source,
    string? Detail,
    IReadOnlyDictionary<MetadataField, string> FieldSources);

public sealed class WebMetadataService
{
    private static readonly MetadataField[] Fields = Enum.GetValues<MetadataField>();
    private static readonly VideoMetadataSourceKind[] GeneralOrder =
    [
        VideoMetadataSourceKind.LibreDmm,
        VideoMetadataSourceKind.JavBus,
        VideoMetadataSourceKind.JavBusUncensored
    ];
    private static readonly VideoMetadataSourceKind[] UncensoredOrder =
    [
        VideoMetadataSourceKind.LibreDmm,
        VideoMetadataSourceKind.JavBusUncensored,
        VideoMetadataSourceKind.JavBus
    ];

    private readonly IReadOnlyList<IWebVideoMetaSource> _sources;
    private readonly ThumbnailCache _thumbnailCache;
    private readonly ITextTranslationService _translationService;
    private readonly string _translationTargetLanguageCode;
    private readonly MetadataLocalization _localization;

    public WebMetadataService(
        IEnumerable<IWebVideoMetaSource> sources,
        ThumbnailCache thumbnailCache,
        ITextTranslationService? translationService = null,
        string translationTargetLanguageCode = "KO")
    {
        _sources = sources?.ToArray() ?? throw new ArgumentNullException(nameof(sources));
        _thumbnailCache = thumbnailCache ?? throw new ArgumentNullException(nameof(thumbnailCache));
        _translationService = translationService ?? NullTranslationService.Instance;
        _translationTargetLanguageCode = translationTargetLanguageCode ?? string.Empty;
        _localization = new MetadataLocalization(_translationService, _translationTargetLanguageCode);
    }

    public async Task<MetadataEnrichmentResult> FetchEnrichmentAsync(
        VideoEntry entry, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(entry);
        token.ThrowIfCancellationRequested();

        var targets = Fields.Except(entry.Meta.ManualFieldLocks).ToHashSet();
        var fieldSources = new Dictionary<MetadataField, string>();
        var attempts = new List<string>();
        var details = new List<string>();
        if (targets.Count == 0)
            return Finish(MetadataLookupOutcome.Unchanged, "갱신할 수 있는 필드가 없습니다.");
        if (MetadataProductCode.FromEntry(entry) is not { } code)
            return Finish(MetadataLookupOutcome.Unidentified, "지원하는 품번 하나를 식별하지 못했습니다.");

        var meta = entry.Meta;
        var matched = false;
        var failed = false;
        var blockedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? ownedThumbnail = null;
        var sameThumbnail = false;

        bool NeedsField(MetadataField field) => targets.Contains(field) && !fieldSources.ContainsKey(field);
        bool NeedsFallback() => targets.Any(field => field != MetadataField.Description && NeedsField(field));

        try
        {
            foreach (var kind in code.IsUncensored ? UncensoredOrder : GeneralOrder)
            {
                token.ThrowIfCancellationRequested();
                if (kind != VideoMetadataSourceKind.LibreDmm && !NeedsFallback()) break;

                var source = _sources.FirstOrDefault(source => source.Kind == kind);
                if (source is null) continue;
                if (blockedHosts.Contains(source.Host))
                {
                    failed = true;
                    details.Add($"{source.Name}: 호스트 접근 제한으로 건너뜀");
                    AppLogger.Info($"Metadata lookup via {source.Name}: host blocked.");
                    continue;
                }

                attempts.Add(source.Name);
                var includeThumbnail = NeedsField(MetadataField.Thumbnail);
                var result = await FetchSourceAsync(source, code, includeThumbnail, blockedHosts, token).ConfigureAwait(false);
                failed |= result.Failed;
                if (!string.IsNullOrWhiteSpace(result.Detail))
                    details.Add($"{source.Name}: {result.Detail}");
                if (result.Meta is not { } incoming) continue;

                matched = true;
                var genres = CleanList(incoming.Genres);
                var tags = CleanList(incoming.Tags);
                bool AcceptField(MetadataField field, bool hasValue) =>
                    hasValue && NeedsField(field) && fieldSources.TryAdd(field, source.Name);

                var actors = CleanList(incoming.Actors);
                var acceptActors = AcceptField(MetadataField.Actors, actors.Length > 0);
                var actorNames = acceptActors ? actors.Select(name => incoming.ActorNames.FirstOrDefault(actor => actor.OriginalName == name)
                    ?? meta.ActorNames.FirstOrDefault(actor => actor.OriginalName == name)).OfType<ActorName>().ToArray() : meta.ActorNames;
                var acceptDate = AcceptField(MetadataField.Date, incoming.Date.HasValue);
                meta = meta with
                {
                    Title = AcceptField(MetadataField.Title, !string.IsNullOrWhiteSpace(incoming.Title)) ? incoming.Title : meta.Title,
                    Date = acceptDate ? incoming.Date : meta.Date,
                    RawDate = acceptDate ? null : meta.RawDate,
                    Actors = acceptActors ? actors : meta.Actors,
                    ActorNames = actorNames,
                    Genres = AcceptField(MetadataField.Genres, genres.Length > 0) ? genres : meta.Genres,
                    Tags = AcceptField(MetadataField.Tags, tags.Length > 0 || genres.Length > 0) ? (tags.Length == 0 ? genres : tags) : meta.Tags,
                    Description = AcceptField(MetadataField.Description, !string.IsNullOrWhiteSpace(incoming.Description)) ? incoming.Description : meta.Description,
                    Studio = AcceptField(MetadataField.Studio, !string.IsNullOrWhiteSpace(incoming.Studio)) ? incoming.Studio : meta.Studio
                };

                if (includeThumbnail && result.ThumbnailBytes is { Length: > 0 } bytes)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        ownedThumbnail = await _thumbnailCache.SaveAsync(
                            bytes, result.ThumbnailExtension ?? ".jpg", code.Value, token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        token.ThrowIfCancellationRequested();
                        failed = true;
                        details.Add($"{source.Name}: 표지 이미지를 사용할 수 없습니다.");
                        AppLogger.Info($"Metadata cover via {source.Name}: unusable ({ex.GetType().Name}).");
                    }

                    if (ownedThumbnail is not null)
                    {
                        meta = meta with { Thumbnail = ownedThumbnail };
                        fieldSources.Add(MetadataField.Thumbnail, source.Name);
                        sameThumbnail = await CoverMatchesAsync(entry.Meta.Thumbnail, bytes, token).ConfigureAwait(false);
                    }
                }

                if (kind != VideoMetadataSourceKind.LibreDmm) break;
            }

            token.ThrowIfCancellationRequested();
            var hasMissingFields = NeedsFallback();
            if (fieldSources.Count > 0) meta = await _localization.TranslateAsync(entry.Meta, meta, fieldSources, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            if (sameThumbnail && ownedThumbnail is not null)
            {
                _thumbnailCache.DiscardOwned(ownedThumbnail);
                ownedThumbnail = null;
                meta = meta with { Thumbnail = entry.Meta.Thumbnail };
            }

            if (fieldSources.Count > 0)
            {
                return hasMissingFields
                    ? Finish(MetadataLookupOutcome.Partial, "보완할 필드가 남았습니다.", entry with { Meta = meta, ProductCode = code.Value })
                    : Finish(MetadataLookupOutcome.Found, null, entry with { Meta = meta, ProductCode = code.Value });
            }
            if (failed) return Finish(MetadataLookupOutcome.Failed, "조회 중 오류가 발생했습니다.");
            if (matched) return Finish(MetadataLookupOutcome.Unchanged, "동일 작품 상세에 채택할 새 값이 없습니다.");
            return Finish(MetadataLookupOutcome.NotFound, attempts.Count == 0
                ? "조회할 공급자가 없습니다."
                : "동일한 품번의 상세를 확인하지 못했습니다.");
        }
        catch
        {
            if (ownedThumbnail is not null) _thumbnailCache.DiscardOwned(ownedThumbnail);
            if (token.IsCancellationRequested) AppLogger.Info($"Metadata lookup cancelled for {entry.Id:D}.");
            throw;
        }

        MetadataEnrichmentResult Finish(MetadataLookupOutcome status, string? reason, VideoEntry? candidate = null)
        {
            if (reason is not null) details.Insert(0, reason);
            IEnumerable<string> sources = candidate is null ? attempts : fieldSources.Values;
            var sourceNames = sources.Distinct().ToArray();
            var detail = details.Count > 0 ? string.Join(" / ", details) : null;
            var trace = string.Join(", ", fieldSources.Select(item => $"{item.Key}={item.Value}"));
            AppLogger.Info($"Metadata lookup for {entry.Id:D}: {status}, sources=[{string.Join(", ", attempts)}], fields=[{trace}], reason={detail ?? "none"}.");
            return new(status, candidate, sourceNames.Length > 0 ? string.Join(", ", sourceNames) : null, detail, fieldSources);
        }
    }

    private static async Task<WebVideoMetaResult> FetchSourceAsync(IWebVideoMetaSource source,
        MetadataProductCode code, bool includeThumbnail, ISet<string> blockedHosts, CancellationToken token)
    {
        try
        {
            var result = await source.FetchAsync(code, includeThumbnail, blockedHosts, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var outcome = result.Failed ? "failed" : result.Meta is null ? "no matching detail" : "matching detail";
            AppLogger.Info($"Metadata lookup via {source.Name}: {outcome}.");
            return result;
        }
        catch (Exception ex)
        {
            token.ThrowIfCancellationRequested();
            AppLogger.Info($"Metadata lookup via {source.Name}: failure ({ex.GetType().Name}).");
            return new(null, Detail: ex is OperationCanceledException or TimeoutException
                ? "요청 시간 초과"
                : "공급자 응답을 확인하지 못했습니다.", Failed: true);
        }
    }

    public async Task<VideoMeta> TranslateDescriptionAsync(VideoMeta meta, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_translationService.IsEnabled ||
            string.IsNullOrWhiteSpace(_translationTargetLanguageCode) ||
            string.IsNullOrWhiteSpace(meta.Description))
        {
            return meta;
        }

        try
        {
            var translated = await _translationService.TranslateAsync(
                meta.Description, null, _translationTargetLanguageCode, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return string.IsNullOrWhiteSpace(translated) || TextEquals(translated, meta.Description)
                ? meta
                : meta with { Description = translated };
        }
        catch (Exception ex)
        {
            token.ThrowIfCancellationRequested();
            AppLogger.Info($"Metadata description translation kept original ({ex.GetType().Name}).");
            return meta;
        }
    }

    private static string[] CleanList(IReadOnlyList<string> values) => values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim())
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private static bool TextEquals(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.Ordinal);

    private static async Task<bool> CoverMatchesAsync(string current, byte[] bytes, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(current)) return false;
        try
        {
            var path = LibraryPathHelper.ResolveToAbsolute(current);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, FileOptions.Asynchronous);
            if (stream.Length != bytes.Length) return false;
            var currentBytes = new byte[bytes.Length];
            await stream.ReadExactlyAsync(currentBytes, token).ConfigureAwait(false);
            return currentBytes.AsSpan().SequenceEqual(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public void AcceptCandidate(VideoEntry candidate) => _thumbnailCache.AcceptOwned(candidate.Meta.Thumbnail);
    public void DiscardCandidate(VideoEntry candidate) => _thumbnailCache.DiscardOwned(candidate.Meta.Thumbnail);

}
