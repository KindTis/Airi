using Airi;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Airi.ViewModels;
using Airi.Infrastructure;
using Airi.Web;
using Microsoft.Win32;

namespace Airi.Views
{
    public partial class MetadataEditorWindow : Window
    {
        public MetadataEditorWindow(VideoItem item)
        {
            InitializeComponent();
            DataContext = new MetadataEditorViewModel(item);
            KeyDown += OnWindowKeyDown;
        }

        public MetadataEditResult? Result { get; private set; }

        private async void OnSelectThumbnailClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MetadataEditorViewModel vm)
            {
                return;
            }

            var dialog = new OpenFileDialog
            {
                Title = "썸네일 이미지 선택",
                Filter = "이미지 파일 (*.jpg;*.jpeg;*.png;*.webp;*.bmp)|*.jpg;*.jpeg;*.png;*.webp;*.bmp|모든 파일 (*.*)|*.*"
            };

            if (dialog.ShowDialog(this) == true)
            {
                try
                {
                    var updated = await vm.UpdateThumbnailFromFileAsync(dialog.FileName);
                    if (!updated)
                    {
                        MessageBox.Show(this, "이미지 파일을 불러오지 못했습니다.", "경고", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"썸네일을 업데이트하는 중 오류가 발생했습니다.\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void OnResetThumbnailClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is MetadataEditorViewModel vm)
            {
                vm.ResetThumbnail();
            }
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MetadataEditorViewModel vm)
            {
                DialogResult = false;
                return;
            }

            if (!vm.TryBuildResult(out var result, out var error))
            {
                MessageBox.Show(this, error ?? "입력을 확인해주세요.", "입력 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Result = result;
            DialogResult = true;
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                DialogResult = false;
            }
        }

        private async void OnTryParseOn141JavClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MetadataEditorViewModel vm)
            {
                return;
            }

            var normalized = LibraryPathHelper.NormalizeCode(vm.Title);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                MessageBox.Show(this, "검색에 사용할 제목이 없습니다.", "안내", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (Owner is not MainWindow mainWindow)
            {
                AppLogger.Info("[MetadataEditor] Unable to access crawler from metadata editor window owner.");
                MessageBox.Show(this, "크롤러에 접근할 수 없습니다.", "안내", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            AppLogger.Info($"[MetadataEditor] Initiating crawler parse for '{vm.Title}' (normalized: '{normalized}').");

            SetInteractionInProgress(true);
            try
            {
                var result = await mainWindow.ViewModel.TryFetchOneFourOneJavMetadataAsync(normalized);
                if (result is null)
                {
                    AppLogger.Info("[MetadataEditor] No metadata payload was returned from 141jav.");
                    MessageBox.Show(this, "141jav에서 메타데이터를 찾지 못했습니다.", "안내", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var applied = await ApplyCrawlerResultAsync(vm, result);
                if (!applied)
                {
                    MessageBox.Show(this, "적용할 새 메타데이터가 없습니다.", "안내", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                AppLogger.Info("[MetadataEditor] Crawler parse completed.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("[MetadataEditor] Unexpected exception while parsing crawler page.", ex);
                MessageBox.Show(this, $"141jav 메타데이터를 적용하는 중 오류가 발생했습니다.\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetInteractionInProgress(false);
            }
        }

        private async Task<bool> ApplyCrawlerResultAsync(MetadataEditorViewModel vm, WebVideoMetaResult result)
        {
            AppLogger.Info("[MetadataEditor] Parsing crawler metadata result.");
            var applied = false;
            var meta = result.Meta;

            if (meta.Date is DateOnly releaseDate)
            {
                AppLogger.Info($"[MetadataEditor] Applying release date from crawler: {releaseDate:yyyy-MM-dd}.");
                vm.ReleaseDate = releaseDate.ToDateTime(TimeOnly.MinValue);
                applied = true;
            }

            if (meta.Tags.Count > 0)
            {
                AppLogger.Info($"[MetadataEditor] Applying {meta.Tags.Count} tags from crawler.");
                vm.TagsText = string.Join(Environment.NewLine, meta.Tags);
                applied = true;
            }

            if (meta.Actors.Count > 0)
            {
                AppLogger.Info($"[MetadataEditor] Applying {meta.Actors.Count} actors from crawler.");
                vm.ActorsText = string.Join(Environment.NewLine, meta.Actors);
                applied = true;
            }

            if (!string.IsNullOrWhiteSpace(meta.Description))
            {
                vm.Description = meta.Description;
                AppLogger.Info("[MetadataEditor] Applied crawler description to metadata editor.");
                applied = true;
            }

            if (result.ThumbnailBytes is { Length: > 0 })
            {
                var updated = await vm.UpdateThumbnailFromBytesAsync(result.ThumbnailBytes, result.ThumbnailExtension);
                if (!updated)
                {
                    AppLogger.Info("[MetadataEditor] Thumbnail update from crawler image was not applied.");
                    MessageBox.Show(this, "썸네일을 갱신하지 못했습니다.", "경고", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    AppLogger.Info("[MetadataEditor] Thumbnail updated from crawler image.");
                    applied = true;
                }
            }

            return applied;
        }

        private void SetInteractionInProgress(bool isInProgress)
        {
            var isEnabled = !isInProgress;
            TryParseOn141JavButton.IsEnabled = isEnabled;
            CancelButton.IsEnabled = isEnabled;
            SaveButton.IsEnabled = isEnabled;
        }
    }
}
