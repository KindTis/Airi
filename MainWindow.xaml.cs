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
using System.Text.RegularExpressions;

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
        private List<VideoInfo> mThumbnailVideoList = new List<VideoInfo>();
        private List<string> mActorListAll = new List<string>();
        private Random mRandom = new Random();

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
            public List<string> actors { get; set; }
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

            _AllBtnEnable(false);
            mDownloadWorker.WorkerReportsProgress = true;
            mDownloadWorker.WorkerSupportsCancellation = true;
            mDownloadWorker.DoWork += new DoWorkEventHandler(_DoWork);
            mDownloadWorker.RunWorkerCompleted += new RunWorkerCompletedEventHandler(_WorkComplete);
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

            mThumbnailVideoList.Clear();
            mActorListAll.Clear();
            mActorListAll.Add("ALL");

            foreach (var video in mAiri.Videos)
            {
                foreach (var actor in video.actors)
                {
                    if (mActorListAll.Contains(actor))
                        continue;
                    mActorListAll.Add(actor);
                }
            }

            mActorListAll.Sort(1, mActorListAll.Count - 1, null);

            Dispatcher.Invoke((Action)(() =>
            {
                lbThumbnailList.ItemsSource = mThumbnailVideoList;
                lbActorList.ItemsSource = mActorListAll;
            }));
            
            _VideoListMapUpdate();
        }

        private void _SaveAiriJson()
        {
            switch ((SortType)mAiri.SortType)
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

            File.WriteAllText(@"Airi.json", JsonConvert.SerializeObject(mAiri, Formatting.Indented));
        }

        private void _DoWork(object sender, DoWorkEventArgs e)
        {
            _LoadArirJson();
            _RemoveRemovedVideo();
            foreach (string dir in mAiri.ParseDirectory)
            {
                _ParseDirectory(dir);
            }
            _VideoListSort((SortType)mAiri.SortType);
            _UpdateMetaData();
            _SaveAiriJson();
        }

        private void _WorkComplete(object sender, RunWorkerCompletedEventArgs e)
        {
            this.Title = "Airi";
            _AllBtnEnable(true);
        }

        private void _RemoveRemovedVideo()
        {
            List<VideoInfo> removeQue = new List<VideoInfo>();
            foreach (var e in mAiri.Videos)
            {
                if (!File.Exists(e.fullPath))
                {
                    removeQue.Add(e);
                    continue;
                }
            }

            foreach (var q in removeQue)
            {
                mVideoListMap.Remove(q.strTitle);
                mAiri.Videos.Remove(q);
            }

            foreach (var video in mAiri.Videos)
            {
                mThumbnailVideoList.Add(video);
            }
            Dispatcher.Invoke((Action)(() =>
            {
                lbThumbnailList.Items.Refresh();
            }));
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
                    dateTime = File.GetCreationTime(fileName),
                    actors = new List<string>()
                });
                mThumbnailVideoList.Add(mAiri.Videos[mAiri.Videos.Count - 1]);

                mVideoListMap.Add(_strTitle, mVideoListMap.Count);
            }

            string[] subdirectoryEntries = Directory.GetDirectories(path);
            foreach (string subdirectory in subdirectoryEntries)
                _ParseDirectory(subdirectory);
        }

        private void _UpdateMetaData()
        {
            Regex jav = new Regex(@"([\w]+)-([\d]+)",
                        RegexOptions.Compiled | RegexOptions.IgnoreCase);
            Regex fc2 = new Regex(@"(fc2-ppv-\d+)",
                        RegexOptions.Compiled | RegexOptions.IgnoreCase);
            foreach (var e in mAiri.Videos)
            {
                Dispatcher.Invoke((Action)(() =>
                {
                    this.Title = "Airi [" + e.strTitle + " 갱신 중...]";
                }));

                bool needDownloadCoverImg = false;
                bool needUpdateMetadata = false;
                string imgName = System.IO.Path.GetFileNameWithoutExtension(e.strImagePath);

                if (imgName == "noimage")
                {
                    needDownloadCoverImg = true;
                    needUpdateMetadata = true;
                }

                if (needDownloadCoverImg || needUpdateMetadata)
                {
                    string url = @"https://onejav.com/search/";
                    string title = "";

                    MatchCollection matches = fc2.Matches(e.strTitle);
                    if (matches.Count > 0)
                    { 
                        url = @"https://onejav.com/torrent/";
                        title = matches.First().Value.ToLower();
                    }
                    else
                    {
                        matches = jav.Matches(e.strTitle);
                        if (matches.Count == 0)
                            continue;
                        title = matches.First().Value;
                    }

                    var html = url + title;
                    var htmlDoc = mWeb.Load(html);
                    var rootNode = htmlDoc.DocumentNode;
                    if (rootNode == null)
                        continue;

                    if (needUpdateMetadata)
                    {
                        _UpdateActorList(rootNode, e.actors);
                        foreach (var actor in e.actors)
                        {
                            if (mActorListAll.Contains(actor))
                                continue;
                            mActorListAll.Add(actor);
                        }
                        mActorListAll.Sort(1, mActorListAll.Count - 1, null);

                        Dispatcher.Invoke((Action)(() =>
                        {
                            lbActorList.Items.Refresh();
                        }));
                    }

                    if (needDownloadCoverImg)
                    {
                        if (_DownloadCoverImg(rootNode, e.strTitle))
                        {
                            e.strImagePath = System.IO.Path.GetFullPath(@"thumb/" + e.strTitle + @".jpg");
                        }
                        Dispatcher.Invoke((Action)(() =>
                        {
                            lbThumbnailList.Items.Refresh();
                        }));
                    }
                }
            }
        }

        private void _UpdateActorList(HtmlNode node, List<string> list)
        {
            var castNode = node.SelectNodes("(//div[contains(@class, 'card-content is-flex')])[1]/div[contains(@class, 'panel')]/a[@href]");
            if (castNode == null)
                return;

            foreach (var actor in castNode)
            {
                list.Add(actor.InnerText);
            }
        }

        private bool _DownloadCoverImg(HtmlNode node, string name)
        {
            var selectNode = node.SelectSingleNode("(//div[contains(@class, 'card mb-3')])[1]");
            if (selectNode == null)
                return false;

            selectNode = selectNode.SelectSingleNode("//img[contains(@class, 'image')]");
            if (selectNode == null)
                return false;

            var imgSrc = selectNode.Attributes["src"].Value;
            try
            {
                using (var imgClient = new WebClient())
                {
                    imgClient.DownloadFile(imgSrc, @"thumb/" + name + @".jpg");
                    return true;
                }
            }
            catch (System.Exception ex)
            {
                Console.WriteLine(ex);
                return false;
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
                        mThumbnailVideoList.Sort((VideoInfo left, VideoInfo right) =>
                        {
                            return left.strTitle.CompareTo(right.strTitle);
                        });
                        break;
                    }
                case SortType.SORT_DESC_NAME:
                    {
                        mThumbnailVideoList.Sort((VideoInfo left, VideoInfo right) =>
                        {
                            return left.strTitle.CompareTo(right.strTitle) * -1;
                        });
                        break;
                    }
                case SortType.SORT_ASC_TIME:
                    {
                        mThumbnailVideoList.Sort((VideoInfo left, VideoInfo right) =>
                        {
                            return left.dateTime.CompareTo(right.dateTime);
                        });
                        break;
                    }
                case SortType.SORT_DESC_TIME:
                    {
                        mThumbnailVideoList.Sort((VideoInfo left, VideoInfo right) =>
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

        private void _AllBtnEnable(bool flag)
        {
            btnUpdateList.IsEnabled = flag;
            btnRandomPlay.IsEnabled = flag;
            btnSortbyTitle.IsEnabled = flag;
            btnSortbyTime.IsEnabled = flag;
        }

        private void lbActorList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var item = sender as ListViewItem;
            string actorName = item.Content as string;
            mThumbnailVideoList.Clear();
            if (actorName == "ALL")
            {
                foreach (var video in mAiri.Videos)
                {
                    mThumbnailVideoList.Add(video);
                }
            }
            else
            {
                foreach (var video in mAiri.Videos)
                {
                    if (video.actors.Contains(actorName))
                    {
                        mThumbnailVideoList.Add(video);
                        continue;
                    }
                }
            }
            lbThumbnailList.Items.Refresh();
        }

        private void lbThumbnailList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var item = sender as ListViewItem;
            Border b = Util.FindByName("Outline", item) as Border;
            b.BorderBrush = Brushes.Red;

            if (mPrevSelectedBorder != null && mPrevSelectedBorder != b)
                mPrevSelectedBorder.BorderBrush = Brushes.Transparent;

            mPrevSelectedBorder = b;

            if (e.ClickCount == 2)
            {
                string fullPath;
                fullPath = mThumbnailVideoList[lbThumbnailList.SelectedIndex].fullPath;

                new Process
                {
                    StartInfo = new ProcessStartInfo(fullPath)
                    {
                        UseShellExecute = true
                    }
                }.Start();
            }
        }

        private void lbThumbnailList_ListViewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete)
            {
                if (MessageBox.Show("파일과 함께 삭제 하시겠습니까?", "확인",
                    MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                {
                    Util.MoveToRecycleBin(mThumbnailVideoList[lbThumbnailList.SelectedIndex].fullPath);
                }

                int index = mVideoListMap[mThumbnailVideoList[lbThumbnailList.SelectedIndex].strTitle];
                mAiri.Videos.RemoveAt(index);
                mThumbnailVideoList.RemoveAt(lbThumbnailList.SelectedIndex);
                _VideoListSort((SortType)mAiri.SortType);
                _SaveAiriJson();
            }
        }

        private void OnBtnClickUpdateList(object sender, RoutedEventArgs e)
        {
            _AllBtnEnable(false);
            mDownloadWorker.RunWorkerAsync();
        }

        private void OnBtnClickRandomPlay(object sender, RoutedEventArgs e)
        {
            string fullPath;
            int rndIdx = mRandom.Next(mThumbnailVideoList.Count);
            fullPath = mThumbnailVideoList[rndIdx].fullPath;

            new Process
            {
                StartInfo = new ProcessStartInfo(fullPath)
                {
                    UseShellExecute = true
                }
            }.Start();
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
