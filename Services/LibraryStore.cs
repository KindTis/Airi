using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            new[] { "*.mp4", "*.mkv", "*.avi" },
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
                var data = CreateDefaultLibrary();
                await SaveAsync(data).ConfigureAwait(false);
                return data;
            }

            try
            {
                await using var stream = File.OpenRead(_filePath);
                var data = await JsonSerializer.DeserializeAsync<LibraryData>(stream, _jsonOptions).ConfigureAwait(false);
                return data is null ? await ResetWithDefaultsAsync().ConfigureAwait(false) : Normalize(data);
            }
            catch
            {
                return await ResetWithDefaultsAsync().ConfigureAwait(false);
            }
        }

        public async Task SaveAsync(LibraryData library)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            library.Version = CurrentVersion;
            await using var stream = File.Create(_filePath);
            await JsonSerializer.SerializeAsync(stream, library, _jsonOptions).ConfigureAwait(false);
        }

        private static string ResolveDefaultPath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "Airi", "videos.json");
        }

        private static JsonSerializerOptions BuildOptions()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            options.Converters.Add(new DateOnlyJsonConverter());
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }

        private async Task<LibraryData> ResetWithDefaultsAsync()
        {
            var data = CreateDefaultLibrary();
            await SaveAsync(data).ConfigureAwait(false);
            return data;
        }

        private static LibraryData CreateDefaultLibrary()
        {
            return new LibraryData
            {
                Version = CurrentVersion,
                Targets = { DefaultTarget },
                Videos =
                {
                    new VideoEntry("./Videos/forest-gump.mp4",
                        new VideoMeta(
                            "Forest Gump",
                            new DateOnly(1994, 7, 6),
                            new[] { "Tom Hanks" },
                            "resources/noimage.jpg",
                            Array.Empty<string>())),
                    new VideoEntry("./Videos/devil-wears-prada.mp4",
                        new VideoMeta(
                            "The Devil Wears Prada",
                            new DateOnly(2006, 6, 30),
                            new[] { "Meryl Streep" },
                            "resources/noimage.jpg",
                            Array.Empty<string>())),
                    new VideoEntry("./Videos/inception.mp4",
                        new VideoMeta(
                            "Inception",
                            new DateOnly(2010, 7, 16),
                            new[] { "Leonardo DiCaprio", "Tom Hardy" },
                            "resources/noimage.jpg",
                            Array.Empty<string>())),
                    new VideoEntry("./Videos/black-widow.mp4",
                        new VideoMeta(
                            "Black Widow",
                            new DateOnly(2021, 7, 9),
                            new[] { "Scarlett Johansson", "Florence Pugh" },
                            "resources/noimage.jpg",
                            Array.Empty<string>())),
                    new VideoEntry("./Videos/fight-club.mp4",
                        new VideoMeta(
                            "Fight Club",
                            new DateOnly(1999, 10, 15),
                            new[] { "Brad Pitt", "Edward Norton" },
                            "resources/noimage.jpg",
                            Array.Empty<string>()))
                }
            };
        }

        private static LibraryData Normalize(LibraryData data)
        {
            data.Version = data.Version <= 0 ? CurrentVersion : data.Version;
            data.Targets ??= new List<TargetFolder> { DefaultTarget };
            if (!data.Targets.Any())
            {
                data.Targets.Add(DefaultTarget);
            }

            data.Videos = data.Videos?
                .Where(v => v is not null && v.Meta is not null)
                .Select(v => new VideoEntry(
                    string.IsNullOrWhiteSpace(v.Path) ? string.Empty : v.Path,
                    NormalizeMeta(v.Meta!)))
                .ToList() ?? new List<VideoEntry>();

            return data;
        }

        private static VideoMeta NormalizeMeta(VideoMeta meta)
        {
            var title = string.IsNullOrWhiteSpace(meta.Title) ? "Untitled" : meta.Title;
            var date = meta.Date;
            var actors = meta.Actors ?? Array.Empty<string>();
            var thumbnail = meta.Thumbnail ?? string.Empty;
            var tags = meta.Tags ?? Array.Empty<string>();

            return meta with
            {
                Title = title,
                Date = date,
                Actors = actors,
                Thumbnail = thumbnail,
                Tags = tags
            };
        }
    }
}
