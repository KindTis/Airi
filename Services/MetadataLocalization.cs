using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Airi.Domain;
using Airi.Infrastructure;

namespace Airi.Services;

internal sealed partial class MetadataLocalization(ITextTranslationService translator, string targetLanguage)
{
    private readonly ConcurrentDictionary<TranslationKey, string> _cache = new();

    public async Task<VideoMeta> TranslateAsync(VideoMeta prior, VideoMeta current,
        IReadOnlyDictionary<MetadataField, string> sources, CancellationToken token)
    {
        var previous = prior.Korean ?? new KoreanMetadata();
        var title = !sources.ContainsKey(MetadataField.Title) || prior.Title == current.Title && prior.ActorNames.SequenceEqual(current.ActorNames)
            ? previous.Title : null;
        var description = !sources.ContainsKey(MetadataField.Description) || prior.Description == current.Description ? previous.Description : null;
        var pending = new List<TranslationKey>();
        Request? titleRequest = null;
        Request? descriptionRequest = null;
        var labels = new Dictionary<string, Request>(StringComparer.Ordinal);

        if (translator.IsEnabled && !string.IsNullOrWhiteSpace(targetLanguage))
        {
            if (title is null && sources.TryGetValue(MetadataField.Title, out var titleSource) && EastAsianText().IsMatch(current.Title))
            {
                var actors = MetadataDisplay.Actors(current).DistinctBy(actor => actor.OriginalName)
                    .ToDictionary(actor => actor.OriginalName, actor => actor.KoreanName ?? actor.OriginalName, StringComparer.Ordinal);
                titleRequest = AddRequest(current.Title, JapaneseSource(current.Title, titleSource), actors);
            }
            if (description is null && sources.TryGetValue(MetadataField.Description, out var descriptionSource))
                descriptionRequest = AddRequest(current.Description, JapaneseSource(current.Description, descriptionSource));
        }

        var genres = PrepareLabels(MetadataField.Genres, current.Genres, previous.Genres);
        var tags = PrepareLabels(MetadataField.Tags, current.Tags, previous.Tags);
        foreach (var group in pending.Distinct().Where(key => !_cache.ContainsKey(key)).GroupBy(key => (key.Language, key.UseMarkup)))
        {
            foreach (var batch in group.Chunk(20))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var results = await translator.TranslateBatchAsync(batch.Select(key => key.Text).ToArray(), group.Key.Language,
                        targetLanguage, group.Key.UseMarkup, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (results.Count != batch.Length) continue;
                    for (var index = 0; index < results.Count; index++)
                    {
                        var text = results[index];
                        if (!string.IsNullOrWhiteSpace(text) && text != batch[index].Text) _cache.TryAdd(batch[index], text);
                    }
                }
                catch (Exception error)
                {
                    token.ThrowIfCancellationRequested();
                    AppLogger.Info($"Metadata translation preserved source text ({error.GetType().Name}).");
                }
            }
        }

        title ??= Resolve(titleRequest);
        description ??= Resolve(descriptionRequest);
        FillLabels(MetadataField.Genres, current.Genres, genres);
        FillLabels(MetadataField.Tags, current.Tags, tags);
        var korean = new KoreanMetadata
        {
            Title = title, Description = description, Genres = genres.Count == 0 ? null : genres, Tags = tags.Count == 0 ? null : tags
        };
        return current with { Korean = korean.OrNull() };

        Request AddRequest(string text, string? language, IReadOnlyDictionary<string, string>? names = null, bool protect = false)
        {
            var request = MakeRequest(text, language, names, protect);
            pending.Add(request.Key);
            return request;
        }

        Dictionary<string, string> PrepareLabels(MetadataField field, IReadOnlyList<string> values, IReadOnlyDictionary<string, string>? existing)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                if (existing?.TryGetValue(value, out var stored) == true) { result[value] = stored; continue; }
                if (!translator.IsEnabled || string.IsNullOrWhiteSpace(targetLanguage) || !sources.TryGetValue(field, out var source) || !EastAsianText().IsMatch(value)) continue;
                var shared = previous.Genres?.GetValueOrDefault(value) ?? previous.Tags?.GetValueOrDefault(value);
                if (shared is not null) result[value] = shared;
                else if (!labels.ContainsKey(value)) labels[value] = AddRequest(value, JapaneseSource(value, source), protect: true);
            }
            return result;
        }

        void FillLabels(MetadataField field, IReadOnlyList<string> values, Dictionary<string, string> result)
        {
            if (!sources.ContainsKey(field)) return;
            foreach (var value in values)
                if (!result.ContainsKey(value) && labels.TryGetValue(value, out var request) && Resolve(request) is { } translated)
                    result[value] = translated;
        }
    }

    private string? Resolve(Request? request)
    {
        if (request is null || !_cache.TryGetValue(request.Key, out var translated)) return null;
        if (!request.Key.UseMarkup) return translated;
        try
        {
            var xml = XElement.Parse("<root>" + translated + "</root>", LoadOptions.PreserveWhitespace);
            var used = new HashSet<int>();
            var result = new StringBuilder();
            foreach (var node in xml.Nodes())
            {
                if (node is XText plain) result.Append(plain.Value);
                else if (node is XElement element && element.Name == "keep" && int.TryParse(element.Value, out var index)
                    && index >= 0 && index < request.Kept.Count && used.Add(index)) result.Append(request.Kept[index]);
                else return null;
            }
            return used.Count == request.Kept.Count && result.Length > 0 ? result.ToString() : null;
        }
        catch (XmlException) { return null; }
    }

    private static Request MakeRequest(string text, string? language, IReadOnlyDictionary<string, string>? names, bool protect)
    {
        var patterns = names?.Keys.Where(name => name.Length > 0).OrderByDescending(name => name.Length).Select(Regex.Escape).ToList() ?? [];
        if (protect || names is not null) patterns.Add(AsciiToken().ToString());
        if (patterns.Count == 0) return new(new(text, language, false), []);
        var matches = Regex.Matches(text, string.Join('|', patterns));
        if (matches.Count == 0) return new(new(text, language, false), []);
        var kept = new List<string>();
        var xml = new StringBuilder();
        var offset = 0;
        foreach (Match match in matches)
        {
            xml.Append(SecurityElement.Escape(text[offset..match.Index]));
            xml.Append("<keep>").Append(kept.Count).Append("</keep>");
            kept.Add(names?.GetValueOrDefault(match.Value) ?? match.Value);
            offset = match.Index + match.Length;
        }
        xml.Append(SecurityElement.Escape(text[offset..]));
        return new(new(xml.ToString(), language, true), kept);
    }

    private static string? JapaneseSource(string value, string source) =>
        Kana().IsMatch(value) || source == "LibreDMM" && EastAsianText().IsMatch(value) ? "JA" : null;

    private sealed record TranslationKey(string Text, string? Language, bool UseMarkup);
    private sealed record Request(TranslationKey Key, IReadOnlyList<string> Kept);

    [GeneratedRegex(@"[\u3041-\u3096\u30A1-\u30FA\u3400-\u9FFF\uFF66-\uFF9F]")]
    private static partial Regex EastAsianText();
    [GeneratedRegex(@"[\u3041-\u3096\u30A1-\u30FA\uFF66-\uFF9F]")]
    private static partial Regex Kana();
    [GeneratedRegex(@"[A-Za-z0-9]+(?:[./+-][A-Za-z0-9]+)*[+#]*")]
    private static partial Regex AsciiToken();
}
