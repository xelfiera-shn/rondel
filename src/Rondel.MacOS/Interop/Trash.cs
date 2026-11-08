using static Rondel.MacOS.Interop.Native;

namespace Rondel.MacOS.Interop;

/// <summary>Moves files and folders to the Trash, as Finder's Move to Trash does (NSFileManager trashItemAtURL).</summary>
internal static class Trash
{
    /// <summary>Moves <paramref name="paths"/> to the Trash, one by one; Finder's Put Back brings each one back.</summary>
    /// <exception cref="IOException">macOS did not move one; those before it are in the Trash already.</exception>
    public static unsafe void Move(IReadOnlyList<string> paths)
    {
        using var pool = new AutoreleasePool();
        nint manager = Send(Class("NSFileManager"), Sel("defaultManager"));
        foreach (string path in paths)
        {
            nint text = CFString(path);
            nint url = Send(Class("NSURL"), Sel("fileURLWithPath:"), text);
            CFRelease(text);
            nint error = 0;
            bool moved = (Send(manager, Sel("trashItemAtURL:resultingItemURL:error:"), url, 0, (nint)(&error)) & 0xFF) != 0;
            if (!moved)
            {
                string reason = error == 0 ? "unknown error" : String(Send(error, Sel("localizedDescription"))) ?? "unknown error";
                throw new IOException($"Moving '{path}' to the Trash failed: {reason}");
            }
        }
    }
}
