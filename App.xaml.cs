using System;
using System.Threading.Tasks;
using System.Windows;
using Airi.Infrastructure;

namespace Airi
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private AppStartupCoordinator? _startupCoordinator;
        private Task? _startupTask;
        internal bool SuppressMainWindowCreation { get; set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            AppLogger.Initialize(AppDomain.CurrentDomain.BaseDirectory);
            AppLogger.Info("Application starting up.");

            if (!SuppressMainWindowCreation)
            {
                _startupCoordinator = new AppStartupCoordinator(
                    Dispatcher,
                    async cancellationToken => await ThumbnailImageLoader.CreateAsync(
                        ThumbnailPerformanceProbe.Disabled,
                        cancellationToken).ConfigureAwait(false),
                    loader => new MainWindow(loader),
                    exception => AppLogger.Error("Application startup failed.", exception),
                    exitCode => Shutdown(exitCode));
                _startupTask = _startupCoordinator.StartAsync();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _startupCoordinator?.Cancel();
            AppLogger.Info("Application shutting down.");
            base.OnExit(e);
            _startupCoordinator?.Dispose();
        }
    }
}
