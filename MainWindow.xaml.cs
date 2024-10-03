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
using System.Net.Http;
using System.Collections.ObjectModel;
using System.Windows.Automation;
using IronPython.Hosting;
using Microsoft.Scripting.Hosting;
using static IronPython.Modules._ast;
using Microsoft.Scripting.Utils;

namespace Airi
{
    public class AiriJSON
    {
        public int SortType { get; set; }
        public List<string> ParseDirectory { get; set; }
        public List<Video> Videos { get; set; }
    }

    // json 파일의 Videos 배열의 각 요소를 나타내는 클래스
    public class Video
    {
        public string strImagePath { get; set; }
        public string strTitle { get; set; }
        public string fullPath { get; set; }
        public DateTime dateTime { get; set; }
        public List<string> actors { get; set; }
    }

    public class VideoThumbnails
    {
        public string strImagePath { get; set; }
        public string strTitle { get; set; }
        public string fullPath { get; set; }
        public DateTime dateTime { get; set; }
    }

    public enum SortType : int
    {
        SORT_ASC_NAME = 0,
        SORT_DESC_NAME,
        SORT_ASC_TIME,
        SORT_DESC_TIME
    }

    public partial class MainWindow
    {
        AiriJSON mAiriJSON;
        private List<string> mSortedActorsList = new List<string>();
        private ObservableCollection<string> mActorsList = new ObservableCollection<string>();
        private ObservableCollection<VideoThumbnails> mVideoThumbnailList = new ObservableCollection<VideoThumbnails>();
        private Dictionary<string, int> mVideoListMap = new Dictionary<string, int>();
        private List<int> mUpdatedVideoIndex = new List<int>();

        public MainWindow()
        {
            InitializeComponent();
            lvVideoList.ItemsSource = mVideoThumbnailList;
            lvActorList.ItemsSource = mActorsList;
            Task.Run(() => _InitialFlowWork());
        }

        private void _InitialFlowWork()
        {
            _LoadAiriJSON();
            _UpdateVideoListMap();
            foreach (var dir in mAiriJSON.ParseDirectory)
                _ParseVideoDirectory(dir);
            _SortAiriJSONVideos();
            _SaveAiriJSON();
            _UpdateVideoListMap();
            _UpdateThumbnails();
            _UpdateVideoMetaData();
            _LoadAiriJSON();
            _UpdateUpdatedVideo();
        }

        private void _LoadAiriJSON()
        {
            string jsonFilePath = "Airi.json";
            using (StreamReader reader = new StreamReader(jsonFilePath))
            {
                string jsonString = reader.ReadToEnd();
                mAiriJSON = JsonConvert.DeserializeObject<AiriJSON>(jsonString);
            }
        }

        private void _UpdateVideoListMap()
        {
            mVideoListMap.Clear();
            int videoListIndex = 0;
            foreach (Video video in mAiriJSON.Videos)
            {
                mVideoListMap.Add(video.fullPath, videoListIndex++);
            }
        }

        private void _ParseVideoDirectory(string parseDir)
        {
            string[] extensions = { ".mp4", ".mkv", ".avi" };
            List<string> filePaths = new DirectoryInfo(parseDir).GetFiles("*",
                SearchOption.AllDirectories)
                .Where(f => extensions.Contains(f.Extension)).Select(f => f.FullName).ToList();

            foreach (string file in filePaths)
            {
                if (!mVideoListMap.ContainsKey(file))
                {
                    string _strTitle = System.IO.Path.GetFileNameWithoutExtension(file);
                    mAiriJSON.Videos.Add(new Video()
                    {
                        strImagePath = @"thumb/noimage.jpg",
                        strTitle = _strTitle,
                        fullPath = file,
                        dateTime = File.GetCreationTime(file),
                        actors = new List<string>()
                    });
                }
            }
        }

