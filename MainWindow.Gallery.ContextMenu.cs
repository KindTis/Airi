using System.Windows;
using System.Windows.Controls;
using Airi.Domain;

namespace Airi;

public partial class MainWindow
{
    private ContextMenu? _galleryCardMenu;
    private void OnGalleryContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (IsGallerySelectionMode || ViewModel.IsEditing || e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(VideoList, source) is not ListBoxItem { DataContext: VideoItem item } anchor)
        { e.Handled = true; return; }
        _galleryPopup?.Close();
        ViewModel.SelectedVideo = item;
        ViewModel.InspectCommand.Execute(item);
        var menu = CreateGalleryCardMenu(item);
        menu.Opened += (_, _) => _galleryPopup?.Close();
        menu.Closed += (_, _) => { if (ReferenceEquals(_galleryCardMenu, menu)) _galleryCardMenu = null; };
        anchor.ContextMenu = _galleryCardMenu = menu;
    }
    internal ContextMenu CreateGalleryCardMenu(VideoItem item)
    {
        var menu = new ContextMenu();
        void Run(Action action)
        {
            if (ViewModel.IsEditing || !ViewModel.Videos.Contains(item)) return;
            ViewModel.SelectedVideo = item;
            ViewModel.InspectCommand.Execute(item);
            action();
        }
        void Add(string label, Action action, bool enabled = true)
        {
            var entry = new MenuItem { Header = label, IsEnabled = enabled };
            entry.Click += (_, _) => Run(action);
            menu.Items.Add(entry);
        }
        Add("메타데이터 갱신", () => ViewModel.FetchMetadataCommand.Execute(item), !item.IsInRecycleBin);
        Add("폴더에서 보기", () =>
        {
            var result = _videoLauncher.ShowInFolder(item.SourcePath);
            if (!result.Succeeded) ShowGalleryMessage("폴더를 열 수 없습니다", result.Reason ?? "경로를 확인해 주세요.");
        }, !string.IsNullOrWhiteSpace(item.SourcePath));
        if (item.IsInRecycleBin)
        {
            Add("재검사", () => ViewModel.ReinspectInspectedCommand.Execute(null));
            Add("복원", () => ViewModel.RestoreInspectedCommand.Execute(null), item.CanRestore);
        }
        else Add("삭제", () => ViewModel.DeleteInspectedCommand.Execute(null));
        menu.Items.Add(new Separator());
        void Facet(string label, GalleryFacet facet, IEnumerable<(string Value, string Label)> values)
        {
            var group = new MenuItem { Header = label };
            var candidates = values.Where(value => !string.IsNullOrWhiteSpace(value.Value)).DistinctBy(value => value.Value).ToArray();
            foreach (var value in candidates.Length == 0 ? new[] { (Value: string.Empty, Label: "미지정") } : candidates)
            {
                var option = new MenuItem { Header = value.Label };
                option.Click += (_, _) => Run(() => ViewModel.AddFacetFilter(facet, value.Value, value.Value.Length == 0));
                group.Items.Add(option);
            }
            menu.Items.Add(group);
        }
        Facet("배우로 필터", GalleryFacet.Actors, item.Actors.Zip(item.DisplayActors));
        Facet("제작사로 필터", GalleryFacet.Studio, new[] { (item.Studio ?? string.Empty, item.Studio ?? string.Empty) });
        Facet("장르로 필터", GalleryFacet.Genres, (item.GenreFilterValues ?? item.Genres).Zip(item.Genres));
        var tags = new MenuItem { Header = "태그로 검색", IsEnabled = item.Tags.Count > 0 };
        foreach (var tag in item.Tags)
        {
            var option = new MenuItem { Header = tag };
            option.Click += (_, _) => Run(() => ViewModel.SearchForTag(tag));
            tags.Items.Add(option);
        }
        menu.Items.Add(tags);
        return menu;
    }
}
