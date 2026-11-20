using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Airi.ViewModels;
namespace Airi.Views;

public partial class GalleryFacetPicker : UserControl
{
    public static readonly DependencyProperty OptionsProperty = DependencyProperty.Register(nameof(Options), typeof(IEnumerable), typeof(GalleryFacetPicker), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(GalleryFacetPicker), new PropertyMetadata("분류", Changed));
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(string), typeof(GalleryFacetPicker), new PropertyMetadata("person"));
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(nameof(IsCompact), typeof(bool), typeof(GalleryFacetPicker), new PropertyMetadata(false, Changed));
    public IEnumerable? Options { get => (IEnumerable?)GetValue(OptionsProperty); set => SetValue(OptionsProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public bool IsCompact { get => (bool)GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }
    public static readonly DependencyProperty AlignRightProperty = DependencyProperty.Register(nameof(AlignRight), typeof(bool), typeof(GalleryFacetPicker), new PropertyMetadata(false));
    public bool AlignRight { get => (bool)GetValue(AlignRightProperty); set => SetValue(AlignRightProperty, value); }
    private readonly List<INotifyPropertyChanged> _subscribed = []; private INotifyCollectionChanged? _collection;
    public GalleryFacetPicker() { InitializeComponent(); Loaded += (_, _) => Connect(); Unloaded += (_, _) => Disconnect(); }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) { var picker = (GalleryFacetPicker)d; if (picker.IsInitialized) picker.Connect(); }
    private void Disconnect() { foreach (var item in _subscribed) item.PropertyChanged -= OptionChanged; _subscribed.Clear(); if (_collection is not null) _collection.CollectionChanged -= CollectionChanged; _collection = null; }
    private void Connect() { Disconnect(); if (Options is INotifyCollectionChanged items) { _collection = items; items.CollectionChanged += CollectionChanged; } foreach (var option in Options?.OfType<INotifyPropertyChanged>() ?? []) { option.PropertyChanged += OptionChanged; _subscribed.Add(option); } Refresh(); }
    private void CollectionChanged(object? s, NotifyCollectionChangedEventArgs e) => Connect(); private void OptionChanged(object? s, PropertyChangedEventArgs e) => Refresh();
    private void Refresh() { var options = Options?.Cast<GalleryFacetOption>().ToArray() ?? []; var selected = options.Where(o => o.IsSelected).ToArray(); Summary.Text = selected.Length == 0 ? $"{Label}: 전체" : selected.Length == 1 ? $"{Label}: {selected[0].Label}" : $"{Label}: {selected[0].Label} 외 {selected.Length - 1}"; LeadingIcon.Visibility = IsCompact ? Visibility.Collapsed : Visibility.Visible; Trigger.Padding = IsCompact ? new Thickness(7, 8, 7, 8) : new Thickness(10, 9, 10, 9); OptionsList.ItemsSource = options.Where(o => string.IsNullOrWhiteSpace(Search.Text) || o.Label.Contains(Search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToArray(); }
    private void Toggle(object s, RoutedEventArgs e)
    {
        if (!OptionsPopup.IsOpen && OptionsPopup.Child is FrameworkElement panel && Window.GetWindow(this)?.Content is FrameworkElement client)
        {
            panel.MaxWidth = Math.Max(190, client.ActualWidth - 24); panel.Measure(new Size(panel.MaxWidth, double.PositiveInfinity));
            var origin = Trigger.TranslatePoint(new Point(), client).X; var desired = origin + (IsCompact && AlignRight ? Trigger.ActualWidth - panel.DesiredSize.Width : 0);
            OptionsPopup.HorizontalOffset = Math.Clamp(desired, 12, Math.Max(12, client.ActualWidth - panel.DesiredSize.Width - 12)) - origin;
        }
        OptionsPopup.IsOpen = !OptionsPopup.IsOpen; if (OptionsPopup.IsOpen) Search.Focus();
    }
    private void Filter(object s, TextChangedEventArgs e) { if (IsInitialized) Refresh(); }
}
