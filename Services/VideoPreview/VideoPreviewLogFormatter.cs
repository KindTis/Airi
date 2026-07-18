using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Airi.Services.VideoPreview;

internal sealed record VideoPreviewFailure(
    string SourcePath,
    string Extension,
    string Container,
    string Codec,
    string Stage,
    string Result,
    int? ExitCode,
    TimeSpan PrepareDuration,
    TimeSpan PlaybackDuration,
    string? RawStderr);

internal sealed class VideoPreviewLogFormatter
{
    private readonly byte[] _salt;

    public VideoPreviewLogFormatter(byte[] salt)
    {
        ArgumentNullException.ThrowIfNull(salt);
        if (salt.Length != 32) throw new ArgumentException("The log salt must contain 32 bytes.", nameof(salt));
        _salt = salt.ToArray();
    }

    public string FormatFailure(VideoPreviewFailure failure)
    {
        var sourceId = CreateSourceId(failure.SourcePath);
        var exitCode = failure.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none";
        return string.Join(' ',
            $"sourceId={sourceId}",
            $"extension={Safe(failure.Extension)}",
            $"container={Safe(failure.Container)}",
            $"codec={Safe(failure.Codec)}",
            $"stage={Safe(failure.Stage)}",
            $"result={Safe(failure.Result)}",
            $"exitCode={exitCode}",
            $"prepareMs={failure.PrepareDuration.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)}",
            $"playbackMs={failure.PlaybackDuration.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)}");
    }

    private string CreateSourceId(string path)
    {
        var pathBytes = Encoding.UTF8.GetBytes(path ?? string.Empty);
        var input = new byte[_salt.Length + pathBytes.Length];
        Buffer.BlockCopy(_salt, 0, input, 0, _salt.Length);
        Buffer.BlockCopy(pathBytes, 0, input, _salt.Length, pathBytes.Length);
        return Convert.ToHexString(SHA256.HashData(input).AsSpan(0, 12)).ToLowerInvariant();
    }

    private static string Safe(string value)
    {
        if (string.IsNullOrEmpty(value)) return "unknown";
        var filtered = new string(value
            .Take(64)
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ',')
            .ToArray());
        return filtered.Length == 0 ? "unknown" : filtered;
    }
}
