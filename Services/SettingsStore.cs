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

namespace Airi.Services;

internal enum SettingsStoreSaveStage
{
    BeforeTemporaryWrite,
    AfterTemporaryFlush,
    BeforeCommit,
    AfterCommit
}

public sealed class SettingsStore
{
    private const int CurrentVersion = 1;
    private static readonly string[] DefaultIncludePatterns =
        { "*.mp4", "*.mkv", "*.avi", "*.wmv" };
    private readonly object _saveGate = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly Action<SettingsStoreSaveStage, string, int>? _saveStage;
    private Task _saveTail = Task.CompletedTask;

    public SettingsStore(string libraryFilePath)
        : this(libraryFilePath, null)
    {
    }

    internal SettingsStore(
        string libraryFilePath,
        Action<SettingsStoreSaveStage, string, int>? saveStage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryFilePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(libraryFilePath))
            ?? throw new ArgumentException(
                "Library path must have a directory.",
                nameof(libraryFilePath));
        FilePath = Path.Combine(directory, "settings.json");
        _saveStage = saveStage;
    }

    public string FilePath { get; }

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => LoadCoreAsync(cancellationToken), cancellationToken);
    }

    public Task SaveAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Normalize(
            settings,
            targetsMissing: false,
            rejectFutureVersion: false);
        lock (_saveGate)
        {
            var previous = _saveTail;
            _saveTail = Task.Run(
                () => SaveAfterAsync(previous, snapshot, cancellationToken),
                CancellationToken.None);
            return _saveTail;
        }
    }

    private async Task<AppSettings> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath))
        {
            var defaults = CreateDefault();
            await SaveCoreAsync(defaults, cancellationToken).ConfigureAwait(false);
            return defaults;
        }

        await using var stream = new FileStream(
            FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Settings JSON must be an object.");
        }

        var targetsMissing = !document.RootElement.TryGetProperty(
            nameof(AppSettings.Targets),
            out _);
        var settings = document.RootElement.Deserialize<AppSettings>(_jsonOptions)
            ?? throw new JsonException("Settings JSON contained null.");
        return Normalize(settings, targetsMissing, rejectFutureVersion: true);
    }

    private async Task SaveAfterAsync(
        Task previous,
        AppSettings snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        cancellationToken.ThrowIfCancellationRequested();
        await SaveCoreAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveCoreAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(FilePath)}.airi-tmp-{Guid.NewGuid():D}");

        try
        {
            Observe(SettingsStoreSaveStage.BeforeTemporaryWrite, tempPath);
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    settings,
                    _jsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            Observe(SettingsStoreSaveStage.AfterTemporaryFlush, tempPath);
            cancellationToken.ThrowIfCancellationRequested();
            Observe(SettingsStoreSaveStage.BeforeCommit, tempPath);
            if (File.Exists(FilePath))
            {
                File.Replace(tempPath, FilePath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, FilePath);
            }
            Observe(SettingsStoreSaveStage.AfterCommit, tempPath);
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLogger.Error("Failed to clean up settings temporary file.", ex);
            }
        }
    }

    private void Observe(SettingsStoreSaveStage stage, string tempPath) =>
        _saveStage?.Invoke(stage, tempPath, Environment.CurrentManagedThreadId);

    private static AppSettings CreateDefault() => new()
    {
        Version = CurrentVersion,
        Targets = CreateDefaultTargets(),
        Sort = new SortSettings()
    };

    private static List<TargetFolder> CreateDefaultTargets() =>
        new()
        {
            new("./Videos", DefaultIncludePatterns.ToArray(), Array.Empty<string>())
        };

    private static AppSettings Normalize(
        AppSettings settings,
        bool targetsMissing,
        bool rejectFutureVersion)
    {
        if (rejectFutureVersion && settings.Version > CurrentVersion)
        {
            throw new JsonException($"Unsupported settings version: {settings.Version}.");
        }

        var targets = targetsMissing
            ? CreateDefaultTargets()
            : NormalizeTargets(settings.Targets);
        var sort = settings.Sort;
        var field = sort?.Field is "Title" or "ReleaseDate" or "CreatedUtc"
            ? sort.Field
            : "ReleaseDate";
        var direction = sort?.Direction is "Ascending" or "Descending"
            ? sort.Direction
            : "Descending";

        return new AppSettings
        {
            Version = CurrentVersion,
            Targets = targets,
            Sort = new SortSettings
            {
                Field = field,
                Direction = direction
            }
        };
    }

    private static List<TargetFolder> NormalizeTargets(
        IReadOnlyList<TargetFolder>? targets)
    {
        if (targets is null || targets.Count == 0)
        {
            throw new JsonException("Settings targets must not be empty.");
        }

        var normalized = new List<TargetFolder>(targets.Count);
        foreach (var target in targets)
        {
            if (target is null || string.IsNullOrWhiteSpace(target.Root))
            {
                throw new JsonException("A settings target root is missing.");
            }

            try
            {
                _ = LibraryPathHelper.ResolveToAbsolute(target.Root);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                throw new JsonException(
                    $"A settings target root is invalid: {target.Root}",
                    ex);
            }

            if (target.IncludePatterns is null ||
                target.IncludePatterns.Count == 0 ||
                target.IncludePatterns.Any(string.IsNullOrWhiteSpace))
            {
                throw new JsonException(
                    $"Include patterns are invalid for target: {target.Root}");
            }

            var exclude = target.ExcludePatterns ?? Array.Empty<string>();
            if (exclude.Any(string.IsNullOrWhiteSpace))
            {
                throw new JsonException(
                    $"Exclude patterns are invalid for target: {target.Root}");
            }

            normalized.Add(new TargetFolder(
                target.Root,
                target.IncludePatterns.ToArray(),
                exclude.ToArray()));
        }

        return normalized;
    }
}
