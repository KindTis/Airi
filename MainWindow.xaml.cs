using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;

namespace Airi
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private VideoMetaParser videoMetaParser = null;
        private int videoItemIndex = 0;

        // resources 폴더 내의 모든 이미지 파일 경로 리스트
        private string[] allImages;

        public MainWindow()
        {
            InitializeComponent();
            _InitVideoItems();

            videoMetaParser = new VideoMetaParser();
            // 데이터 바인딩을 위해 DataContext 설정
            DataContext = this;
        }

        #region Event_Function
        private async void AddButton_Click(object sender, RoutedEventArgs e)
        {
            // TextBox에서 검색어 가져오기
            string searchKeyword = SearchTextBox.Text;

            if (!string.IsNullOrWhiteSpace(searchKeyword))
            {
                await videoMetaParser.SearchAsync(searchKeyword);
                MessageBox.Show("완료");
            }
            else
            {
                MessageBox.Show("검색어를 입력해주세요.");
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedVideoItem != null)
            {
                // ImagePaths에서 제거
                VideoItems.Remove(SelectedVideoItem);

                // 선택된 이미지 초기화
                SelectedVideoItem = null;
            }
            else
            {
                MessageBox.Show("삭제할 이미지를 선택해주세요.");
            }
        }
        #endregion
    }
}
