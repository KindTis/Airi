using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Airi.Domain;
using Airi.Infrastructure;

namespace Airi.Services
{
    public class LibraryStore
    {
        private const int CurrentVersion = 1;
        private static readonly TargetFolder DefaultTarget = new(
            "./Videos",
            new[] { "*.mp4", "*.mkv", "*.avi", "*.wmv" },
            Array.Empty<string>(),
            null);

        private readonly string _filePath;
        private readonly JsonSerializerOptions _jsonOptions;

        public LibraryStore(string? customFilePath = null)
        {
            _filePath = customFilePath ?? ResolveDefaultPath();
            _jsonOptions = BuildOptions();
        }

        public string FilePath => _filePath;

        public async Task<LibraryData> LoadAsync()
        {
            if (!File.Exists(_filePath))
            {
                AppLogger.Info($"Library file not found. Creating new library at {_filePath}.");
                var data = Normalize(CreateDefaultLibrary());
                await SaveAsync(data).ConfigureAwait(false);
                return data;
            }

            try
            {
                AppLogger.Info($"Loading library from {_filePath}.");
                await using var stream = File.OpenRead(_filePath);
                var data = await JsonSerializer.DeserializeAsync<LibraryData>(stream, _jsonOptions).ConfigureAwait(false);
                return data is null ? await ResetWithDefaultsAsync().ConfigureAwait(false) : Normalize(data);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to load library; resetting to defaults.", ex);
                return await ResetWithDefaultsAsync().ConfigureAwait(false);
            }
        }

        public async Task SaveAsync(LibraryData library)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            library.Version = CurrentVersion;
            AppLogger.Info($"Saving library to {_filePath} (videos: {library.Videos.Count}).");
            var json = JsonSerializer.Serialize(library, _jsonOptions);
            await File.WriteAllTextAsync(_filePath, json).ConfigureAwait(false);
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

        private async Task<LibraryData> ResetWithDefaultsAsync()
        {
            AppLogger.Info("Resetting library to default state.");
            var data = Normalize(CreateDefaultLibrary());
            await SaveAsync(data).ConfigureAwait(false);
            return data;
        }

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


