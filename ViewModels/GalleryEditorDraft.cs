using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Airi.Domain;

namespace Airi.ViewModels;

public sealed class GalleryDraftField : INotifyPropertyChanged
{
    private string _value;
    private bool _isApplied;
    private readonly bool _initiallyLocked;
    private bool _manualValue;
    private bool? _lockOverride;
    public GalleryDraftField(MetadataField field, string value, bool applied, bool locked)
    { Field = field; _value = value; _isApplied = applied; _initiallyLocked = locked; }
    public MetadataField Field { get; }
    public string Label => Field switch { MetadataField.Title => "제목", MetadataField.Date => "출시일", MetadataField.Actors => "배우",
        MetadataField.Studio => "제작사", MetadataField.Genres => "장르", MetadataField.Tags => "태그", MetadataField.Description => "설명", _ => "썸네일" };
    public string Value { get => _value; set => SetValue(value, manual: true); }
    public bool IsApplied { get => _isApplied; set { if (_isApplied != value) { _isApplied = value; Changed(nameof(IsApplied)); } } }
    public bool IsLocked
    {
        get => _lockOverride ?? (_initiallyLocked || _manualValue);
        set { if (IsLocked != value) { _lockOverride = value; Changed(nameof(IsLocked)); } }
    }
    internal VideoMeta? FetchedMetadata { get; private set; }
    internal bool ValueEdited { get; private set; }
    internal bool LockEdited => _lockOverride.HasValue;
    internal void SetGeneratedValue(string value) => SetValue(value, manual: false);

    private void SetValue(string value, bool manual)
    {
        if (_value == value) return;
        _value = value;
        FetchedMetadata = null;
        ValueEdited = true;
        _manualValue = manual;
        Changed(nameof(Value));
        Changed(nameof(IsLocked));
    }

    internal void FillFromMetadata(VideoMeta metadata)
    {
        FetchedMetadata = metadata;
        _value = GalleryMetadataFields.Get(metadata, Field);
        ValueEdited = true;
        _manualValue = false;
        IsApplied = true;
        Changed(nameof(Value));
        Changed(nameof(IsLocked));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string property) => PropertyChanged?.Invoke(this, new(property));
}
public sealed record GalleryMetadataTargetResult(string TargetTitle, string Outcome, string? Source, string? Reason);
public sealed record GalleryChange(string TargetTitle, MetadataField Field, string Before, string After, bool LockChanged);

