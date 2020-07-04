using HtmlAgilityPack;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Airi
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow
    {
        HtmlWeb mWeb = new HtmlWeb();
        private AiriJSON mAiri = null;
        private BackgroundWorker mDownloadWorker = new BackgroundWorker();
        private Border mPrevSelectedBorder = null;
        private Dictionary<string, int> mVideoListMap = new Dictionary<string, int>();

        public enum SortType : int
        {
            SORT_ASC_NAME = 0,
            SORT_DESC_NAME,
            SORT_ASC_TIME,
            SORT_DESC_TIME
        }

        public class VideoInfo
        {
            // binded
            public string strImagePath { get; set; }
            public string strTitle { get; set; }

            // ext
            public string fullPath { get; set; }
            public DateTime dateTime { get; set; }
        }

        public class AiriJSON
        {
            public int SortType { get; set; }
            public List<string> ParseDirectory { get; set; }
            public List<VideoInfo> Videos { get; set; }
        }

        public MainWindow()
        {
            InitializeComponent();
            Directory.CreateDirectory("thumb");

            _LoadArirJson();

            mDownloadWorker.WorkerReportsProgress = true;
            mDownloadWorker.WorkerSupportsCancellation = true;
            mDownloadWorker.DoWork += new DoWorkEventHandler(_DoWork);
            mDownloadWorker.RunWorkerAsync();
        }

        private void _LoadArirJson()
        {
            if (File.Exists(@"Airi.json"))
            {
                mAiri = JsonConvert.DeserializeObject<AiriJSON>(File.ReadAllText(@"Airi.json"));
            }

            if (mAiri == null)
            {
                mAiri = new AiriJSON();
                mAiri.SortType = (int)SortType.SORT_ASC_NAME;
                mAiri.ParseDirectory = new List<string>();
                mAiri.Videos = new List<VideoInfo>();
                mAiri.ParseDirectory.Add(@"e:\Fascinating\Jap");
            }

            lbThumbnailList.ItemsSource = mAiri.Videos;
            _VideoListMapUpdate();
        }

        private void _SaveAiriJson()
        {
            File.WriteAllText(@"Airi.json", JsonConvert.SerializeObject(mAiri, Formatting.Indented));
        }

        private void _DoWork(object sender, DoWorkEventArgs e)
        {
            foreach (string dir in mAiri.ParseDirectory)
            {
                _ParseDirectory(dir);
            }
            _VideoListSort((SortType)mAiri.SortType);
            _UpdateCoverImg();
            _SaveAiriJson();
        }

        private void _ParseDirectory(string path)
        {
            string[] fileEntries = Directory.GetFiles(path);
            foreach (string fileName in fileEntries)
            {
                string extension = System.IO.Path.GetExtension(fileName);
                if (!string.Equals(extension, ".mp4", StringComparison.CurrentCultureIgnoreCase)
                    && !string.Equals(extension, ".mkv", StringComparison.CurrentCultureIgnoreCase)
                    && !string.Equals(extension, ".avi", StringComparison.CurrentCultureIgnoreCase)
                    && !string.Equals(extension, ".wmv", StringComparison.CurrentCultureIgnoreCase))
                    continue;

                string _strTitle = System.IO.Path.GetFileNameWithoutExtension(fileName);
                if (mVideoListMap.ContainsKey(_strTitle))
                    continue;

                mAiri.Videos.Add(new VideoInfo()
                {
                    strImagePath = System.IO.Path.GetFullPath(@"thumb/noimage.jpg"),
                    strTitle = _strTitle,
                    fullPath = fileName,
                    dateTime = File.GetCreationTime(fileName)
                });
            }

            string[] subdirectoryEntries = Directory.GetDirectories(path);
            foreach (string subdirectory in subdirectoryEntries)
                _ParseDirectory(subdirectory);
        }

        private void _UpdateCoverImg()
        {
            List<VideoInfo> removeQue = new List<VideoInfo>();
            foreach (var e in mAiri.Videos)
            {
                if (!File.Exists(e.fullPath))
                {
                    removeQue.Add(e);
                    continue;
                }

                string imgName = System.IO.Path.GetFileNameWithoutExtension(e.strImagePath);
                if (imgName != "noimage")
                    continue;

                var html = @"http://www.javlibrary.com/en/vl_searchbyid.php?keyword=" + e.strTitle;
                var htmlDoc = mWeb.Load(html);
                if (_ParsingHTMLPage(htmlDoc.DocumentNode, e.strTitle))
                {
                    e.strImagePath = System.IO.Path.GetFullPath(@"thumb/" + e.strTitle + @".jpg");
                }

                Dispatcher.Invoke((Action)(() =>
                {
                    lbThumbnailList.Items.Refresh();
                }));
            }

            foreach (var q in removeQue)
            {
                mVideoListMap.Remove(q.strTitle);
                mAiri.Videos.Remove(q);
            }
        }

        private bool _ParsingHTMLPage(HtmlNode node, string name)
        {
            var selectNode = node.SelectSingleNode("//text()[contains(., 'ID Search Result')]/..");
            if (selectNode != null)
            {
                selectNode = node.SelectSingleNode("//div[contains(@class, 'videos')]");
                var link = selectNode.SelectSingleNode(".//a[@href]");
                if (link == null)
                    return false;
                var href = link.Attributes["href"].Value;
                var htmlDoc = mWeb.Load(@"http://www.javlibrary.com/en/" + href);
                return _ParsingHTMLPage(htmlDoc.DocumentNode, name);
            }
            else
            {
                selectNode = node.SelectSingleNode("//img[contains(@id, 'video_jacket_img')]");
                var imgSrc = selectNode.Attributes["src"].Value;

                try
                {
                    using (var imgClient = new WebClient())
                    {
                        imgClient.DownloadFile("http:" + imgSrc, @"thumb/" + name + @".jpg");
                        return true;
                    }
                }
                catch (System.Exception ex)
                {
                    Console.WriteLine(ex);
                    return false;
                }
            }
        }

        private void _VideoListMapUpdate()
        {
            int index = 0;
            mVideoListMap.Clear();
            foreach (var e in mAiri.Videos)
            {
                mVideoListMap.Add(e.strTitle, index++);
            }
        }

        private void _VideoListSort(SortType sortType)
        {
            switch (sortType)
            {
                case SortType.SORT_ASC_NAME:
                    {
                        mAiri.Videos.Sort((VideoInfo left, VideoInfo right) =>
                        {
                            return left.strTitle.CompareTo(right.strTitle);
                        });
                        break;
                    }
                case SortType.SORT_DESC_NAME:
                    {
                        mAiri.Videos.Sort((VideoInfo left, VideoInfo right) =>
                        {
                            return left.strTitle.CompareTo(right.strTitle) * -1;
                        });
                        break;
                    }
                case SortType.SORT_ASC_TIME:
                    {
                        mAiri.Videos.Sort((VideoInfo left, VideoInfo right) =>
                        {
                            return left.dateTime.CompareTo(right.dateTime);
                        });
                        break;
                    }
                case SortType.SORT_DESC_TIME:
                    {
                        mAiri.Videos.Sort((VideoInfo left, VideoInfo right) =>
                        {
                            return left.dateTime.CompareTo(right.dateTime) * -1;
                        });
                        break;
                    }
            }
            _VideoListMapUpdate();

            Dispatcher.Invoke((Action)(() =>
            {
                lbThumbnailList.Items.Refresh();
            }));
        }

        public void ListViewItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var item = sender as ListViewItem;
            Border b = Util.FindByName("Outline", item) as Border;
            b.BorderBrush = Brushes.Red;

            if (mPrevSelectedBorder != null && mPrevSelectedBorder != b)
                mPrevSelectedBorder.BorderBrush = Brushes.Transparent;

            mPrevSelectedBorder = b;

            if (e.ClickCount == 2)
            {
                new Process
                {
                    StartInfo = new ProcessStartInfo(mAiri.Videos[lbThumbnailList.SelectedIndex].fullPath)
                    {
                        UseShellExecute = true
                    }
                }.Start();
            }
        }

        private void ListViewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete)
            {
                if (MessageBox.Show("파일과 함께 삭제 하시겠습니까?", "확인", MessageBoxButton.YesNo)
                    == MessageBoxResult.Yes)
                {
                    Util.MoveToRecycleBin(mAiri.Videos[lbThumbnailList.SelectedIndex].fullPath);
                }
                mAiri.Videos.RemoveAt(lbThumbnailList.SelectedIndex);
                _VideoListSort((SortType)mAiri.SortType);
                _SaveAiriJson();
            }
        }

        private void OnBtnClickNameSort(object sender, RoutedEventArgs e)
        {
            if (mAiri.SortType == (int)SortType.SORT_ASC_NAME)
            {
                mAiri.SortType = (int)SortType.SORT_DESC_NAME;
            }
            else
            {
                mAiri.SortType = (int)SortType.SORT_ASC_NAME;
            }

            _VideoListSort((SortType)mAiri.SortType);
            _SaveAiriJson();
        }

        private void OnBtnClickTimeSort(object sender, RoutedEventArgs e)
        {
            if (mAiri.SortType == (int)SortType.SORT_ASC_TIME)
            {
                mAiri.SortType = (int)SortType.SORT_DESC_TIME;
            }
            else
            {
                mAiri.SortType = (int)SortType.SORT_ASC_TIME;
            }

            _VideoListSort((SortType)mAiri.SortType);
            _SaveAiriJson();
        }
    }
}
