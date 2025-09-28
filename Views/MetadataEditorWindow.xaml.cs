using Airi;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
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

        private async void On141JavClick(object sender, RoutedEventArgs e)
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
                MessageBox.Show(this, "크롤러에 접근할 수 없습니다.", "안내", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var url = $"https://www.141jav.com/search/{Uri.EscapeDataString(normalized)}";
            var navigated = await mainWindow.ViewModel.NavigateCrawlerToAsync(url);
            if (!navigated)
            {
                MessageBox.Show(this, "크롤러가 실행 중인지 확인해주세요.", "안내", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private async void OnParseClick(object sender, RoutedEventArgs e)
        {
            if (Owner is not MainWindow mainWindow)
            {
                return;
            }

            if (DataContext is not MetadataEditorViewModel vm)
            {
                return;
            }

            try
            {
                var imageUrl = await mainWindow.ViewModel.TryGetCrawlerThumbnailUrlAsync();
                if (string.IsNullOrWhiteSpace(imageUrl))
                {
                    MessageBox.Show(this, "크롤러 페이지에서 이미지를 찾지 못했습니다.", "안내", MessageBoxButton.OK, MessageBoxImage.Information);
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
                        MessageBox.Show(this, $"이미지를 다운로드하지 못했습니다.\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                    }

                    if (imageBytes.Length == 0)
                    {
                        if (!downloadFailed)
                        {
                            MessageBox.Show(this, "다운로드한 이미지가 비어 있습니다.", "안내", MessageBoxButton.OK, MessageBoxImage.Information);
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
                            MessageBox.Show(this, "쌍네일을 갱신하지 못했습니다.", "경고", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"페이지를 파싱하는 중 오류가 발생했습니다.\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