public sealed class GalleryEditorDraft : INotifyPropertyChanged, IDisposable
{
    internal IReadOnlyList<VideoEntry> Targets { get; }
    private string _syncedThumbnailPath;
    private string? _error;
    public GalleryEditorDraft(IReadOnlyList<VideoEntry> targets, VideoItem displayItem)
    {
        Targets = targets;
        IsBatch = targets.Count > 1;
        var fields = IsBatch ? new[] { MetadataField.Studio, MetadataField.Genres, MetadataField.Actors, MetadataField.Date, MetadataField.Tags } :
            new[] { MetadataField.Title, MetadataField.Date, MetadataField.Actors, MetadataField.Studio, MetadataField.Genres, MetadataField.Tags, MetadataField.Description, MetadataField.Thumbnail };
        foreach (var field in fields) Fields.Add(new(field, IsBatch ? string.Empty : GalleryMetadataFields.Get(targets[0].Meta, field),
            !IsBatch, !IsBatch && targets[0].Meta.ManualFieldLocks.Contains(field)));
        ThumbnailEditor = new MetadataEditorViewModel(displayItem);
        _syncedThumbnailPath = ThumbnailEditor.ThumbnailPath;
    }
    public bool IsBatch { get; }
    public int TargetCount => Targets.Count;
    public ObservableCollection<GalleryDraftField> Fields { get; } = new();
    public ObservableCollection<GalleryChange> Changes { get; } = new();
    public ObservableCollection<GalleryMetadataTargetResult> TargetResults { get; } = new();
    public MetadataEditorViewModel ThumbnailEditor { get; }
    public string? Error { get => _error; internal set { _error = value; PropertyChanged?.Invoke(this, new(nameof(Error))); } }
    private string? _proposalSource;
    private string? _proposalReason;
    public string? ProposalSource { get => _proposalSource; internal set { _proposalSource = value; PropertyChanged?.Invoke(this, new(nameof(ProposalSource))); } }
    public string? ProposalReason { get => _proposalReason; internal set { _proposalReason = value; PropertyChanged?.Invoke(this, new(nameof(ProposalReason))); } }
    public bool HasChanges => Changes.Count > 0;
    internal bool IsAutomaticProposal { get; set; }
    internal Dictionary<Guid, VideoEntry>? AutomaticCandidates { get; set; }
    internal HashSet<Guid> MetadataUpdatesNeedingAttention { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void FillFromMetadata(VideoMeta metadata, IEnumerable<MetadataField> fetchedFields)
    {
        foreach (var field in Fields.Where(field => fetchedFields.Contains(field.Field)))
        {
            field.FillFromMetadata(metadata);
            if (field.Field == MetadataField.Thumbnail)
            {
                ThumbnailEditor.UseCachedThumbnail(metadata.Thumbnail);
                _syncedThumbnailPath = ThumbnailEditor.ThumbnailPath;
            }
        }
    }

    internal VideoEntry GetMetadataUpdateTarget(VideoEntry entry) => IsBatch ? entry : entry with
    {
        Meta = entry.Meta with
        {
            ManualFieldLocks = Fields.Where(field => field.IsApplied
                ? field.IsLocked
                : entry.Meta.ManualFieldLocks.Contains(field.Field)).Select(field => field.Field).ToArray()
        }
    };

    internal void SyncThumbnail()
    {
        if (!IsBatch && ThumbnailEditor.ThumbnailPath != _syncedThumbnailPath)
        {
            Fields.Single(field => field.Field == MetadataField.Thumbnail).SetGeneratedValue(ThumbnailEditor.ThumbnailPath);
            _syncedThumbnailPath = ThumbnailEditor.ThumbnailPath;
        }
    }

    internal Dictionary<Guid, VideoEntry>? BuildCandidates()
    {
        Error = null; Changes.Clear();
        SyncThumbnail();
        var result = new Dictionary<Guid, VideoEntry>();
        foreach (var target in Targets)
        {
            var entry = IsAutomaticProposal && AutomaticCandidates?.TryGetValue(target.Id, out var automatic) == true ? automatic : target;
            var meta = entry.Meta;
            if (!IsAutomaticProposal)
            {
                foreach (var field in Fields.Where(field => field.IsApplied && (IsBatch || field.ValueEdited || field.LockEdited)))
                {
                    try
                    {
                        if (field.ValueEdited || IsBatch)
                            meta = field.FetchedMetadata is { } fetched
                                ? GalleryMetadataFields.CopyField(meta, fetched, field.Field)
                                : GalleryMetadataFields.Set(meta, field.Field, field.Value, appendTags: IsBatch);
                        var locks = meta.ManualFieldLocks.ToHashSet();
                        if (!IsBatch || field.LockEdited)
                        {
                            if (field.IsLocked) locks.Add(field.Field); else locks.Remove(field.Field);
                        }
                        else if (!GalleryMetadataFields.Equal(target.Meta, meta, field.Field))
                        {
                            locks.Add(field.Field);
                        }
                        meta = meta with { ManualFieldLocks = locks.OrderBy(value => value).ToArray() };
                    }
                    catch (ArgumentException ex) { Error = ex.Message; return null; }
                }
            }
            foreach (var field in Enum.GetValues<MetadataField>())
            {
                var lockChanged = target.Meta.ManualFieldLocks.Contains(field) != meta.ManualFieldLocks.Contains(field);
                if (!GalleryMetadataFields.Equal(target.Meta, meta, field) || lockChanged)
                    Changes.Add(new(target.Meta.Title, field, GalleryMetadataFields.Get(target.Meta, field), GalleryMetadataFields.Get(meta, field), lockChanged));
            }
            result[target.Id] = entry with { Meta = meta };
        }
        PropertyChanged?.Invoke(this, new(nameof(HasChanges)));
        return result;
    }
    public void Dispose() => ThumbnailEditor.Dispose();
    internal void AcceptChanges() => ThumbnailEditor.AcceptChanges();
}

internal static class GalleryMetadataFields
{
    public static string Get(VideoMeta meta, MetadataField field) => field switch
    {
        MetadataField.Title => meta.Korean?.Title ?? meta.Title ?? string.Empty,
        MetadataField.Date => meta.RawDate ?? meta.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
        MetadataField.Actors => string.Join("\n", MetadataDisplay.Actors(meta).Select(actor => actor.KoreanName ?? actor.OriginalName)),
        MetadataField.Tags => string.Join("\n", meta.Tags.Select(value => KoreanMetadata.Label(meta.Korean?.Tags, value))),
        MetadataField.Genres => string.Join("\n", meta.Genres.Select(value => KoreanMetadata.Label(meta.Korean?.Genres, value))), MetadataField.Studio => meta.Studio ?? string.Empty,
        MetadataField.Description => meta.Korean?.Description ?? meta.Description ?? string.Empty, _ => meta.Thumbnail ?? string.Empty
    };
    public static IReadOnlyList<string> Tokens(string value) => value.Split(new[] { ',', '，', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(token => token.Trim()).Where(token => token.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
    public static VideoMeta Set(VideoMeta meta, MetadataField field, string value, bool appendTags = false)
    {
        var text = value.Trim();
        DateOnly? date = null;
        if (field == MetadataField.Date && text.Length > 0)
        {
            if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                throw new ArgumentException("출시일은 yyyy-MM-dd 형식 또는 빈 값이어야 합니다.");
            date = parsed;
        }
        if (field == MetadataField.Title && string.IsNullOrWhiteSpace(text)) throw new ArgumentException("제목을 입력해 주세요.");
        var korean = meta.Korean ?? new KoreanMetadata();
        return field switch
        {
            MetadataField.Title => meta with { Title = text, Korean = (korean with { Title = null }).OrNull() },
            MetadataField.Date => meta with { Date = date, RawDate = null },
            MetadataField.Actors => SetActors(meta, text),
            MetadataField.Genres => meta with { Genres = Tokens(text), Korean = (korean with { Genres = null }).OrNull() },
            MetadataField.Studio => meta with { Studio = text.Length == 0 ? null : text },
            MetadataField.Tags => meta with { Tags = appendTags ? meta.Tags.Concat(Tokens(text).Where(tag => !meta.Tags.Contains(tag, StringComparer.Ordinal))).ToArray() : Tokens(text),
                Korean = appendTags ? meta.Korean : (korean with { Tags = null }).OrNull() },
            MetadataField.Description => meta with { Description = text, Korean = (korean with { Description = null }).OrNull() }, _ => meta with { Thumbnail = text }
        };
    }

    private static VideoMeta SetActors(VideoMeta meta, string text)
    {
        var original = MetadataDisplay.Actors(meta);
        var actors = Tokens(text).Select(value =>
        {
            var matches = original.Where(actor => value == actor.OriginalName || value == actor.KoreanName).Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : new ActorName(value);
        }).DistinctBy(actor => actor.OriginalName).ToArray();
        return meta with { Actors = actors.Select(actor => actor.OriginalName).ToArray(),
            ActorNames = actors.Where(actor => actor.Reading is not null || actor.KoreanName is not null).ToArray() };
    }
    public static bool Equal(VideoMeta left, VideoMeta right, MetadataField field) => field switch
    {
        MetadataField.Title => left.Title == right.Title && left.Korean?.Title == right.Korean?.Title,
        MetadataField.Description => left.Description == right.Description && left.Korean?.Description == right.Korean?.Description,
        MetadataField.Actors => left.Actors.SequenceEqual(right.Actors) && left.ActorNames.SequenceEqual(right.ActorNames),
        MetadataField.Tags => left.Tags.SequenceEqual(right.Tags) && Get(left, field) == Get(right, field),
        MetadataField.Genres => left.Genres.SequenceEqual(right.Genres) && Get(left, field) == Get(right, field), MetadataField.Date => left.Date == right.Date && left.RawDate == right.RawDate,
        MetadataField.Studio => left.Studio == right.Studio, _ => Get(left, field) == Get(right, field)
    };
    public static VideoMeta CopyField(VideoMeta target, VideoMeta source, MetadataField field)
    {
        var korean = target.Korean ?? new KoreanMetadata();
        return field switch
        {
            MetadataField.Title => target with { Title = source.Title, Korean = (korean with { Title = source.Korean?.Title }).OrNull() },
            MetadataField.Date => target with { Date = source.Date, RawDate = source.RawDate },
            MetadataField.Actors => target with { Actors = source.Actors, ActorNames = source.ActorNames },
            MetadataField.Tags => target with { Tags = source.Tags, Korean = (korean with { Tags = source.Korean?.Tags }).OrNull() },
            MetadataField.Genres => target with { Genres = source.Genres, Korean = (korean with { Genres = source.Korean?.Genres }).OrNull() },
            MetadataField.Studio => target with { Studio = source.Studio },
            MetadataField.Description => target with { Description = source.Description, Korean = (korean with { Description = source.Korean?.Description }).OrNull() },
            _ => target with { Thumbnail = source.Thumbnail }
        };
    }
}
