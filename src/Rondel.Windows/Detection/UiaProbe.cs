using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using Rondel.Windows.Interop;

namespace Rondel.Windows.Detection;

internal enum UiaHitKind
{
    /// <summary>Empty area of a shell items view (desktop list view or Explorer UIItemsView).</summary>
    Empty,
    /// <summary>On an item (icon, label, or anywhere in a Details-view row).</summary>
    Item,
    /// <summary>Inside the items view but on something that is not an item or empty space (column header, scroll bar, rename box...).</summary>
    Blocked,
    /// <summary>Not inside a shell items view at all.</summary>
    Outside,
    Failed,
}

/// <param name="ItemName">Display name of the item (UIA Name), for <see cref="UiaHitKind.Item"/>.</param>
/// <param name="ItemType">UIA ItemType of the item, e.g. "File folder" - localized, compared against the shell's own Type string.</param>
internal readonly record struct UiaHit(UiaHitKind Kind, string? ItemName, string? ItemType, string Detail);

/// <summary>
/// UI Automation (native UIA3 COM) probe: ElementFromPoint plus the ancestor chain, with all
/// needed properties fetched through one cache request per element. Only non-localized
/// identifiers (control type ids, class names) drive decisions. Lives on the detection thread.
/// </summary>
internal sealed class UiaProbe
{
    const int MaxDepth = 32;

    // UIA property ids
    const int ControlTypeProperty = 30003;
    const int NameProperty = 30005;
    const int AutomationIdProperty = 30011;
    const int ClassNameProperty = 30012;
    const int NativeWindowHandleProperty = 30020;
    const int ItemTypeProperty = 30021;
    const int BoundingRectangleProperty = 30001;

    // UIA control type ids
    const int Button = 50000, CheckBox = 50002, Edit = 50004, Hyperlink = 50005, ListItem = 50007, List = 50008,
        MenuItem = 50011, ScrollBar = 50014, Thumb = 50027, DataGrid = 50028, DataItem = 50029, SplitButton = 50031,
        Header = 50034, HeaderItem = 50035, Table = 50036;

    static readonly Dictionary<int, string> ControlTypeNames = new()
    {
        [50000] = "Button", [50002] = "CheckBox", [50003] = "ComboBox", [50004] = "Edit", [50005] = "Hyperlink",
        [50006] = "Image", [50007] = "ListItem", [50008] = "List", [50011] = "MenuItem", [50014] = "ScrollBar",
        [50018] = "Tab", [50019] = "TabItem", [50020] = "Text", [50021] = "ToolBar", [50023] = "Tree",
        [50024] = "TreeItem", [50025] = "Custom", [50026] = "Group", [50027] = "Thumb", [50028] = "DataGrid",
        [50029] = "DataItem", [50030] = "Document", [50031] = "SplitButton", [50032] = "Window", [50033] = "Pane",
        [50034] = "Header", [50035] = "HeaderItem", [50036] = "Table", [50037] = "TitleBar",
    };

    readonly IUIAutomation _automation;
    readonly IUIAutomationCacheRequest _cache;
    readonly IUIAutomationTreeWalker _walker;
    readonly IUIAutomationCondition _anyElement;

    public UiaProbe()
    {
        var automation = new CUIAutomation8();
        if (automation is IUIAutomation2 withTimeouts)
        {
            // A hung Explorer must not hold the detection thread for UIA's default 20 s.
            withTimeouts.ConnectionTimeout = 1500;
            withTimeouts.TransactionTimeout = 1500;
        }
        _automation = automation;
        _cache = automation.CreateCacheRequest();
        foreach (int property in new[] { ControlTypeProperty, NameProperty, AutomationIdProperty, ClassNameProperty, NativeWindowHandleProperty, ItemTypeProperty, BoundingRectangleProperty })
            _cache.AddProperty(property);
        _walker = automation.RawViewWalker;
        _anyElement = automation.CreateTrueCondition();
    }

    /// <param name="hitWindow">WindowFromPoint result; UIA's own hit test must land inside it.</param>
    public UiaHit Probe(POINT point, nint rootWindow, nint hitWindow, List<string>? trace)
    {
        try
        {
            var chain = ChainFromPoint(point, rootWindow);
            if (!chain.Any(e => e.CachedNativeWindowHandle == hitWindow))
            {
                // Click-through overlays (NVIDIA/Discord/Game Bar...) are skipped by WindowFromPoint but
                // not by UIA's ElementFromPoint. Hit-test inside the real window instead.
                trace?.Add("ElementFromPoint landed outside the window under the cursor (overlay?): "
                    + string.Join(" > ", chain.Take(4).Select(e => $"{TypeName(e.CachedControlType)} '{e.CachedClassName}'")));
                chain = ChainByDescent(point, hitWindow, rootWindow);
            }
            return Classify(chain, trace);
        }
        catch (COMException ex)
        {
            return new UiaHit(UiaHitKind.Failed, null, null, $"UIA error 0x{ex.HResult:X8}: {ex.Message}");
        }
        catch (InvalidCastException ex)
        {
            return new UiaHit(UiaHitKind.Failed, null, null, $"UIA error: {ex.Message}");
        }
    }

