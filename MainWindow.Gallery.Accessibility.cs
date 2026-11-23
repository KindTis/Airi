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
        AutomationProperties.SetName(DetailSurface, DetailHeading.Text + (string.IsNullOrWhiteSpace(title) ? string.Empty : ": " + title));
        AutomationProperties.SetHelpText(DetailSurface, ViewModel.IsEditing ? "메타데이터 편집 대화 상자. 변경 미리보기 후 적용하거나 Escape로 초안을 취소합니다." : "비모달 영상 상세 대화 상자. Enter와 Tab으로 조작하고 Escape로 원래 영상으로 돌아갑니다.");
    }
}
