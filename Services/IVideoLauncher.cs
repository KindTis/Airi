using System.Diagnostics;
using System.IO;

namespace Airi.Services;

public interface IVideoLauncher
{
    VideoLaunchResult Play(string absolutePath);
    VideoLaunchResult ShowInFolder(string absolutePath);
    VideoLaunchResult OpenFolder(string absolutePath) => new(false, "이 서비스는 폴더 열기를 지원하지 않습니다.");
}

public sealed record VideoLaunchResult(bool Succeeded, string? Reason = null);

public sealed class WindowsVideoLauncher : IVideoLauncher
{
    private readonly Func<ProcessStartInfo, Process?> _start;

    public WindowsVideoLauncher() : this(Process.Start) { }
    internal WindowsVideoLauncher(Func<ProcessStartInfo, Process?> start) => _start = start;

    public VideoLaunchResult Play(string absolutePath) => Launch(() =>
    {
        var path = ValidatePath(absolutePath);
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0) throw new IOException("재생할 일반 파일이 아닙니다.");
        return new ProcessStartInfo { FileName = path, UseShellExecute = true };
    });

    public VideoLaunchResult ShowInFolder(string absolutePath) => Launch(() =>
    {
        var path = ValidatePath(absolutePath);
        if (Directory.Exists(path)) throw new IOException("원본 일반 파일 경로가 아닙니다.");
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) throw new DirectoryNotFoundException("원본의 부모 폴더가 없습니다.");
        if (!File.Exists(path)) return new ProcessStartInfo { FileName = parent, UseShellExecute = true };
        // Explorer has a comma-delimited command syntax. Quote only the exact pathname;
        // ArgumentList would quote the entire /select,path token when it contains spaces.
        return new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true,
            Arguments = "/select,\"" + path + "\""
        };
    });

    public VideoLaunchResult OpenFolder(string absolutePath) => Launch(() =>
    {
        var path = ValidatePath(absolutePath);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("열려는 폴더가 없습니다.");
        return new ProcessStartInfo { FileName = path, UseShellExecute = true };
    });

    private VideoLaunchResult Launch(Func<ProcessStartInfo> createInfo)
    {
        try
        {
            using var process = _start(createInfo());
            // ShellExecute can reuse an existing handler and return no new Process.
            return new(true);
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }

    private static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("유효한 절대 파일 경로가 아닙니다.", nameof(path));
        return Path.GetFullPath(path);
    }
}
