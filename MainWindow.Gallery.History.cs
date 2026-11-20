using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Airi.Infrastructure;
using Airi.ViewModels;

namespace Airi;

public partial class MainWindow
{
    // Each command captures the row it describes. It must never turn into the next undo.
    internal FrameworkElement CreateGalleryHistoryBody(out Action dispose)
    {
        var body = new StackPanel();
        var rowSubscriptions = new List<Action>();
        void ClearRows()
        {
            foreach (var unsubscribe in rowSubscriptions) unsubscribe();
            rowSubscriptions.Clear();
            body.Children.Clear();
        }
        void BuildRows()
        {
            ClearRows();
            if (ViewModel.ChangeHistory.Count == 0)
                body.Children.Add(new TextBlock { Text = "변경 내역이 없습니다.", Foreground = (System.Windows.Media.Brush)FindResource("GalleryMuted") });
            foreach (var history in ViewModel.ChangeHistory)
            {
                var row = new StackPanel { Margin = new Thickness(0, 14, 0, 14), Tag = history.Id };
                var summary = new TextBlock { TextWrapping = TextWrapping.Wrap };
                summary.SetBinding(TextBlock.TextProperty, new Binding(nameof(GalleryHistoryItem.Summary)) { Source = history });
                row.Children.Add(summary);
                row.Children.Add(new TextBlock { Text = history.Timestamp.ToString("yyyy-MM-dd HH:mm"), Foreground = (System.Windows.Media.Brush)FindResource("GalleryMuted"), Margin = new Thickness(0, 4, 0, 0) });
                bool CanUndoThisRow() => history.CanUndo && ViewModel.GalleryActionTask.IsCompleted && ViewModel.UndoCommand.CanExecute(null) && ReferenceEquals(ViewModel.ChangeHistory.FirstOrDefault(item => item.CanUndo), history);
                var command = new RelayCommand(_ => { if (CanUndoThisRow()) ViewModel.UndoCommand.Execute(null); }, _ => CanUndoThisRow());
                var undo = new Button { Content = "되돌리기", Command = command, Tag = history.Id, Style = (Style)FindResource("GalleryToolbarButton"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
                System.Windows.Automation.AutomationProperties.SetName(undo, history.Summary + " 되돌리기");
                PropertyChangedEventHandler changed = (_, _) => command.RaiseCanExecuteChanged();
                EventHandler canExecuteChanged = (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(command.RaiseCanExecuteChanged));
                history.PropertyChanged += changed;
                ViewModel.UndoCommand.CanExecuteChanged += canExecuteChanged;
                rowSubscriptions.Add(() => { history.PropertyChanged -= changed; ViewModel.UndoCommand.CanExecuteChanged -= canExecuteChanged; });
                row.Children.Add(undo);
                var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (System.Windows.Media.Brush)FindResource("GalleryDanger") };
                error.SetBinding(TextBlock.TextProperty, new Binding(nameof(GalleryHistoryItem.Error)) { Source = history });
                error.SetBinding(VisibilityProperty, new Binding(nameof(GalleryHistoryItem.Error)) { Source = history, Converter = (IValueConverter)FindResource("GalleryVisibility") });
                row.Children.Add(error);
                body.Children.Add(row);
            }
        }
        NotifyCollectionChangedEventHandler collectionChanged = (_, _) => BuildRows();
        ViewModel.ChangeHistory.CollectionChanged += collectionChanged;
        BuildRows();
        dispose = () => { ViewModel.ChangeHistory.CollectionChanged -= collectionChanged; ClearRows(); };
        return body;
    }
}
