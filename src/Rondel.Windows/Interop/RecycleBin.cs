using System.Runtime.InteropServices;

namespace Rondel.Windows.Interop;

/// <summary>Moves files and folders into the Recycle Bin, as File Explorer's Delete does.</summary>
internal static partial class RecycleBin
{
    const uint FO_DELETE = 3;
    const ushort FOF_SILENT = 0x0004;
    const ushort FOF_NOCONFIRMATION = 0x0010;
    const ushort FOF_ALLOWUNDO = 0x0040;
    const ushort FOF_NOERRORUI = 0x0400;
    const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCTW
    {
        public nint hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public nint hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperationW(ref SHFILEOPSTRUCTW operation);

    /// <summary>
    /// Moves <paramref name="paths"/> into the Recycle Bin in one operation, so one Undo in File Explorer brings them all
    /// back. Windows asks first only for what is too large for the bin and would be deleted for good.
    /// </summary>
    /// <param name="owner">The window Windows' own question belongs to.</param>
    /// <exception cref="IOException">Windows did not move them, or the user stopped it.</exception>
    public static void Move(IReadOnlyList<string> paths, nint owner = 0)
    {
        if (paths.Count == 0)
            return;
        var operation = new SHFILEOPSTRUCTW
        {
            hwnd = owner,
            wFunc = FO_DELETE,
            pFrom = string.Join('\0', paths) + "\0\0", // each path ends in a null, the list in another
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING,
        };
        int result = SHFileOperationW(ref operation);
        if (result != 0)
            throw new IOException($"The Recycle Bin did not take the files (error 0x{result:X}).");
        if (operation.fAnyOperationsAborted != 0)
            throw new IOException("Moving the files into the Recycle Bin was stopped.");
    }
}
