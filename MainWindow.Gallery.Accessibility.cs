using System.Windows.Automation;
using Airi.Controls;
namespace Airi;
public partial class MainWindow
{
    private void CloseGalleryReadingForQuery()
    {
        _gallerySelectionAnchor = null;
        if (ViewModel.IsEditing) return;
        _galleryPopup?.QueryChanged();
    }
    private static string GalleryItemDisplayTitle(VideoItem? item) => item is null ? string.Empty : new GalleryDisplayTitleConverter().Convert(new object[] { item.Title, item.SourcePath, item.LibraryPath }, typeof(string), null!, System.Globalization.CultureInfo.CurrentCulture).ToString() ?? string.Empty;
    private void UpdateGalleryDialogAccessibility()
    {
        var title = GalleryItemDisplayTitle(ViewModel.InspectedVideo);
        AutomationProperties.SetName(DetailSurface, "영상 상세정보" + (string.IsNullOrWhiteSpace(title) ? string.Empty : ": " + title));
        AutomationProperties.SetHelpText(DetailSurface, "읽기 전용 영상 요약. Enter로 재생하고 F1로 전체 정보를 확인하거나 편집합니다. Escape로 닫습니다.");
    }
}
