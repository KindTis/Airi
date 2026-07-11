using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Airi.Domain;
using Airi.Infrastructure;

namespace Airi.Services
{
    internal enum LibraryStoreSaveStage
    {
        BeforeTemporaryWrite,
        AfterTemporaryFlush,
        BeforeCommit,
        AfterCommit
    }

    internal enum LibraryStoreIoStage
    {
        StaleTemporaryEnumeration,
        DestinationExistence,
        DestinationOpen,
        BeforeDeserialize,
        PersistenceProjection,
        TemporaryOpen,
        Serialize,
        Flush,
        Commit
    }

    public class LibraryStore : ILibraryStore
    {
        private const int CurrentVersion = 1;
        private static readonly TargetFolder DefaultTarget = new(
            "./Videos",
            new[] { "*.mp4", "*.mkv", "*.avi", "*.wmv" },
            Array.Empty<string>(),
            null);

        private readonly string _filePath;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly Action<LibraryStoreSaveStage, string, int>? _saveStage;
        private readonly Action<LibraryStoreIoStage, int>? _ioStage;

        public LibraryStore(string? customFilePath = null)
            : this(customFilePath, null, null)
        {
        }

        internal LibraryStore(
            string? customFilePath,
            Action<LibraryStoreSaveStage, string, int>? saveStage,
            Action<LibraryStoreIoStage, int>? ioStage)
        {
            _filePath = customFilePath ?? ResolveDefaultPath();
            _jsonOptions = BuildOptions();
            _saveStage = saveStage;
            _ioStage = ioStage;
        }

        public string FilePath => _filePath;

        public Task<LibraryData> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.Run(() => LoadCoreAsync(cancellationToken), cancellationToken);
        }

        public Task SaveAsync(LibraryData library, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(library);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.Run(() => SaveCoreAsync(library, cancellationToken), cancellationToken);
        }

        private async Task<LibraryData> LoadCoreAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CleanupOwnedStaleTemporaryFiles();
            ObserveIo(LibraryStoreIoStage.DestinationExistence);
            if (!File.Exists(_filePath))
            {
                AppLogger.Info($"Library file not found. Creating new library at {_filePath}.");
                var defaultData = Normalize(CreateDefaultLibrary());
                await SaveCoreAsync(defaultData, cancellationToken).ConfigureAwait(false);
                return defaultData;
            }

            LibraryData? data;
            try
            {
                AppLogger.Info($"Loading library from {_filePath}.");
                ObserveIo(LibraryStoreIoStage.DestinationOpen);
                await using var stream = new FileStream(
                    _filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                ObserveIo(LibraryStoreIoStage.BeforeDeserialize);
                data = await JsonSerializer.DeserializeAsync<LibraryData>(
                    stream,
                    _jsonOptions,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                AppLogger.Error("Library JSON is invalid; resetting to defaults.", ex);
                return await ResetWithDefaultsCoreAsync(cancellationToken).ConfigureAwait(false);
            }

            if (data is null)
            {
                AppLogger.Error("Library JSON contained null; resetting to defaults.");
                return await ResetWithDefaultsCoreAsync(cancellationToken).ConfigureAwait(false);
            }

            return Normalize(data);
        }

        private async Task SaveCoreAsync(LibraryData library, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(directory);
            ObserveIo(LibraryStoreIoStage.PersistenceProjection);
            var persistenceProjection = new LibraryData
            {
                Version = CurrentVersion,
                Targets = library.Targets.ToList(),
                Videos = library.Videos.ToList()
            };
            var tempPath = Path.Combine(
                directory,
                $".{Path.GetFileName(_filePath)}.airi-tmp-{Guid.NewGuid():D}");

            AppLogger.Info($"Saving library to {_filePath} (videos: {persistenceProjection.Videos.Count}).");
            try
            {
                ObserveSave(LibraryStoreSaveStage.BeforeTemporaryWrite, tempPath);
                ObserveIo(LibraryStoreIoStage.TemporaryOpen);
                await using (var stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    ObserveIo(LibraryStoreIoStage.Serialize);
                    await JsonSerializer.SerializeAsync(
                        stream,
                        persistenceProjection,
                        _jsonOptions,
                        cancellationToken).ConfigureAwait(false);
                    ObserveIo(LibraryStoreIoStage.Flush);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                ObserveSave(LibraryStoreSaveStage.AfterTemporaryFlush, tempPath);
                cancellationToken.ThrowIfCancellationRequested();
                ObserveSave(LibraryStoreSaveStage.BeforeCommit, tempPath);
                ObserveIo(LibraryStoreIoStage.Commit);

                if (File.Exists(_filePath))
                {
                    File.Replace(tempPath, _filePath, null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, _filePath);
                }

                ObserveSave(LibraryStoreSaveStage.AfterCommit, tempPath);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch (Exception cleanupException) when (
                    cleanupException is IOException or UnauthorizedAccessException)
                {
                    AppLogger.Error(
                        "Failed to clean up owned library temporary file; next load will retry.",
                        cleanupException);
                }
            }
        }

        private static string ResolveDefaultPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "videos.json");
        }