    /// <summary>ElementFromPoint and its ancestors up to the top-level window (deepest first).</summary>
    List<IUIAutomationElement> ChainFromPoint(POINT point, nint rootWindow)
    {
        var chain = new List<IUIAutomationElement>();
        var element = _automation.ElementFromPointBuildCache(new tagPOINT { x = point.X, y = point.Y }, _cache);
        AppendAncestors(chain, element, rootWindow);
        return chain;
    }

    /// <summary>
    /// Starts at the UIA element of <paramref name="hitWindow"/> and repeatedly steps into the smallest
    /// child whose bounding rectangle contains the point; then adds the ancestors (deepest first).
    /// </summary>
    List<IUIAutomationElement> ChainByDescent(POINT point, nint hitWindow, nint rootWindow)
    {
        var start = _automation.ElementFromHandleBuildCache(hitWindow, _cache);
        var path = new List<IUIAutomationElement> { start };
        var current = start;
        for (int depth = 0; depth < 10; depth++)
        {
            var children = current.FindAllBuildCache(TreeScope.TreeScope_Children, _anyElement, _cache);
            IUIAutomationElement? best = null;
            long bestArea = long.MaxValue;
            for (int i = 0; i < (children?.Length ?? 0); i++)
            {
                var child = children!.GetElement(i);
                var r = child.CachedBoundingRectangle;
                if (point.X < r.left || point.X >= r.right || point.Y < r.top || point.Y >= r.bottom)
                    continue;
                long area = (long)(r.right - r.left) * (r.bottom - r.top);
                if (area < bestArea)
                {
                    best = child;
                    bestArea = area;
                }
            }
            if (best is null)
                break;
            path.Add(best);
            current = best;
        }
        path.Reverse();
        var chain = new List<IUIAutomationElement>(path);
        if (start.CachedNativeWindowHandle != rootWindow)
            AppendAncestors(chain, _walker.GetParentElementBuildCache(start, _cache), rootWindow);
        return chain;
    }

    void AppendAncestors(List<IUIAutomationElement> chain, IUIAutomationElement? element, nint rootWindow)
    {
        while (element is not null && chain.Count < MaxDepth)
        {
            chain.Add(element);
            if (element.CachedNativeWindowHandle == rootWindow)
                break;
            element = _walker.GetParentElementBuildCache(element, _cache);
        }
    }

    /// <summary>Walks the chain from the deepest element outwards until the items view.</summary>
    static UiaHit Classify(List<IUIAutomationElement> chain, List<string>? trace)
    {
        string? itemName = null, itemType = null;
        bool haveItem = false;
        for (int depth = 0; depth < chain.Count; depth++)
        {
            var element = chain[depth];
            int type = element.CachedControlType;
            string cls = element.CachedClassName ?? "";
            string name = element.CachedName ?? "";
            nint hwnd = element.CachedNativeWindowHandle;
            trace?.Add($"[{depth}] {TypeName(type)} class='{cls}' name='{name}' id='{element.CachedAutomationId}'"
                + (hwnd != 0 ? $" hwnd=0x{hwnd:X}" : ""));

            if (IsItemsView(type, cls))
            {
                return haveItem
                    ? new UiaHit(UiaHitKind.Item, itemName, itemType, $"item '{itemName}' in {cls}")
                    : new UiaHit(UiaHitKind.Empty, null, null, $"empty area of {cls}");
            }

            if (haveItem)
                continue;
            if (type is ListItem or DataItem)
            {
                haveItem = true;
                itemName = name;
                itemType = element.CachedItemType;
                trace?.Add($"    item type: '{itemType}'");
            }
            else if (IsBlocking(type, cls))
            {
                return new UiaHit(UiaHitKind.Blocked, null, null, $"{TypeName(type)} '{cls}' is not an item or empty space");
            }
        }
        return new UiaHit(UiaHitKind.Outside, null, null, "no shell items view in the ancestor chain");
    }

    /// <summary>Explorer's DirectUI items view, or the desktop's SysListView32.</summary>
    static bool IsItemsView(int type, string cls) =>
        type is List or DataGrid or Table && cls is "UIItemsView" or "SysListView32";

    /// <summary>
    /// Things inside the items view that are neither an item nor empty space. Text, images and
    /// UIProperty cells (Details view) belong to an item and are not listed here; groups count as
    /// empty space.
    /// </summary>
    static bool IsBlocking(int type, string cls) =>
        type is Header or HeaderItem or ScrollBar or Thumb or Button or SplitButton or Hyperlink or MenuItem or CheckBox
        || cls == "UIColumnHeader" // Details-view column header (SplitButton on Windows 11)
        || (type == Edit && cls == "Edit"); // in-place rename box

    static string TypeName(int type) => ControlTypeNames.TryGetValue(type, out string? name) ? name : type.ToString();
}
