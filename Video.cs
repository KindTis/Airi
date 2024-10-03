using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace Airi
{
    public class VideoItem
    {
        public string ImagePath { get; set; }
    }

    public partial class MainWindow
    {
        private VideoItem _selectedVideo;
        public ObservableCollection<VideoItem> VideoItems { get; set; }
        public VideoItem SelectedVideoItem
        {
            get { return _selectedVideo; }
            set
            {
                _selectedVideo = value;
                OnPropertyChanged_VideoItems(nameof(SelectedVideoItem));
            }
        }

        // INotifyPropertyChanged 구현
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged_VideoItems(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void _InitVideoItems()
        {
            // 이미지 경로 컬렉션 초기화
            VideoItems = new ObservableCollection<VideoItem>();

            // 실행 파일 기준 절대 경로로 설정
            string resourcesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources");

            // resources 폴더가 존재하는지 확인
            if (Directory.Exists(resourcesPath))
            {
                // 이미지 파일 목록 가져오기
                allImages = Directory.GetFiles(resourcesPath, "*.*", SearchOption.TopDirectoryOnly)
                                     .Where(s => s.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                                 s.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                                 s.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                                     .ToArray();

                // 필요에 따라 초기 이미지를 로드할 수 있습니다.
                // 예를 들어, 프로그램 시작 시 첫 번째 이미지를 표시하려면 다음 코드를 사용하세요:
                // if (allImages.Length > 0)
                // {
                //     ImagePaths.Add(allImages[0]);
                //     imageIndex = 1;
                // }
            }
            else
            {
                // 폴더가 존재하지 않을 경우 처리
                MessageBox.Show("resources 폴더를 찾을 수 없습니다.");
            }
        }
    }
}
