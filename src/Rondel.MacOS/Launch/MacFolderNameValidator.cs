using System.Runtime.InteropServices;
using Rondel.Core.Launch;
using Rondel.Core.Localization;
using Rondel.Core.Platform;

namespace Rondel.MacOS.Launch;

/// <summary>
/// <see cref="MacFolderNameRules"/> plus what only the file system can answer: whether the user may create a folder
/// in the parent, asked of the nearest folder that exists, without creating anything.
/// </summary>
internal sealed partial class MacFolderNameValidator : IFolderNameValidator
{
    const int W_OK = 2, X_OK = 1;

    [LibraryImport("/usr/lib/libSystem.B.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int access(string path, int mode);

    public string? Validate(string parent, string name)
    {
        if (MacFolderNameRules.Validate(parent, name) is { } error)
            return error;
        string? existing = parent;
        while (existing is not null && !Directory.Exists(existing))
            existing = Path.GetDirectoryName(existing);
        // Only a definite "no" blocks; anything else surfaces when the folder is created.
        return existing is null || access(existing, W_OK | X_OK) == 0 ? null : Loc.Format("Validation.NoWriteAccess", parent);
    }
}
