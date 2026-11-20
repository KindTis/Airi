using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;

namespace Airi.Services;

public sealed partial class WindowsVideoRecycleService
{
    private sealed record NativeFileIdentity(uint VolumeSerial, ulong FileIndex, long CreationTime, long Length);
    private sealed record NativeReceipt(int Version, string OriginalPath, string RecyclePath,
        string ShellParsingName, string ShellPidl, NativeFileIdentity Identity);

    public Task<VideoRecycleResult> RestoreAsync(string receipt) => OnSta(() => RestoreOnSta(receipt), ex => new VideoRecycleResult(VideoRecycleOutcome.Unknown, ex.Message));

    public Task<VideoRecycleInspection> InspectAsync(string absolutePath, string? receipt = null) =>
        OnSta(() => InspectReceipt(absolutePath, receipt), ex => new VideoRecycleInspection(VideoFileEvidence.Unknown, VideoFileEvidence.Unknown, ex.Message));

    private static Task<T> OnSta<T>(Func<T> action, Func<Exception, T> failure)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                dispatcher.BeginInvoke(new Action(() =>
                {
                    try { completion.TrySetResult(action()); }
                    catch (Exception ex) { completion.TrySetResult(failure(ex)); }
                    finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
                }));
                Dispatcher.Run();
            }
            catch (Exception ex) { completion.TrySetResult(failure(ex)); }
        }) { IsBackground = true, Name = "Airi exact restore STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static NativeFileIdentity ReadIdentity(string path)
    {
        // Open the item itself, rather than following a replacement symbolic link.
        using var handle = CreateFile(path, 0x80, FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero, 3, 0x00200000, IntPtr.Zero); // FILE_READ_ATTRIBUTES, OPEN_EXISTING, OPEN_REPARSE_POINT
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 2) throw new FileNotFoundException("대상 파일이 없습니다.", path);
            if (error == 3) throw new DirectoryNotFoundException("대상 부모 폴더가 없습니다.");
            throw new Win32Exception(error);
        }
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((info.Attributes & ((uint)FileAttributes.Directory | (uint)FileAttributes.ReparsePoint)) != 0)
            throw new IOException("폴더와 재분석 지점은 정확한 단일 파일 작업 대상이 아닙니다.");
        return new(info.VolumeSerial, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow,
            ((long)info.CreationTime.High << 32) | info.CreationTime.Low,
            ((long)info.FileSizeHigh << 32) | info.FileSizeLow);
    }

    private const string RecycleBinParsingName = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    private static string CreateReceipt(string sourcePath, NativeFileIdentity sourceIdentity, IShellItem created)
    {
        var recyclePath = GetPath(created);
        if (ReadIdentity(recyclePath) != sourceIdentity) throw new IOException("휴지통 항목의 파일 동일성이 일치하지 않습니다.");
        // The callback item is a physical $R file. Rebind this exact path under the virtual
        // Recycle Bin to preserve its exact namespace identity across processes. No enumeration.
        // Private $I metadata lifetime belongs to Windows, including after a normal Shell undelete.
        var iid = typeof(IShellItem).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(RecycleBinParsingName + "\\" + recyclePath,
            IntPtr.Zero, ref iid, out var virtualItem));
        try
        {
            if (!SamePath(GetPath(virtualItem), recyclePath) || !HasRecycleBinParent(virtualItem))
                throw new IOException("정확한 가상 휴지통 항목을 확인할 수 없습니다.");
            Marshal.ThrowExceptionForHR(SHGetIDListFromObject(virtualItem, out var pidl));
            try
            {
                var size = ILGetSize(pidl);
                if (size < 2 || size > 65536) throw new IOException("Shell 식별자의 크기가 잘못되었습니다.");
                var data = new byte[size];
                Marshal.Copy(pidl, data, 0, data.Length);
                return JsonSerializer.Serialize(new NativeReceipt(1, sourcePath, recyclePath,
                    RecycleBinParsingName + "\\" + recyclePath, Convert.ToBase64String(data), sourceIdentity));
            }
            finally { Marshal.FreeCoTaskMem(pidl); }
        }
        finally { Marshal.FinalReleaseComObject(virtualItem); }
    }

    private static bool HasRecycleBinParent(IShellItem item)
    {
        Marshal.ThrowExceptionForHR(item.GetParent(out var parent));
        try
        {
            Marshal.ThrowExceptionForHR(parent.GetDisplayName(0x80028000, out var value));
            try { return string.Equals(Marshal.PtrToStringUni(value), RecycleBinParsingName, StringComparison.OrdinalIgnoreCase); }
            finally { Marshal.FreeCoTaskMem(value); }
        }
        finally { Marshal.FinalReleaseComObject(parent); }
    }

    private static NativeReceipt ParseReceipt(string receipt)
    {
        if (string.IsNullOrWhiteSpace(receipt) || receipt.Length > 150000) throw new IOException("복원 영수증이 없습니다.");
        var parsed = JsonSerializer.Deserialize<NativeReceipt>(receipt) ?? throw new IOException("복원 영수증이 잘못되었습니다.");
        if (parsed.Version != 1 || parsed.Identity is null || !IsAbsolute(parsed.OriginalPath) || !IsAbsolute(parsed.RecyclePath) ||
            parsed.ShellParsingName != RecycleBinParsingName + "\\" + parsed.RecyclePath || string.IsNullOrWhiteSpace(parsed.ShellPidl) ||
            SamePath(parsed.OriginalPath, parsed.RecyclePath)) throw new IOException("지원하지 않는 복원 영수증입니다.");
        var pidl = Convert.FromBase64String(parsed.ShellPidl);
        if (!IsValidPidl(pidl)) throw new IOException("Shell 식별자가 잘못되었습니다.");
        // An issued receipt always points into the source volume's Recycle Bin. Never accept arbitrary moves.
        var relative = Path.GetRelativePath(Path.GetPathRoot(parsed.RecyclePath)!, parsed.RecyclePath);
        if (!relative.StartsWith("$Recycle.Bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("휴지통 파일 경로가 아닙니다.");
        return parsed;
    }

    private static bool IsValidPidl(byte[] bytes)
    {
        if (bytes.Length is < 2 or > 65536) return false;
        var offset = 0;
        while (offset + 2 <= bytes.Length)
        {
            var size = (int)BitConverter.ToUInt16(bytes, offset);
            if (size == 0) return offset + 2 == bytes.Length;
            if (size < 2 || offset + size + 2 > bytes.Length) return false;
            offset += size;
        }
        return false;
    }

    private static bool IsAbsolute(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);
    private static bool SamePath(string? first, string? second) => first is not null && second is not null &&
        Path.GetFullPath(first).Equals(Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

    private static VideoFileEvidence Probe(string path, NativeFileIdentity? expected)
    {
        try
        {
            var actual = ReadIdentity(path);
            return expected is null ? VideoFileEvidence.PresentUnverified : actual == expected ? VideoFileEvidence.MatchesReceipt : VideoFileEvidence.Different;
        }
        catch (FileNotFoundException) { return VideoFileEvidence.Missing; }
        catch (DirectoryNotFoundException) { return VideoFileEvidence.Missing; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
        { return VideoFileEvidence.Unknown; }
    }

    private static VideoRecycleInspection InspectReceipt(string originalPath, string? receipt)
    {
        try
        {
            if (!IsAbsolute(originalPath)) return new(VideoFileEvidence.Unknown, VideoFileEvidence.Unknown, "유효한 절대 파일 경로가 아닙니다.");
            if (receipt is null) return new(Probe(originalPath, null), VideoFileEvidence.Unknown, "확인된 복원 영수증이 없습니다.");
            var parsed = ParseReceipt(receipt);
            if (!SamePath(originalPath, parsed.OriginalPath)) return new(VideoFileEvidence.Unknown, VideoFileEvidence.Unknown, "영수증의 원경로가 다릅니다.");
            var source = Probe(parsed.OriginalPath, parsed.Identity);
            var recycled = Probe(parsed.RecyclePath, parsed.Identity);
            if (recycled == VideoFileEvidence.MatchesReceipt)
            {
                var item = RehydrateItem(parsed);
                try
                {
                    if (!HasRecycleBinParent(item) || !SamePath(GetPath(item), parsed.RecyclePath))
                        return new(source, VideoFileEvidence.Unknown, "정확한 Shell 영수증을 확인할 수 없습니다.");
                }
                finally { Marshal.FinalReleaseComObject(item); }
            }
            return new(source, recycled);
        }
        catch (Exception ex) when (ex is JsonException or IOException or FormatException or ArgumentException or NotSupportedException or COMException)
        { return new(VideoFileEvidence.Unknown, VideoFileEvidence.Unknown, ex.Message); }
    }

    private static VideoRecycleResult RestoreOnSta(string receipt)
    {
        IFileOperation? operation = null;
        IShellItem? item = null;
        IShellItem? parent = null;
        var started = false;
        try
        {
            var parsed = ParseReceipt(receipt);
            var inspection = InspectReceipt(parsed.OriginalPath, receipt);
            if (inspection.SourceState != VideoFileEvidence.Missing || inspection.RecycleState != VideoFileEvidence.MatchesReceipt)
                return Refused("원경로가 비어 있지 않거나 정확한 휴지통 항목을 확인할 수 없습니다.");
            var parentPath = Path.GetDirectoryName(parsed.OriginalPath)!;
            if (!Directory.Exists(parentPath) || (File.GetAttributes(parentPath) & FileAttributes.ReparsePoint) != 0)
                return Refused("원래 부모 폴더가 없거나 안전하게 확인할 수 없습니다.");
            item = RehydrateItem(parsed);
            if (!HasRecycleBinParent(item) || !SamePath(GetPath(item), parsed.RecyclePath) || ReadIdentity(GetPath(item)) != parsed.Identity)
                return Refused("저장된 Shell 항목이 정확한 휴지통 파일과 일치하지 않습니다.");
            var iid = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(parentPath, IntPtr.Zero, ref iid, out parent));
            operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), true)!)!;
            // No overwrite, auto-rename, confirmation-to-all, directory creation, or secondary operation.
            Marshal.ThrowExceptionForHR(operation.SetOperationFlags(0x2000 | 0x4 | 0x400 | 0x00100000));
            var sink = new RestoreProgressSink(parsed);
            Marshal.ThrowExceptionForHR(operation.MoveItem(item, parent, Path.GetFileName(parsed.OriginalPath), sink));
            started = true;
            var perform = operation.PerformOperations();
            var query = operation.GetAnyOperationsAborted(out var aborted);
            GC.KeepAlive(sink);
            var after = InspectReceipt(parsed.OriginalPath, receipt);
            var confirmed = sink.Confirmed && perform == 0 && query == 0 && !aborted &&
                after.SourceState == VideoFileEvidence.MatchesReceipt && after.RecycleState == VideoFileEvidence.Missing;
            return new(confirmed ? VideoRecycleOutcome.Succeeded : VideoRecycleOutcome.Unknown,
                confirmed ? null : "정확한 복원 완료를 확인하지 못했습니다.")
            {
                Receipt = receipt, MovementConfirmed = confirmed,
                Diagnostics = $"restorePre={sink.PreCount}, restorePost={sink.PostCount}, veto={sink.Vetoed}, postHRESULT=0x{sink.PostResult:X8}, performHRESULT=0x{perform:X8}, queryHRESULT=0x{query:X8}, aborted={aborted}"
            };
        }
        catch (Exception ex)
        {
            return new(started ? VideoRecycleOutcome.Unknown : VideoRecycleOutcome.Failed, ex.Message)
                { SideEffectsKnownAbsent = !started };
        }
        finally
        {
            if (item is not null) Marshal.FinalReleaseComObject(item);
            if (parent is not null) Marshal.FinalReleaseComObject(parent);
            if (operation is not null) Marshal.FinalReleaseComObject(operation);
        }
    }

    private static VideoRecycleResult Refused(string reason) => new(VideoRecycleOutcome.Failed, reason) { SideEffectsKnownAbsent = true };

    private static IShellItem RehydrateItem(NativeReceipt receipt)
    {
        var bytes = Convert.FromBase64String(receipt.ShellPidl);
        var pidl = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pidl, bytes.Length);
            var iid = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromIDList(pidl, ref iid, out var item));
            return item;
        }
        finally { Marshal.FreeCoTaskMem(pidl); }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class RestoreProgressSink(NativeReceipt receipt) : IFileOperationProgressSink
    {
        public int PreCount { get; private set; }
        public int PostCount { get; private set; }
        public int PostResult { get; private set; }
        public bool Vetoed { get; private set; }
        public bool Confirmed => !Vetoed && PreCount == 1 && PostCount == 1 && PostResult is 0 or 0x00270008;
        private int Veto() { Vetoed = true; return unchecked((int)0x80004004); }
        public int StartOperations() => 0;
        public int FinishOperations(int result) => 0;
        public int PreMoveItem(uint flags, IShellItem item, IShellItem destination, string? name)
        {
            PreCount++;
            try
            {
                var target = Path.Combine(GetPath(destination), name ?? "");
                return PreCount == 1 && (flags & (3 | 0x400)) == 0 && SamePath(GetPath(item), receipt.RecyclePath) &&
                    ReadIdentity(receipt.RecyclePath) == receipt.Identity && SamePath(target, receipt.OriginalPath) &&
                    Probe(receipt.OriginalPath, null) == VideoFileEvidence.Missing ? 0 : Veto();
            }
            catch { return Veto(); }
        }
        public int PostMoveItem(uint flags, IShellItem item, IShellItem destination, string? name, int result, IShellItem? created)
        {
            PostCount++;
            PostResult = result;
            try
            {
                if (PostCount != 1 || (flags & (3 | 0x400)) != 0 || created is null ||
                    !SamePath(GetPath(item), receipt.RecyclePath) ||
                    !SamePath(Path.Combine(GetPath(destination), name ?? ""), receipt.OriginalPath) ||
                    !SamePath(GetPath(created), receipt.OriginalPath) ||
                    ReadIdentity(receipt.OriginalPath) != receipt.Identity) Veto();
            }
            catch { Veto(); }
            finally { if (created is not null) Marshal.ReleaseComObject(created); }
            return 0;
        }
        public int PreRenameItem(uint flags, IShellItem item, string name) => Veto();
        public int PostRenameItem(uint flags, IShellItem item, string name, int result, IShellItem? created) => 0;
        public int PreCopyItem(uint flags, IShellItem item, IShellItem destination, string? name) => Veto();
        public int PostCopyItem(uint flags, IShellItem item, IShellItem destination, string? name, int result, IShellItem? created) => 0;
        public int PreDeleteItem(uint flags, IShellItem item) => Veto();
        public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created) => 0;
        public int PreNewItem(uint flags, IShellItem destination, string name) => Veto();
        public int PostNewItem(uint flags, IShellItem destination, string name, string? template, uint attributes, int result, IShellItem? created) => 0;
        public int UpdateProgress(uint total, uint completed) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime { public uint Low; public uint High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileInformation
    {
        public uint Attributes;
        public NativeFileTime CreationTime;
        public NativeFileTime LastAccessTime;
        public NativeFileTime LastWriteTime;
        public uint VolumeSerial;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out NativeFileInformation info);
    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHGetIDListFromObject([MarshalAs(UnmanagedType.IUnknown)] object item, out IntPtr pidl);
    [DllImport("shell32.dll")]
    private static extern uint ILGetSize(IntPtr pidl);
    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHCreateItemFromIDList(IntPtr pidl, ref Guid iid, out IShellItem item);
}
