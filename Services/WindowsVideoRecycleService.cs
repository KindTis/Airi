using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Airi.Services;

// All signals come from one operation and its one registered item. Missing or conflicting
// signals must never turn a mere disappearance of the source into a successful recycle.
internal sealed class RecycleOperationState(string absolutePath)
{
    internal const uint RecycleIfPossible = 0x80; // TRANSFER_SOURCE_FLAGS.TSF_DELETE_RECYCLE_IF_POSSIBLE
    internal const uint FileSystemAttribute = 0x40000000; // SFGAO_FILESYSTEM
    internal const uint FolderAttribute = 0x20000000; // SFGAO_FOLDER
    private const int CopyEngineDontProcessChildren = 0x00270008;
    private int _preCount;
    private int _postCount;
    private bool _vetoed;
    private bool _hasRecycleItem;
    private int _postResult;
    private string? _recycledPath;
    private int _attributeQueryResult;
    private uint _attributes;
    private Exception? _receiptFailure;
    internal string? Receipt { get; private set; }

    internal void CaptureReceipt(Func<string> createReceipt)
    {
        try { Receipt = createReceipt(); }
        catch (Exception ex) { _receiptFailure = ex; }
    }

    internal bool BeforeDelete(string? path, uint flags, int attributeQueryResult, uint attributes)
    {
        _preCount++;
        _attributeQueryResult = attributeQueryResult;
        _attributes = attributes;
        // GetAttributes documents S_FALSE for partial mask matches, as expected for a file
        // when requesting FILESYSTEM | FOLDER. Neither a folder nor an unknown query is allowed.
        var isFileSystemFile = attributeQueryResult is 0 or 1 &&
            (attributes & FileSystemAttribute) != 0 && (attributes & FolderAttribute) == 0;
        var allowed = _preCount == 1 && Matches(path) && (flags & RecycleIfPossible) != 0 && isFileSystemFile;
        _vetoed |= !allowed;
        return allowed;
    }

    internal void Veto() => _vetoed = true;

    internal void AfterDelete(string? path, int result, bool hasRecycleItem, string? recycledPath)
    {
        _postCount++;
        if (!Matches(path) || _postCount != 1) _vetoed = true;
        _postResult = result;
        _hasRecycleItem = hasRecycleItem;
        _recycledPath = recycledPath;
    }

    internal VideoRecycleResult Complete(int performResult, int abortedQueryResult, bool aborted)
    {
        var result = ClassifyCompletion(performResult, abortedQueryResult, aborted);
        if (_receiptFailure is not null && !_vetoed)
            result = result with
            {
                Outcome = VideoRecycleOutcome.Unknown,
                RecycledPath = null,
                Reason = $"대상 이동은 관측했지만 정확한 복원 영수증을 확보하지 못해 완료를 확정할 수 없습니다. {_receiptFailure.Message}"
            };
        return result with
        {
            ObservedDestinationPath = !_vetoed && _preCount == 1 && _postCount == 1 && _hasRecycleItem ? _recycledPath : null,
            Diagnostics = $"pre={_preCount}, post={_postCount}, postHRESULT=0x{_postResult:X8}, " +
                $"performHRESULT=0x{performResult:X8}, abortedQueryHRESULT=0x{abortedQueryResult:X8}, " +
                $"aborted={aborted}, recycleItem={_hasRecycleItem}, safetyVeto={_vetoed}, " +
                $"attributesHRESULT=0x{_attributeQueryResult:X8}, attributes=0x{_attributes:X8}" +
                (_receiptFailure is null ? string.Empty :
                    $", receiptError={_receiptFailure.GetType().FullName}, receiptHRESULT=0x{_receiptFailure.HResult:X8}, receiptMessage={_receiptFailure.Message}")
        };
    }