        private void _SortAiriJSONVideos()
        {
            switch ((SortType)mAiriJSON.SortType)
            {
                case SortType.SORT_ASC_NAME:
                    {
                        mAiriJSON.Videos.Sort((Video left, Video right) =>
                        {
                            return left.strTitle.CompareTo(right.strTitle);
                        });
                        break;
                    }
                case SortType.SORT_DESC_NAME:
                    {
                        mAiriJSON.Videos.Sort((Video left, Video right) =>
                        {
                            return left.strTitle.CompareTo(right.strTitle) * -1;
                        });
                        break;
                    }
                case SortType.SORT_ASC_TIME:
                    {
                        mAiriJSON.Videos.Sort((Video left, Video right) =>
                        {
                            return left.dateTime.CompareTo(right.dateTime);
                        });
                        break;
                    }
                case SortType.SORT_DESC_TIME:
                    {
                        mAiriJSON.Videos.Sort((Video left, Video right) =>
                        {
                            return left.dateTime.CompareTo(right.dateTime) * -1;
                        });
                        break;
                    }
            }
        }

        private void _SaveAiriJSON()
        {
            string jsonString = JsonConvert.SerializeObject(mAiriJSON, Formatting.Indented);
            File.WriteAllText("Airi.json", jsonString);
        }

        private void _UpdateThumbnails()
        {
            Dispatcher.Invoke((Action)(() =>
            {
                mActorsList.Clear();
                mVideoThumbnailList.Clear();
            }));

            mSortedActorsList.Clear();
            mSortedActorsList.Add("ALL");
            foreach (Video video in mAiriJSON.Videos)
            {
                foreach (string actor in video.actors)
                {
                    if (!mSortedActorsList.Contains(actor))
                        mSortedActorsList.Add(actor);
                }
                
                Dispatcher.Invoke((Action)(() =>
                {
                    mVideoThumbnailList.Add(new VideoThumbnails()
                    {
                        strImagePath = System.IO.Path.GetFullPath(video.strImagePath),
                        strTitle = video.strTitle,
                        fullPath = video.fullPath,
                        dateTime = video.dateTime
                    });
                }));
            }

            mSortedActorsList.Sort(1, mSortedActorsList.Count - 1, null);

            Dispatcher.Invoke((Action)(() =>
            {
                mActorsList.AddRange(mSortedActorsList);
            }));
        }

        private void _UpdateVideoMetaData()
        {
            ProcessStartInfo StartInfo = new ProcessStartInfo()
            {
                FileName = "python.exe",
                Arguments = "VideoMetaDataUpdater.py",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            mUpdatedVideoIndex.Clear();
            string pattern = @"\[(\d+)\]";
            using (Process process = Process.Start(StartInfo))
            {
                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data == null) { return; }
                    Match match = Regex.Match(e.Data, pattern);
                    if (match.Success)
                    {
                        string captured = match.Groups[1].Value;
                        mUpdatedVideoIndex.Add(int.Parse(captured));
                    }
                };
                process.BeginOutputReadLine();
                process.WaitForExit();
            }
        }

        private void _UpdateUpdatedVideo()
        {
            foreach (int idx in mUpdatedVideoIndex)
            {
                Dispatcher.Invoke((Action)(() =>
                {
                    mVideoThumbnailList[idx] = new VideoThumbnails()
                    {
                        strImagePath = System.IO.Path.GetFullPath(mAiriJSON.Videos[idx].strImagePath),
                        strTitle = mAiriJSON.Videos[idx].strTitle,
                        fullPath = mAiriJSON.Videos[idx].fullPath,
                        dateTime = mAiriJSON.Videos[idx].dateTime
                    };
                }));

                foreach (string actor in mAiriJSON.Videos[idx].actors)
                {
                    if (!mSortedActorsList.Contains(actor))
                        mSortedActorsList.Add(actor);
                }
            }

            mSortedActorsList.Sort(1, mSortedActorsList.Count - 1, null);

            Dispatcher.Invoke((Action)(() =>
            {
                mActorsList.Clear();
                mActorsList.AddRange(mSortedActorsList);
            }));
        }

        private void lvVideoList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var element = e.Source as ListView;
            var selectedItem = element.SelectedValue as Video;
            new Process
            {
                StartInfo = new ProcessStartInfo(selectedItem.fullPath)
                {
                    UseShellExecute = true
                }
            }.Start();
        }
    }
}
