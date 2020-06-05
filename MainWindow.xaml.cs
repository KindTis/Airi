using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
        public class CompareListItem
        {
            public string strImagePath { get; set; }
            public string strTitle { get; set; }
        }

        private List<CompareListItem> mThumbnailList = new List<CompareListItem>();
        public MainWindow()
        {
            InitializeComponent();
            Directory.CreateDirectory("thumb");
            lbThumbnailList.ItemsSource = mThumbnailList;
            ShowImage();
        }

        private void ShowImage()
        {
            for (int i = 0; i < 100; i++)
            {
                mThumbnailList.Add(new CompareListItem()
                {
                    strImagePath = System.IO.Path.GetFullPath(@"thumb/img.jpg"),
                    strTitle = "Title"
                });
            }
            lbThumbnailList.Items.Refresh();
        }

        private FrameworkElement FindByName(string name, FrameworkElement root)
        {
            Stack<FrameworkElement> tree = new Stack<FrameworkElement>();
            tree.Push(root);

            while (tree.Count > 0)
            {
                FrameworkElement current = tree.Pop();
                if (current.Name == name)
                    return current;

                int count = VisualTreeHelper.GetChildrenCount(current);
                for (int i = 0; i < count; ++i)
                {
                    DependencyObject child = VisualTreeHelper.GetChild(current, i);
                    if (child is FrameworkElement)
                        tree.Push((FrameworkElement)child);
                }
            }

            return null;
        }

        private void ListViewItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var item = sender as ListViewItem;
            Border b = FindByName("Outline", item) as Border;
            b.BorderBrush = Brushes.Red;

            if (e.ClickCount == 2)
                b.BorderBrush = Brushes.Blue;
        }
    }
}
