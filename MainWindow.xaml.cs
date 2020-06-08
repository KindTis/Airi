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
        private List<CompareListItem> mThumbnailList = new List<CompareListItem>();
        private BackgroundWorker mDownloadWorker = new BackgroundWorker();
        private Border mPrevSelectedBorder = null;

        public class CompareListItem
        {
            // binded
            public string strImagePath { get; set; }
            public string strTitle { get; set; }

            // ext
            public string fullPath { get; set; }
        }        

        public MainWindow()
        {
            InitializeComponent();
            Directory.CreateDirectory("thumb");
            lbThumbnailList.ItemsSource = mThumbnailList;

            mDownloadWorker.WorkerReportsProgress = true;
            mDownloadWorker.WorkerSupportsCancellation = true;
            mDownloadWorker.DoWork += new DoWorkEventHandler(_DoWork);
            mDownloadWorker.RunWorkerAsync();
            //File.WriteAllText(@"list.json", JsonConvert.SerializeObject(mThumbnailList));
        }

        public void _DoWork(object sender, DoWorkEventArgs e)
        {
            _ParseDirectory(@"e:\");
            _UpdateCoverImg();
        }

        private void _ParseDirectory(string path)
        {
            string[] fileEntries = Directory.GetFiles(path);
            foreach (string fileName in fileEntries)
            {
                mThumbnailList.Add(new CompareListItem()
                {
                    strImagePath = System.IO.Path.GetFullPath(@"thumb/noimage.jpg"),
                    strTitle = System.IO.Path.GetFileNameWithoutExtension(fileName),
                    fullPath = fileName
                });
            }

            string[] subdirectoryEntries = Directory.GetDirectories(path);
            foreach (string subdirectory in subdirectoryEntries)
                _ParseDirectory(subdirectory);
        }

        private void _UpdateCoverImg()
        {
            foreach (var e in mThumbnailList)
            {
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

        private void ListViewItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
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
                    StartInfo = new ProcessStartInfo(mThumbnailList[lbThumbnailList.SelectedIndex].fullPath)
                    {
                        UseShellExecute = true
                    }
                }.Start();
            }
        }
    }
}