        private static JsonSerializerOptions BuildOptions()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            options.Converters.Add(new DateOnlyJsonConverter());
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }

        private async Task<LibraryData> ResetWithDefaultsCoreAsync(CancellationToken cancellationToken)
        {
            AppLogger.Info("Resetting library to default state.");
            var data = Normalize(CreateDefaultLibrary());
            await SaveCoreAsync(data, cancellationToken).ConfigureAwait(false);
            return data;
        }

        private void CleanupOwnedStaleTemporaryFiles()
        {
            ObserveIo(LibraryStoreIoStage.StaleTemporaryEnumeration);
            var directory = Path.GetDirectoryName(_filePath)!;
            if (!Directory.Exists(directory))
            {
                return;
            }

            var prefix = $".{Path.GetFileName(_filePath)}.airi-tmp-";
            var failures = 0;
            foreach (var path in Directory.EnumerateFiles(directory, prefix + "*"))
            {
                var name = Path.GetFileName(path);
                var suffix = name.Substring(prefix.Length);
                if (!Guid.TryParse(suffix, out _))
                {
                    continue;
                }

                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failures++;
                }
            }

            if (failures > 0)
            {
                AppLogger.Error($"Failed to clean up {failures} owned stale library temporary file(s).");
            }
        }

        private void ObserveSave(LibraryStoreSaveStage stage, string tempPath) =>
            _saveStage?.Invoke(stage, tempPath, Environment.CurrentManagedThreadId);

        private void ObserveIo(LibraryStoreIoStage stage) =>
            _ioStage?.Invoke(stage, Environment.CurrentManagedThreadId);

        private static LibraryData CreateDefaultLibrary()
        {
            var library = new LibraryData
            {
                Version = CurrentVersion
            };

            library.Targets.Add(DefaultTarget);
            AppLogger.Info("Default target folder registered (./Videos).");

#if DEBUG
            AppLogger.Info("Creating debug sample entries.");
            library.Videos.AddRange(new[]
            {
                new VideoEntry("./Videos/forest-gump.mp4",
                    new VideoMeta(
                        "Forest Gump",
                        new DateOnly(1994, 7, 6),
                        new[] { "Tom Hanks" },
                        "resources/noimage.jpg",
                        Array.Empty<string>(),
                        string.Empty)),
                new VideoEntry("./Videos/devil-wears-prada.mp4",
                    new VideoMeta(
                        "The Devil Wears Prada",
                        new DateOnly(2006, 6, 30),
                        new[] { "Meryl Streep" },
                        "resources/noimage.jpg",
                        Array.Empty<string>(),
                        string.Empty)),
                new VideoEntry("./Videos/inception.mp4",
                    new VideoMeta(
                        "Inception",
                        new DateOnly(2010, 7, 16),
                        new[] { "Leonardo DiCaprio", "Tom Hardy" },
                        "resources/noimage.jpg",
                        Array.Empty<string>(),
                        string.Empty)),
                new VideoEntry("./Videos/black-widow.mp4",
                    new VideoMeta(
                        "Black Widow",
                        new DateOnly(2021, 7, 9),
                        new[] { "Scarlett Johansson", "Florence Pugh" },
                        "resources/noimage.jpg",
                        Array.Empty<string>(),
                        string.Empty)),
                new VideoEntry("./Videos/fight-club.mp4",
                    new VideoMeta(
                        "Fight Club",
                        new DateOnly(1999, 10, 15),
                        new[] { "Brad Pitt", "Edward Norton" },
                        "resources/noimage.jpg",
                        Array.Empty<string>(),
                        string.Empty))
            });
#endif

            return library;
        }

        private static LibraryData Normalize(LibraryData data)
        {
            data.Version = data.Version <= 0 ? CurrentVersion : data.Version;

            var targets = (data.Targets ?? new List<TargetFolder>())
                .Where(t => t is not null)
                .Select(NormalizeTarget)
                .ToList();
            if (targets.Count == 0)
            {
                targets.Add(DefaultTarget);
            }
            data.Targets = targets;

            var originalVideoCount = data.Videos?.Count ?? 0;
            var videos = (data.Videos ?? new List<VideoEntry>())
                .Where(v => v is not null && v.Meta is not null)
                .Select(NormalizeVideoEntry)
                .ToList();

#if !DEBUG
            videos = videos
                .Where(v => File.Exists(LibraryPathHelper.ResolveToAbsolute(v.Path)))
                .ToList();
            var removed = originalVideoCount - videos.Count;
            if (removed > 0)
            {
                AppLogger.Info($"Removed {removed} entries referencing missing files.");
            }
#endif

            AppLogger.Info($"Library normalization complete. Targets: {targets.Count}, Videos: {videos.Count}.");
            data.Videos = videos;

            return data;
        }

        private static TargetFolder NormalizeTarget(TargetFolder target)
        {
            var root = LibraryPathHelper.NormalizeLibraryPath(target.Root ?? "./");
            var include = target.IncludePatterns ?? Array.Empty<string>();
            var exclude = target.ExcludePatterns ?? Array.Empty<string>();
            return target with
            {
                Root = root,
                IncludePatterns = include,
                ExcludePatterns = exclude
            };
        }

        private static VideoEntry NormalizeVideoEntry(VideoEntry entry)
        {
            var normalizedPath = LibraryPathHelper.NormalizeLibraryPath(entry.Path);
            var normalizedMeta = NormalizeMeta(entry.Meta);
            var size = entry.SizeBytes < 0 ? 0 : entry.SizeBytes;
            var lastModified = entry.LastModifiedUtc;
            if (lastModified != default && lastModified.Kind != DateTimeKind.Utc)
            {
                lastModified = DateTime.SpecifyKind(lastModified, DateTimeKind.Utc);
            }

            var created = entry.CreatedUtc;
            if (created != default && created.Kind != DateTimeKind.Utc)
            {
                created = DateTime.SpecifyKind(created, DateTimeKind.Utc);
            }

            if (created == default)
            {
                created = lastModified;
            }

            return entry with
            {
                Path = normalizedPath,
                Meta = normalizedMeta,
                SizeBytes = size,
                LastModifiedUtc = lastModified,
                CreatedUtc = created
            };
        }

        private static VideoMeta NormalizeMeta(VideoMeta meta)
        {
            var title = string.IsNullOrWhiteSpace(meta.Title) ? "Untitled" : meta.Title.Trim();
            var actors = (meta.Actors ?? Array.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .ToArray();
            var tags = (meta.Tags ?? Array.Empty<string>())
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim())
                .ToArray();
            var thumbnail = string.IsNullOrWhiteSpace(meta.Thumbnail)
                ? string.Empty
                : LibraryPathHelper.NormalizeLibraryPath(meta.Thumbnail);
            var description = string.IsNullOrWhiteSpace(meta.Description) ? string.Empty : meta.Description.Trim();

            return meta with
            {
                Title = title,
                Date = meta.Date,
                Actors = actors,
                Thumbnail = thumbnail,
                Tags = tags,
                Description = description
            };
        }
    }
}