    private VideoRecycleResult ClassifyCompletion(int performResult, int abortedQueryResult, bool aborted)
    {
        if (_vetoed) return new(VideoRecycleOutcome.Failed, "안전한 휴지통 이동을 보장할 수 없어 작업을 중단했습니다.");
        // Positive HRESULTs include COPYENGINE_S_PENDING/PENDING_DELETE and skipped work.
        // A validated single file can report DONT_PROCESS_CHILDREN after recycling; it has no
        // children to process. This exception applies only to PostDelete, never the global results.
        if (_preCount == 1 && _postCount == 1 && _postResult is 0 or CopyEngineDontProcessChildren && _hasRecycleItem &&
            performResult == 0 && abortedQueryResult == 0 && !aborted)
            return new(VideoRecycleOutcome.Succeeded, RecycledPath: _recycledPath);

        // A successful per-item callback followed by a conflicting global result is uncertain,
        // not cancellation: the file may already have moved.
        if (_postCount == 1 && _postResult >= 0 || performResult > 0 || abortedQueryResult != 0)
            return new(VideoRecycleOutcome.Unknown, "Windows에서 휴지통 이동 완료를 확인하지 못했습니다. 파일 상태를 확인해 주세요.");
        if (IsCancellation(_postResult) || IsCancellation(performResult))
            return new(VideoRecycleOutcome.Cancelled, "휴지통 이동이 취소되었습니다.");
        var failure = _postCount == 1 && _postResult < 0 ? _postResult : performResult;
        if (failure < 0)
            return new(VideoRecycleOutcome.Failed, Marshal.GetExceptionForHR(failure)?.Message ?? $"Windows 오류: 0x{failure:X8}");
        return new(VideoRecycleOutcome.Unknown, "Windows에서 대상 파일의 휴지통 이동 결과를 확인하지 못했습니다.");
    }

