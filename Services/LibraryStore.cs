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
        private const int CurrentVersion = 2;
        private bool _writeProtected;
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
                var defaultData = CreateDefaultLibrary();
                await SaveCoreAsync(defaultData, cancellationToken).ConfigureAwait(false);
                return defaultData;
            }

            try
            {
                AppLogger.Info($"Loading library from {_filePath}.");
                ObserveIo(LibraryStoreIoStage.DestinationOpen);
                var bytes = await File.ReadAllBytesAsync(_filePath, cancellationToken).ConfigureAwait(false);
                ObserveIo(LibraryStoreIoStage.BeforeDeserialize);
                cancellationToken.ThrowIfCancellationRequested();
                ReadOnlyMemory<byte> jsonBytes = bytes;
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) jsonBytes = jsonBytes[3..];
                using var document = JsonDocument.Parse(jsonBytes);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("Version", out var versionElement) ||
                    !versionElement.TryGetInt32(out var version) || version is not (1 or CurrentVersion) ||
                    !root.TryGetProperty("Videos", out var videosElement) || videosElement.ValueKind != JsonValueKind.Array)
                    throw new JsonException("Unsupported library schema. The original file has been preserved.");
                if (version == CurrentVersion) ValidateVersionTwoShape(root);
                var data = JsonSerializer.Deserialize<LibraryData>(jsonBytes.Span, _jsonOptions)
                    ?? throw new JsonException("Library cannot be null.");
                Validate(data);
                if (version == CurrentVersion)
                {
                    _writeProtected = false;
                    return data;
                }

                // Do not normalize legacy values: whitespace, duplicates, paths and file times are authored data.
                var migrated = data.WithVideos(data.Videos);
                migrated.Version = CurrentVersion;
                migrated.AdditionalData?.Remove("Targets");
                migrated.AdditionalData?.Remove("LastScanUtc");
                var backupPath = _filePath + $".v1-backup-{Guid.NewGuid():N}.json";
                await using (var backup = new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    await backup.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await backup.FlushAsync(cancellationToken).ConfigureAwait(false);
                    backup.Flush(true);
                }
                _writeProtected = false;
                try
                {
                    await SaveCoreAsync(migrated, cancellationToken).ConfigureAwait(false);
                    var reread = JsonSerializer.Deserialize<LibraryData>(
                        await File.ReadAllBytesAsync(_filePath, CancellationToken.None).ConfigureAwait(false), _jsonOptions)
                        ?? throw new JsonException("Migrated library could not be read back.");
                    Validate(reread);
                    if (JsonSerializer.Serialize(migrated, _jsonOptions) != JsonSerializer.Serialize(reread, _jsonOptions))
                        throw new JsonException("Migrated library verification failed. The v1 backup is preserved.");
                    return reread;
                }
                catch
                {
                    _writeProtected = true;
                    // Restore exact bytes even if a read-back or after-commit failure occurs.
                    var originalNow = await File.ReadAllBytesAsync(_filePath, CancellationToken.None).ConfigureAwait(false);
                    if (!bytes.AsSpan().SequenceEqual(originalNow))
                    {
                        var restorePath = _filePath + $".airi-rollback-{Guid.NewGuid():N}";
                        try
                        {
                            await File.WriteAllBytesAsync(restorePath, bytes, CancellationToken.None).ConfigureAwait(false);
                            File.Replace(restorePath, _filePath, null, ignoreMetadataErrors: true);
                        }
                        finally { if (File.Exists(restorePath)) File.Delete(restorePath); }
                    }
                    throw;
                }
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
                _writeProtected = true;
                AppLogger.Error("Library could not be read. The original file is protected; retry after recovery.", ex);
                throw;
            }
        }

        private static void ValidateVersionTwoShape(JsonElement root)
        {
            static void Require(JsonElement value, params string[] names)
            {
                if (value.ValueKind != JsonValueKind.Object || names.Any(name => !value.TryGetProperty(name, out _)))
                    throw new JsonException("Version 2 library is missing required identity or journal fields.");
            }
            foreach (var entry in root.GetProperty("Videos").EnumerateArray()) Require(entry, "Id", "Revision");
            if (!root.TryGetProperty("Journal", out var journal) || journal.ValueKind != JsonValueKind.Array)
                throw new JsonException("Version 2 library is missing its journal.");
            foreach (var record in journal.EnumerateArray())
            {
                Require(record, "OperationId", "EntryId", "BaseRevision", "OriginalPath", "Snapshot", "TimestampUtc", "Intent", "State", "MoveConfirmed", "RestoreAvailable");
                Require(record.GetProperty("Snapshot"), "Id", "Revision", "Path", "Meta");
                if (record.GetProperty("Intent").ValueKind != JsonValueKind.String ||
                    !Enum.TryParse<LibraryOperationIntent>(record.GetProperty("Intent").GetString(), out var intent) || !Enum.IsDefined(intent) ||
                    record.GetProperty("State").ValueKind != JsonValueKind.String ||
                    !Enum.TryParse<LibraryOperationState>(record.GetProperty("State").GetString(), out var state) || !Enum.IsDefined(state))
                    throw new JsonException("Journal contains an unsupported intent or state.");
            }
        }

        private static bool IsValidEntry(VideoEntry? entry) => entry is not null && entry.Meta is not null && entry.Path is not null &&
            entry.Id != Guid.Empty && entry.Revision >= 0 && Enum.IsDefined(entry.Presence) && entry.Meta.Actors is not null &&
            entry.Meta.Tags is not null && entry.Meta.Genres is not null && entry.Meta.ManualFieldLocks is not null &&
            entry.Meta.ManualFieldLocks.All(Enum.IsDefined);

        private static void Validate(LibraryData data)
        {
            if (data.Videos is null || data.Journal is null || data.Videos.Any(entry => !IsValidEntry(entry)) ||
                data.Videos.Select(entry => entry.Id).Distinct().Count() != data.Videos.Count)
                throw new JsonException("Library contains invalid entries or duplicate identities. The original file is preserved.");
            var ids = data.Videos.Select(entry => entry.Id).ToHashSet();
            var operations = new HashSet<Guid>();
            foreach (var record in data.Journal)
            {
                if (record is null || record.OperationId == Guid.Empty || !operations.Add(record.OperationId) ||
                    !ids.Contains(record.EntryId) || record.BaseRevision < 0 || string.IsNullOrWhiteSpace(record.OriginalPath) ||
                    record.TimestampUtc == default || !Enum.IsDefined(record.Intent) || !Enum.IsDefined(record.State) ||
                    !IsValidEntry(record.Snapshot) || record.Snapshot!.Id != record.EntryId || record.Snapshot.Revision != record.BaseRevision ||
                    !string.Equals(record.Snapshot.Path, record.OriginalPath, StringComparison.Ordinal) ||
                    (record.RestoreAvailable && string.IsNullOrWhiteSpace(record.ReceiptJson)) ||
                    (record.State == LibraryOperationState.Pending && record.Intent != LibraryOperationIntent.Recycle) ||
                    (record.State == LibraryOperationState.RestorePending && record.Intent != LibraryOperationIntent.Restore) ||
                    // Intent is the requested operation; exact inspection can observe an external restore after Recycle.
                    (record.State == LibraryOperationState.Restored && (!record.MoveConfirmed || string.IsNullOrWhiteSpace(record.ReceiptJson))) ||
                    (record.State == LibraryOperationState.Failed && record.Intent != LibraryOperationIntent.Recycle))
                    throw new JsonException("Library journal is invalid. The original file is preserved.");
            }
        }

        private async Task SaveCoreAsync(LibraryData library, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(directory);
            ObserveIo(LibraryStoreIoStage.PersistenceProjection);
            if (_writeProtected) throw new InvalidOperationException("Library is protected after a read failure. Reload successfully before saving.");
            if (library.Version > CurrentVersion) throw new NotSupportedException("Cannot overwrite a future library schema.");
            var persistenceProjection = library.WithVideos(library.Videos);
            persistenceProjection.Version = CurrentVersion;
            Validate(persistenceProjection);
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

                var verification = JsonSerializer.Deserialize<LibraryData>(
                    await File.ReadAllBytesAsync(tempPath, cancellationToken).ConfigureAwait(false), _jsonOptions)
                    ?? throw new JsonException("Candidate library could not be read back.");
                Validate(verification);
                if (JsonSerializer.Serialize(persistenceProjection, _jsonOptions) != JsonSerializer.Serialize(verification, _jsonOptions))
                    throw new JsonException("Candidate library verification failed.");
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

            options.Converters.Add(new PreservedVideoEntryConverter());
            options.Converters.Add(new PreservedVideoMetaConverter());
            options.Converters.Add(new ActorNameConverter());
            options.Converters.Add(new DateOnlyJsonConverter());
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
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

        private static LibraryData CreateDefaultLibrary() => new() { Version = CurrentVersion };
    }
}
