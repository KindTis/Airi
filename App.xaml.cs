using System;
using System.Windows;
using Airi.Infrastructure;

namespace Airi
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        internal bool SuppressMainWindowCreation { get; set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            AppLogger.Initialize(AppDomain.CurrentDomain.BaseDirectory);
            AppLogger.Info("Application starting up.");
            base.OnStartup(e);

            if (!SuppressMainWindowCreation)
            {
                MainWindow = new MainWindow();
                MainWindow.Show();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            AppLogger.Info("Application shutting down.");
            base.OnExit(e);
        }
    }
}
