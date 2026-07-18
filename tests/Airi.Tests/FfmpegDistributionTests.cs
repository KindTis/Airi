using System.Diagnostics;
using System.IO;

namespace Airi.Tests;

public sealed class FfmpegDistributionTests
{
    [Fact]
    public async Task BundledBuild_IsLgplAndDecodesGuaranteedCodecs()
    {
        var binaryFolder = Path.Combine(AppContext.BaseDirectory, "resources", "ffmpeg", "win-x64");
        var ffmpeg = Path.Combine(binaryFolder, "ffmpeg.exe");
        var ffprobe = Path.Combine(binaryFolder, "ffprobe.exe");
        var buildInfo = Path.Combine(binaryFolder, "BUILD_INFO.md");

        Assert.True(File.Exists(ffmpeg), ffmpeg);
        Assert.True(File.Exists(ffprobe), ffprobe);
        Assert.Equal("# FFmpeg 배포 정보", File.ReadLines(buildInfo).First());

        var version = await CaptureAsync(ffmpeg, "-version");
        Assert.Contains("ffmpeg version n8.1.2-22-g94138f6973", version, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--enable-gpl", version, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--enable-nonfree", version, StringComparison.OrdinalIgnoreCase);

        var decoders = await CaptureAsync(ffmpeg, "-hide_banner -decoders");
        Assert.Contains(" h264 ", decoders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" hevc ", decoders, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> CaptureAsync(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException($"프로세스를 시작할 수 없습니다: {fileName}");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        return (await stdout) + Environment.NewLine + (await stderr);
    }
}
