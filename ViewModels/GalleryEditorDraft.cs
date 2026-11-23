using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Airi.Domain;
using Airi.Infrastructure;

namespace Airi.ViewModels;

public sealed class GalleryDraftField : INotifyPropertyChanged
{
    private string _value;
    private bool _isApplied;
    private bool _isLocked;
    private string? _proposedValue;
    public GalleryDraftField(MetadataField field, string value, bool applied, bool locked)
    { Field = field; _value = value; _isApplied = applied; _isLocked = locked; UseProposalCommand = new RelayCommand(_ =>
        { if (ProposedValue is { } proposal) { Value = proposal; IsApplied = true; } }); }
    public MetadataField Field { get; }
    public string Label => Field switch { MetadataField.Title => "제목", MetadataField.Date => "출시일", MetadataField.Actors => "배우",
        MetadataField.Studio => "제작사", MetadataField.Genres => "장르", MetadataField.Tags => "태그", MetadataField.Description => "설명", _ => "썸네일" };
    public string Value { get => _value; set { if (_value != value) { _value = value; ValueEdited = true; _isLocked = true;
        Changed(nameof(Value)); Changed(nameof(IsLocked)); } } }
    public bool IsApplied { get => _isApplied; set { if (_isApplied != value) { _isApplied = value; Changed(nameof(IsApplied)); } } }
    public bool IsLocked { get => _isLocked; set { if (_isLocked != value) { _isLocked = value; LockEdited = true; Changed(nameof(IsLocked)); } } }
    public string? ProposedValue { get => _proposedValue; internal set { _proposedValue = value; Changed(nameof(ProposedValue)); } }
    private string? _proposalSource;
    private string? _proposalReason;
    public string? ProposalSource { get => _proposalSource; internal set { _proposalSource = value; Changed(nameof(ProposalSource)); } }
    public string? ProposalReason { get => _proposalReason; internal set { _proposalReason = value; Changed(nameof(ProposalReason)); } }
    public RelayCommand UseProposalCommand { get; }
    internal bool ValueEdited { get; private set; }
    internal bool LockEdited { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string property) => PropertyChanged?.Invoke(this, new(property));
}
public sealed record GalleryMetadataTargetResult(string TargetTitle, string Outcome, string? Source, string? Reason);
public sealed record GalleryChange(string TargetTitle, MetadataField Field, string Before, string After, bool LockChanged);

public sealed class GalleryEditorDraft : INotifyPropertyChanged, IDisposable
{
    internal IReadOnlyList<VideoEntry> Targets { get; }
    private readonly string _initialThumbnail;
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
        _initialThumbnail = ThumbnailEditor.ThumbnailPath;
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
    private bool _isProposalPartial;
    public string? ProposalSource { get => _proposalSource; internal set { _proposalSource = value; PropertyChanged?.Invoke(this, new(nameof(ProposalSource))); } }
    public string? ProposalReason { get => _proposalReason; internal set { _proposalReason = value; PropertyChanged?.Invoke(this, new(nameof(ProposalReason))); } }
    public bool IsProposalPartial { get => _isProposalPartial; internal set { _isProposalPartial = value; PropertyChanged?.Invoke(this, new(nameof(IsProposalPartial))); } }
    public bool HasChanges => Changes.Count > 0;
    internal bool IsAutomaticProposal { get; set; }
    internal Dictionary<Guid, VideoEntry>? AutomaticCandidates { get; set; }
    internal HashSet<Guid> MetadataUpdatesNeedingAttention { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;

    internal Dictionary<Guid, VideoEntry>? BuildCandidates()
    {
        Error = null; Changes.Clear();
        if (!IsBatch && ThumbnailEditor.ThumbnailPath != _initialThumbnail)
            Fields.Single(field => field.Field == MetadataField.Thumbnail).Value = ThumbnailEditor.ThumbnailPath;
        var result = new Dictionary<Guid, VideoEntry>();
        foreach (var target in Targets)
        {
            var meta = IsAutomaticProposal && AutomaticCandidates?.TryGetValue(target.Id, out var automatic) == true ? automatic.Meta : target.Meta;
            if (!IsAutomaticProposal)
            {
                foreach (var field in Fields.Where(field => field.IsApplied && (IsBatch || field.ValueEdited || field.LockEdited)))
                {
                    try
                    {
                        if (field.ValueEdited || IsBatch) meta = GalleryMetadataFields.Set(meta, field.Field, field.Value, appendTags: IsBatch);
                        var locks = meta.ManualFieldLocks.ToHashSet();
                        if (IsBatch || field.IsLocked) locks.Add(field.Field); else locks.Remove(field.Field);
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
            result[target.Id] = target with { Meta = meta };
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
        MetadataField.Title => meta.Title ?? string.Empty,
        MetadataField.Date => meta.RawDate ?? meta.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
        MetadataField.Actors => string.Join("\n", meta.Actors), MetadataField.Tags => string.Join("\n", meta.Tags),
        MetadataField.Genres => string.Join("\n", meta.Genres), MetadataField.Studio => meta.Studio ?? string.Empty,
        MetadataField.Description => meta.Description ?? string.Empty, _ => meta.Thumbnail ?? string.Empty
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
        return field switch
        {
            MetadataField.Title => meta with { Title = text }, MetadataField.Date => meta with { Date = date, RawDate = null },
            MetadataField.Actors => meta with { Actors = Tokens(text) }, MetadataField.Genres => meta with { Genres = Tokens(text) },
            MetadataField.Studio => meta with { Studio = text.Length == 0 ? null : text },
            MetadataField.Tags => meta with { Tags = appendTags ? meta.Tags.Concat(Tokens(text).Where(tag => !meta.Tags.Contains(tag, StringComparer.Ordinal))).ToArray() : Tokens(text) },
            MetadataField.Description => meta with { Description = text }, _ => meta with { Thumbnail = text }
        };
    }
    public static bool Equal(VideoMeta left, VideoMeta right, MetadataField field) => field switch
    {
        MetadataField.Actors => left.Actors.SequenceEqual(right.Actors), MetadataField.Tags => left.Tags.SequenceEqual(right.Tags),
        MetadataField.Genres => left.Genres.SequenceEqual(right.Genres), MetadataField.Date => left.Date == right.Date && left.RawDate == right.RawDate,
        MetadataField.Studio => left.Studio == right.Studio, _ => Get(left, field) == Get(right, field)
    };
    public static VideoMeta CopyField(VideoMeta target, VideoMeta source, MetadataField field) => field switch
    {
        MetadataField.Title => target with { Title = source.Title }, MetadataField.Date => target with { Date = source.Date, RawDate = source.RawDate },
        MetadataField.Actors => target with { Actors = source.Actors.ToArray() }, MetadataField.Tags => target with { Tags = source.Tags.ToArray() },
        MetadataField.Genres => target with { Genres = source.Genres.ToArray() }, MetadataField.Studio => target with { Studio = source.Studio },
        MetadataField.Description => target with { Description = source.Description }, _ => target with { Thumbnail = source.Thumbnail }
    };
}
