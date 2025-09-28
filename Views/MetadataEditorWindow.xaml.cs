using Airi;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Airi.ViewModels;
using Airi.Infrastructure;
using Microsoft.Win32;

namespace Airi.Views
{
    public partial class MetadataEditorWindow : Window
    {
        private static readonly HttpClient ThumbnailHttpClient = new();

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

            var url = $"https://www.141jav.com/search/{Uri.EscapeDataString(normalized)}";

            AppLogger.Info($"[MetadataEditor] Initiating crawler parse for '{vm.Title}' (normalized: '{normalized}').");

            SetInteractionInProgress(true);
            try
            {
                var navigated = await mainWindow.ViewModel.NavigateCrawlerToAsync(url);
                if (!navigated)
                {
                    AppLogger.Info("[MetadataEditor] Crawler navigation request was not executed (crawler inactive).");
                    MessageBox.Show(this, "크롤러가 실행 중인지 확인해주세요.", "안내", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                await ParseCrawlerResultAsync(mainWindow, vm);
                AppLogger.Info("[MetadataEditor] Crawler parse completed.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("[MetadataEditor] Unexpected exception while parsing crawler page.", ex);
            }
            finally
            {
                SetInteractionInProgress(false);
            }
        }

        private async Task ParseCrawlerResultAsync(MainWindow mainWindow, MetadataEditorViewModel vm)
        {
            AppLogger.Info("[MetadataEditor] Parsing crawler metadata result.");
            var crawlerMetadata = await mainWindow.ViewModel.TryGetCrawlerMetadataAsync();
            if (crawlerMetadata is not null)
            {
                if (crawlerMetadata.ReleaseDate is DateTime releaseDate)
                {
                    AppLogger.Info($"[MetadataEditor] Applying release date from crawler: {releaseDate:yyyy-MM-dd}.");
                    vm.ReleaseDate = releaseDate;
                }

                if (crawlerMetadata.Tags.Count > 0)
                {
                    AppLogger.Info($"[MetadataEditor] Applying {crawlerMetadata.Tags.Count} tags from crawler.");
                    vm.TagsText = string.Join(Environment.NewLine, crawlerMetadata.Tags);
                }

                if (crawlerMetadata.Actors.Count > 0)
                {
                    AppLogger.Info($"[MetadataEditor] Applying {crawlerMetadata.Actors.Count} actors from crawler.");
                    vm.ActorsText = string.Join(Environment.NewLine, crawlerMetadata.Actors);
                }

                vm.Description = crawlerMetadata.Description;
                AppLogger.Info("[MetadataEditor] Applied crawler description to metadata editor.");
            }
            else
            {
                AppLogger.Info("[MetadataEditor] No metadata payload was returned from crawler.");
            }

            var imageUrl = await mainWindow.ViewModel.TryGetCrawlerThumbnailUrlAsync();
            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                AppLogger.Info("[MetadataEditor] No thumbnail URL discovered on crawler page.");
            }
            else
            {
                byte[] imageBytes = Array.Empty<byte>();
                var downloadFailed = false;

                try
                {
                    imageBytes = await ThumbnailHttpClient.GetByteArrayAsync(imageUrl);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    downloadFailed = true;
                    AppLogger.Error($"[MetadataEditor] Failed to download crawler thumbnail from {imageUrl}.", ex);
                }

                if (imageBytes.Length == 0)
                {
                    if (!downloadFailed)
                    {
                        AppLogger.Info("[MetadataEditor] Downloaded crawler thumbnail was empty.");
                    }
                    else
                    {
                        AppLogger.Info("[MetadataEditor] Skipping empty thumbnail update due to earlier download failure.");
                    }
                }
                else
                {
                    var extension = ".jpg";
                    string? fileName = null;

                    if (Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
                    {
                        fileName = Path.GetFileName(uri.LocalPath);
                        var candidate = Path.GetExtension(fileName);
                        if (!string.IsNullOrWhiteSpace(candidate))
                        {
                            extension = candidate;
                        }
                    }

                    var updated = await vm.UpdateThumbnailFromBytesAsync(imageBytes, extension, string.IsNullOrWhiteSpace(fileName) ? null : fileName);
                    if (!updated)
                    {
                        AppLogger.Info("[MetadataEditor] Thumbnail update from crawler image was not applied.");
                        MessageBox.Show(this, "썸네일을 갱신하지 못했습니다.", "경고", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    else
                    {
                        AppLogger.Info("[MetadataEditor] Thumbnail updated from crawler image.");
                    }
                }
            }
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
