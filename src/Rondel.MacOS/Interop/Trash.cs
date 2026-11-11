using Rondel.Core.Localization;
using Rondel.Core.Logging;
using static Rondel.MacOS.Interop.Native;

namespace Rondel.MacOS.Interop;

/// <summary>Moves files and folders to the Trash, as Finder's Move to Trash does (NSFileManager trashItemAtURL).</summary>
internal static class Trash
{
    /// <summary>
    /// Moves <paramref name="paths"/> to the Trash, one by one. A refused path does not stop the others, and one gone
    /// since it was listed counts as moved. Transcripts go last: a session whose transcript stays is still listed, so it
    /// can be deleted again, while one that lost its transcript would leave its other folders out of sight.
    /// </summary>
    /// <exception cref="IOException">macOS did not move some; the others are in the Trash.</exception>
    public static unsafe void Move(IReadOnlyList<string> paths)
    {
        using var pool = new AutoreleasePool();
        nint manager = Send(Class("NSFileManager"), Sel("defaultManager"));
        var failed = new List<string>();
        string? firstReason = null;
        foreach (string path in paths.OrderBy(p => p.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)))
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                continue;
            nint text = CFString(path);
            nint url = Send(Class("NSURL"), Sel("fileURLWithPath:"), text);
            CFRelease(text);
            nint error = 0;
            bool moved = (Send(manager, Sel("trashItemAtURL:resultingItemURL:error:"), url, 0, (nint)(&error)) & 0xFF) != 0;
            if (moved)
                continue;
            // macOS words the reason in the user's language.
            string reason = (error == 0 ? null : String(Send(error, Sel("localizedDescription")))) ?? "?";
            Log.Warn($"Moving '{path}' to the Trash failed: {reason}");
            failed.Add(path);
            firstReason ??= reason;
        }
        if (failed.Count > 0)
            throw new IOException(Loc.Format("Sessions.TrashFailed.Mac", failed.Count, firstReason));
    }
}
