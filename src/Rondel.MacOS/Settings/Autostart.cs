using System.Runtime.InteropServices;
using Rondel.Core.Logging;
using Rondel.Core.Platform;
using Rondel.MacOS.Interop;

namespace Rondel.MacOS.Settings;

/// <summary>
/// "Open at login" through <c>SMAppService.mainAppService</c> (macOS 13): Rondel.app itself becomes a login item, the
/// same entry the user sees in System Settings → General → Login Items. Nothing is written by Rondel.
/// </summary>
internal sealed unsafe class Autostart : IAutostart
{
    const long Enabled = 1, RequiresApproval = 2;

    static readonly nint s_service = MainAppService();

    public bool IsEnabled => Status == Enabled;

    /// <summary>Registered, but the user turned it off in Login Items (or has not allowed it yet).</summary>
    public bool NeedsApproval => Status == RequiresApproval;

    static long Status => s_service == 0 ? 0 : Native.Send(s_service, Native.Sel("status"));

    public void Set(bool enabled)
    {
        if (s_service == 0)
            throw new InvalidOperationException("SMAppService is not available");
        nint error = 0;
        bool done = Native.SendBool(s_service, Native.Sel(enabled ? "registerAndReturnError:" : "unregisterAndReturnError:"), (nint)(&error));
        if (done)
        {
            Log.Info($"Open at login {(enabled ? "on" : "off")} (status {Status})");
            return;
        }
        string? reason = error == 0 ? null : Native.String(Native.Send(error, Native.Sel("localizedDescription")));
        throw new InvalidOperationException($"Turning open at login {(enabled ? "on" : "off")} failed: {reason}");
    }

    /// <summary>Opens System Settings at Login Items, where a login item that needs approval is allowed.</summary>
    public static void OpenLoginItems()
    {
        nint type = Native.Class("SMAppService");
        if (type != 0)
            Native.Send(type, Native.Sel("openSystemSettingsLoginItems"));
    }

    static nint MainAppService()
    {
        if (!NativeLibrary.TryLoad("/System/Library/Frameworks/ServiceManagement.framework/ServiceManagement", out _))
            return 0;
        nint type = Native.Class("SMAppService");
        return type == 0 ? 0 : Native.Send(Native.Send(type, Native.Sel("mainAppService")), Native.Sel("retain"));
    }
}