    private bool Matches(string? path)
    {
        try { return path is not null && Path.GetFullPath(path).Equals(absolutePath, StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool IsCancellation(int value) => value is unchecked((int)0x800704C7) or unchecked((int)0x80270000);
}

public sealed partial class WindowsVideoRecycleService : IVideoRecycleService
{
    public Task<VideoRecycleResult> RecycleAsync(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath) || !Path.IsPathFullyQualified(absolutePath))
            return Task.FromResult(new VideoRecycleResult(VideoRecycleOutcome.Failed, "유효한 절대 파일 경로가 아닙니다.") { SideEffectsKnownAbsent = true });
        var completion = new TaskCompletionSource<VideoRecycleResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                dispatcher.BeginInvoke(new Action(() =>
                {
                    try { completion.TrySetResult(RecycleOnSta(Path.GetFullPath(absolutePath))); }
                    catch (Exception ex) { completion.TrySetResult(new(VideoRecycleOutcome.Failed, ex.Message)); }
                    finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
                }));
                Dispatcher.Run();
            }
            catch (Exception ex) { completion.TrySetResult(new(VideoRecycleOutcome.Failed, ex.Message)); }
        }) { IsBackground = true, Name = "Airi video recycle STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static VideoRecycleResult RecycleOnSta(string path)
    {
        if (!File.Exists(path) || Directory.Exists(path))
            return Refused("대상 파일이 없거나 일반 파일이 아닙니다.");
        IFileOperation? operation = null;
        IShellItem? item = null;
        var state = new RecycleOperationState(path);
        var started = false;
        try
        {
            var sourceIdentity = ReadIdentity(path);
            var operationType = Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), throwOnError: true)!;
            operation = (IFileOperation)Activator.CreateInstance(operationType)!;
            // FOFX_RECYCLEONDELETE | FOF_NO_CONNECTED_ELEMENTS | FOF_SILENT |
            // FOF_NOERRORUI | FOFX_EARLYFAILURE | FOF_WANTNUKEWARNING | FOF_NOCONFIRMATION.
            // NOCONFIRMATION is paired with the recycle flag gate below; it is never a nuke fallback.
            Marshal.ThrowExceptionForHR(operation.SetOperationFlags(0x00080000 | 0x2000 | 0x4 | 0x400 | 0x00100000 | 0x4000 | 0x10));
            var iid = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item));
            var sink = new RecycleProgressSink(state, path, sourceIdentity);
            Marshal.ThrowExceptionForHR(operation.DeleteItem(item, sink));
            started = true;
            var perform = operation.PerformOperations();
            var query = operation.GetAnyOperationsAborted(out var aborted);
            GC.KeepAlive(sink);
            var result = state.Complete(perform, query, aborted);
            if (result.ObservedDestinationPath is not null) result = result with { Receipt = sink.Receipt };
            if (result.Outcome != VideoRecycleOutcome.Succeeded) return result;
            if (sink.Receipt is null || InspectReceipt(path, sink.Receipt) is not
                { SourceState: VideoFileEvidence.Missing, RecycleState: VideoFileEvidence.MatchesReceipt })
                return result with { Outcome = VideoRecycleOutcome.Unknown, RecycledPath = null,
                    Reason = "휴지통 이동의 정확한 복원 영수증을 확인하지 못했습니다." };
            return result with { Receipt = sink.Receipt, MovementConfirmed = true };
        }
        catch (Exception ex)
        {
            return new(started ? VideoRecycleOutcome.Unknown : VideoRecycleOutcome.Failed, ex.Message) { SideEffectsKnownAbsent = !started };
        }
        finally
        {
            if (item is not null) Marshal.FinalReleaseComObject(item);
            if (operation is not null) Marshal.FinalReleaseComObject(operation);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, out IShellItem item);

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class RecycleProgressSink(RecycleOperationState state, string sourcePath, NativeFileIdentity sourceIdentity) : IFileOperationProgressSink
    {
        public string? Receipt => state.Receipt;
        private const int Abort = unchecked((int)0x80004004);
        public int StartOperations() => 0;
        public int FinishOperations(int result) => 0;
        public int PreRenameItem(uint flags, IShellItem item, string name) => AbortUnexpected();
        public int PostRenameItem(uint flags, IShellItem item, string name, int result, IShellItem? created) => 0;
        public int PreMoveItem(uint flags, IShellItem item, IShellItem destination, string? name) => AbortUnexpected();
        public int PostMoveItem(uint flags, IShellItem item, IShellItem destination, string? name, int result, IShellItem? created) => 0;
        public int PreCopyItem(uint flags, IShellItem item, IShellItem destination, string? name) => AbortUnexpected();
        public int PostCopyItem(uint flags, IShellItem item, IShellItem destination, string? name, int result, IShellItem? created) => 0;
        public int PreDeleteItem(uint flags, IShellItem item)
        {
            try
            {
                var path = GetPath(item);
                var query = item.GetAttributes(RecycleOperationState.FileSystemAttribute | RecycleOperationState.FolderAttribute, out var attributes);
                return state.BeforeDelete(path, flags, query, attributes) && ReadIdentity(sourcePath) == sourceIdentity ? 0 : AbortUnexpected();
            }
            catch { return AbortUnexpected(); }
        }
        public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created)
        {
            try
            {
                state.AfterDelete(GetPath(item), result, created is not null, TryGetPath(created));
                if (created is not null && result is 0 or 0x00270008)
                    state.CaptureReceipt(() => CreateReceipt(sourcePath, sourceIdentity, created));
            }
            catch { state.Veto(); }
            finally { if (created is not null) Marshal.ReleaseComObject(created); }
            return 0;
        }
        public int PreNewItem(uint flags, IShellItem destination, string name) => AbortUnexpected();
        public int PostNewItem(uint flags, IShellItem destination, string name, string? template, uint attributes, int result, IShellItem? created) => 0;
        public int UpdateProgress(uint total, uint completed) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
        private int AbortUnexpected() { state.Veto(); return Abort; }
    }

    private static string? TryGetPath(IShellItem? item)
    {
        try { return item is null ? null : GetPath(item); }
        catch (COMException) { return null; }
    }

    private static string GetPath(IShellItem item)
    {
        Marshal.ThrowExceptionForHR(item.GetDisplayName(0x80058000, out var value)); // SIGDN_FILESYSPATH
        try { return Marshal.PtrToStringUni(value) ?? string.Empty; }
        finally { Marshal.FreeCoTaskMem(value); }
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint kind, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    [ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? created);
        [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name);
        [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, int result, IShellItem? created);
        [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name);
        [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, int result, IShellItem? created);
        [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
        [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created);
        [PreserveSig] int PreNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? template, uint attributes, int result, IShellItem? created);
        [PreserveSig] int UpdateProgress(uint total, uint completed);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IFileOperationProgressSink sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog([MarshalAs(UnmanagedType.Interface)] object dialog);
        [PreserveSig] int SetProperties([MarshalAs(UnmanagedType.Interface)] object properties);
        [PreserveSig] int SetOwnerWindow(IntPtr owner);
        [PreserveSig] int ApplyPropertiesToItem(IShellItem item);
        [PreserveSig] int ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        [PreserveSig] int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink? sink);
        [PreserveSig] int RenameItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IFileOperationProgressSink? sink);
        [PreserveSig] int MoveItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destination);
        [PreserveSig] int CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IFileOperationProgressSink? sink);
        [PreserveSig] int CopyItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destination);
        [PreserveSig] int DeleteItem(IShellItem item, IFileOperationProgressSink sink);
        [PreserveSig] int DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        [PreserveSig] int NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? template, IFileOperationProgressSink? sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
