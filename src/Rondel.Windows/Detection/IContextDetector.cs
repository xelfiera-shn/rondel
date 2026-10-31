using Rondel.Core.Context;
using Rondel.Windows.Interop;

namespace Rondel.Windows.Detection;

internal interface IContextDetector
{
    /// <summary>Runs on the STA detection thread. Must not throw for ordinary failures; return None instead.</summary>
    TriggerContext Detect(POINT screenPoint, nint rootWindow);
}
