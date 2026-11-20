using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Airi.Domain;
using Airi.Infrastructure;

namespace Airi.ViewModels;

public sealed class GalleryFacetOption : INotifyPropertyChanged
{
    private readonly Action<GalleryFacetOption, bool> _change;
    private bool _isSelected;
    public GalleryFacetOption(GalleryFacetValue value, int count, bool selected, Action<GalleryFacetOption, bool> change)
    { Key = value; Count = count; _isSelected = selected; _change = change; }
    public GalleryFacetValue Key { get; }
    public string? Value => Key.Value;
    public bool IsUnspecified => Key.IsUnspecified;
    public string Label => Key.Label;
    public int Count { get; private set; }
    public bool IsSelected { get => _isSelected; set { if (_isSelected != value) _change(this, value); } }
    internal void Update(int count, bool selected)
    {
        if (Count != count) { Count = count; PropertyChanged?.Invoke(this, new(nameof(Count))); }
        if (_isSelected != selected) SetSelected(selected);
    }
    internal void SetSelected(bool value) { _isSelected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); }
    public event PropertyChangedEventHandler? PropertyChanged;
}
public sealed record GalleryChip(string Label, RelayCommand RemoveCommand);
public sealed record GalleryOperationConfirmation(LibraryOperationIntent Intent, IReadOnlyList<string> Titles)
{
    public int Count => Titles.Count;
    public IReadOnlyList<string> Paths { get; init; } = Array.Empty<string>();
}
public sealed class GalleryHistoryItem : INotifyPropertyChanged
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Summary { get; init; } = string.Empty;
    public DateTime Timestamp { get; } = DateTime.Now;
    public int Count { get; init; }
    public LibraryOperationIntent? Intent { get; init; }
    private bool _canUndo = true;
    public bool CanUndo { get => _canUndo; internal set { _canUndo = value; PropertyChanged?.Invoke(this, new(nameof(CanUndo))); } }
    private string? _error;
    public string? Error { get => _error; internal set { _error = value; PropertyChanged?.Invoke(this, new(nameof(Error))); } }
    internal bool IsUndone { get; set; }
    internal IReadOnlyList<GalleryMetadataUndo> Metadata { get; init; } = Array.Empty<GalleryMetadataUndo>();
    internal IReadOnlyList<Guid> EntryIds { get; init; } = Array.Empty<Guid>();
    public event PropertyChangedEventHandler? PropertyChanged;
}
internal sealed record GalleryMetadataUndo(VideoEntry Before, VideoEntry After, IReadOnlyList<MetadataField> Fields);
