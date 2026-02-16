using System;
using System.IO;
using System.Text;

namespace Airi.Infrastructure
{
    public static class AppLogger
    {
        private static readonly object Sync = new();
        private static string _logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log");
        private static string _logPath = string.Empty;
        private static bool _initialized;

        public static void Initialize(string? baseDirectory = null)
        {
            lock (Sync)
            {
                if (!string.IsNullOrWhiteSpace(baseDirectory))
                {
                    _logDirectory = Path.Combine(baseDirectory, "log");
                }

                if (_initialized)
                {
                    return;
                }

                Directory.CreateDirectory(_logDirectory);
                _logPath = Path.Combine(_logDirectory, $"airi_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}.log");
                var header = new StringBuilder()
                    .AppendLine()
                    .AppendLine(new string('-', 60))
                    .AppendLine($"{DateTime.UtcNow:o} [INFO] Logger initialized at {_logPath}")
                    .AppendLine(new string('-', 60))
                    .ToString();
                File.AppendAllText(_logPath, header);
                _initialized = true;
            }
        }

        public static void Info(string message) => Write("INFO", message, null);

        public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

        private static void Write(string level, string message, Exception? exception)
        {
            lock (Sync)
            {
                if (!_initialized)
                {
                    Initialize();
                }

                var builder = new StringBuilder()
                    .AppendLine($"{DateTime.UtcNow:o} [{level}] {message}");

                if (exception is not null)
                {
                    builder.AppendLine(exception.ToString());
                }

                File.AppendAllText(_logPath, builder.ToString());
            }
        }
    }
}
