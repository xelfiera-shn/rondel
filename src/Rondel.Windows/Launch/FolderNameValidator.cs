using System.Runtime.InteropServices;
using Microsoft.Win32;
using Rondel.Core.Launch;
using Rondel.Core.Localization;
using Rondel.Core.Platform;
using Rondel.Windows.Interop;

namespace Rondel.Windows.Launch;

/// <summary>
/// <see cref="FolderNameRules"/> plus what only Windows can answer: whether long paths are enabled
/// and whether the user may create a folder in the parent.
/// </summary>
internal sealed class FolderNameValidator : IFolderNameValidator
{
    static readonly Lazy<bool> LongPathsEnabled = new(() =>
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem");
            return key?.GetValue("LongPathsEnabled") is int value && value == 1;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    });

    (string Directory, bool CanCreate, long Tick) _accessCache;

    /// <returns>A localized error, or null when <paramref name="name"/> can be created in <paramref name="parent"/>.</returns>
    public string? Validate(string parent, string name)
    {
        int limit = LongPathsEnabled.Value ? FolderNameRules.LongPathLimit : FolderNameRules.ClassicPathLimit;
        if (FolderNameRules.Validate(parent, name, limit) is { } error)
            return error;
        return CanCreateIn(parent) ? null : Loc.Format("Validation.NoWriteAccess", parent);
    }

    /// <summary>
    /// Asks the file system whether we may add a subdirectory to the nearest existing ancestor,
    /// without creating anything (no temp folders appearing in Explorer or OneDrive).
    /// </summary>
    bool CanCreateIn(string directory)
    {
        long now = Environment.TickCount64;
        var cached = _accessCache;
        if (cached.Directory == directory && now - cached.Tick < 10_000)
            return cached.CanCreate;

        string? existing = directory;
        while (existing is not null && !Directory.Exists(existing))
            existing = Path.GetDirectoryName(existing);

        bool canCreate = true;
        if (existing is not null)
        {
            string path = existing.Length >= 248 && !existing.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\" + existing : existing;
            using var handle = Native.CreateFile(path, Native.FILE_ADD_SUBDIRECTORY, Native.FILE_SHARE_ALL, 0,
                Native.OPEN_EXISTING, Native.FILE_FLAG_BACKUP_SEMANTICS, 0);
            // Only a definite "access denied" blocks; anything else surfaces when creating.
            canCreate = !handle.IsInvalid || Marshal.GetLastPInvokeError() != Native.ERROR_ACCESS_DENIED;
        }
        _accessCache = (directory, canCreate, now);
        return canCreate;
    }
}
