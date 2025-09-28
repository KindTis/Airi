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
                    MessageBox.Show(this, "\uD06C\uB864\uB7EC \uD398\uC774\uC9C0\uC5D0\uC11C \uC774\uBBF8\uC9C0\uB97C \uCC3E\uC9C0 \uBABB\uD588\uC2B5\uB2C8\uB2E4.", "\uC548\uB0B4", MessageBoxButton.OK, MessageBoxImage.Information);
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
                        MessageBox.Show(this, $"\uC774\uBBF8\uC9C0\uB97C \uB2E4\uC6B4\uB85C\uB4DC\uD558\uC9C0 \uBABB\uD588\uC2B5\uB2C8\uB2E4.\n{ex.Message}", "\uC624\uB958", MessageBoxButton.OK, MessageBoxImage.Error);
                    }

                    if (imageBytes.Length == 0)
                    {
                        if (!downloadFailed)
                        {
                            MessageBox.Show(this, "\uB2E4\uC6B4\uB85C\uB4DC\uD55C \uC774\uBBF8\uC9C0\uAC00 \uBE44\uC5B4 \uC788\uC2B5\uB2C8\uB2E4.", "\uC548\uB0B4", MessageBoxButton.OK, MessageBoxImage.Information);
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
                            MessageBox.Show(this, "\uC30D\uB124\uC77C\uC744 \uAC31\uC2E0\uD558\uC9C0 \uBABB\uD588\uC2B5\uB2C8\uB2E4.", "\uACBD\uACE0", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"\uD398\uC774\uC9C0\uB97C \uD30C\uC2F1\uD558\uB294 \uC911 \uC624\uB958\uAC00 \uBC1C\uC0DD\uD588\uC2B5\uB2C8\uB2E4.\n{ex.Message}", "\uC624\uB958", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
