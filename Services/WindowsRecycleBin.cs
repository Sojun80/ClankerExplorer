using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;

namespace ClankerExplorer.Services;

public interface IWindowsRecycleBin
{
    string Recycle(string path);
    void Restore(string nativeItem, string destination);
}

/// <summary>Windows shell recycling. A permanent-delete callback is always refused.</summary>
internal sealed class WindowsRecycleBin : IWindowsRecycleBin
{
    public string Recycle(string path) => OnSta(() => Execute(path, null));
    public void Restore(string nativeItem, string destination) => OnSta(() => Execute(nativeItem, destination));

    private static T OnSta<T>(Func<T> action)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() => { try { result = action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }

    private static string Execute(string path, string? destination)
    {
        IFileOperation? operation = null;
        IShellItem? item = null, folder = null;
        var sink = new ProgressSink();
        try
        {
            operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(
                new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), throwOnError: true)!)!;
            // Recycle plus undo record, early failure, and no shell UI (the app confirms).
            // PreDeleteItem refuses any callback without TSF_DELETE_RECYCLE_IF_POSSIBLE.
            // This is also the safeguard used by Electron's MoveItemToTrashWithError.
            operation.SetOperationFlags(0x00080000 | 0x00100000 | 0x20000000 | 0x400 | 0x4 | 0x10 | 0x200);
            item = CreateItem(path);
            if (destination == null) operation.DeleteItem(item, sink);
            else
            {
                folder = CreateItem(Path.GetDirectoryName(destination)!);
                operation.MoveItem(item, folder, Path.GetFileName(destination), sink);
            }
            operation.PerformOperations();
            operation.GetAnyOperationsAborted(out bool aborted);
            if (sink.Error < 0) Marshal.ThrowExceptionForHR(sink.Error);
            if (aborted) throw new IOException("Windows cancelled the recycle operation; no permanent deletion was allowed.");
            return sink.NewItem ?? throw new IOException("Windows did not confirm a recoverable recycle item.");
        }
        catch (COMException ex)
        {
            string reason = unchecked((uint)ex.HResult) switch
            {
                0x80270037 => "The item is too large for the Windows Recycle Bin.",
                0x80270036 => "Windows recycling is disabled or unavailable for this location.",
                0x80270038 => "The path is too long for the Windows Recycle Bin.",
                0x8027003A => "The Windows Recycle Bin is unavailable.",
                0x80004004 when sink.RefusedPermanentDelete => "Windows could not recycle this item. Permanent deletion was prevented.",
                _ => $"Windows Recycle Bin error 0x{ex.HResult:X8}: {ex.Message}"
            };
            throw new IOException(reason, ex);
        }
        finally
        {
            if (folder != null) Marshal.FinalReleaseComObject(folder);
            if (item != null) Marshal.FinalReleaseComObject(item);
            if (operation != null) Marshal.FinalReleaseComObject(operation);
        }
    }

    private static IShellItem CreateItem(string path)
    {
        Guid iid = typeof(IShellItem).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item));
        return item;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItem item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint kind, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IProgressSink sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(IntPtr window);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems(IntPtr items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IProgressSink sink);
        void RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name, IProgressSink sink);
        void MoveItems(IntPtr items, IShellItem folder);
        void CopyItem(IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name, IProgressSink sink);
        void CopyItems(IntPtr items, IShellItem folder);
        void DeleteItem(IShellItem item, IProgressSink sink);
        void DeleteItems(IntPtr items);
        void NewItem(IShellItem folder, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string template, IProgressSink sink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? created);
        [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? created);
        [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? created);
        [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
        [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created);
        [PreserveSig] int PreNewItem(uint flags, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostNewItem(uint flags, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string template, uint attributes, int result, IShellItem? created);
        [PreserveSig] int UpdateProgress(uint total, uint soFar);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class ProgressSink : IProgressSink
    {
        public string? NewItem { get; private set; }
        public int Error { get; private set; }
        public bool RefusedPermanentDelete { get; private set; }
        private int Completed(int result, IShellItem? created)
        {
            Error = result;
            if (result >= 0 && created != null)
            {
                created.GetDisplayName(0x80028000, out var name); // DESKTOPABSOLUTEPARSING
                try { NewItem = Marshal.PtrToStringUni(name); }
                finally { Marshal.FreeCoTaskMem(name); }
            }
            return 0;
        }
        public int StartOperations() => 0;
        public int FinishOperations(int result) { if (result < 0) Error = result; return 0; }
        public int PreRenameItem(uint f, IShellItem i, string n) => 0;
        public int PostRenameItem(uint f, IShellItem i, string n, int r, IShellItem? c) => Completed(r, c);
        public int PreMoveItem(uint f, IShellItem i, IShellItem d, string n)
        {
            d.GetDisplayName(0x80058000, out var folder); // FILESYSPATH
            try
            {
                string path = Path.Combine(Marshal.PtrToStringUni(folder)!, n);
                return File.Exists(path) || Directory.Exists(path) ? unchecked((int)0x80070050) : 0;
            }
            finally { Marshal.FreeCoTaskMem(folder); }
        }
        public int PostMoveItem(uint f, IShellItem i, IShellItem d, string n, int r, IShellItem? c) => Completed(r, c);
        public int PreCopyItem(uint f, IShellItem i, IShellItem d, string n) => 0;
        public int PostCopyItem(uint f, IShellItem i, IShellItem d, string n, int r, IShellItem? c) => Completed(r, c);
        public int PreDeleteItem(uint flags, IShellItem item)
        {
            // The shell removes this flag when recycling is unavailable (size, policy, volume, etc.).
            if ((flags & 0x80) != 0) return 0; // TSF_DELETE_RECYCLE_IF_POSSIBLE
            RefusedPermanentDelete = true;
            Error = unchecked((int)0x80004004); // E_ABORT
            return Error;
        }
        public int PostDeleteItem(uint f, IShellItem i, int r, IShellItem? c) => Completed(r, c);
        public int PreNewItem(uint f, IShellItem d, string n) => 0;
        public int PostNewItem(uint f, IShellItem d, string n, string t, uint a, int r, IShellItem? c) => Completed(r, c);
        public int UpdateProgress(uint t, uint s) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }
}
