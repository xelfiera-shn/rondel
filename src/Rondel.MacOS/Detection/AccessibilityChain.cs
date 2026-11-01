using Rondel.MacOS.Interop;
using static Rondel.MacOS.Interop.Native;

namespace Rondel.MacOS.Detection;

/// <summary>The accessibility element under a point and its ancestors, with the attributes detection decides on.</summary>
internal static class AccessibilityChain
{
    /// <param name="Url">The file or folder the element stands for (AXURL), as a path.</param>
    /// <param name="Title">For the trace only: titles are localized and never decide anything.</param>
    internal sealed record Node(string Role, string? Subrole, string? Identifier, string? Url, string? Title)
    {
        public override string ToString() =>
            Role + (Subrole is null ? "" : "/" + Subrole) + (Identifier is null ? "" : $" id={Identifier}")
            + (Url is null ? "" : $" url={Url}") + (Title is null ? "" : $" title=\"{Title}\"");
    }

    const int MaxDepth = 40;

    static readonly nint SystemWide = CreateSystemWide();
    static readonly nint Role = CFString("AXRole"), Subrole = CFString("AXSubrole"), Identifier = CFString("AXIdentifier"),
        Url = CFString("AXURL"), Title = CFString("AXTitle"), Parent = CFString("AXParent");

    static nint CreateSystemWide()
    {
        nint element = AXUIElementCreateSystemWide();
        // Finder answers in milliseconds; a hung app must not hold the gesture for the default six seconds.
        AXUIElementSetMessagingTimeout(element, 0.5f);
        return element;
    }

    /// <returns>Innermost first; empty with <paramref name="error"/> set when there is nothing to read.</returns>
    public static List<Node> At(CGPoint point, out int pid, out string? error)
    {
        var chain = new List<Node>();
        pid = 0;
        error = null;
        int status = AXUIElementCopyElementAtPosition(SystemWide, (float)point.X, (float)point.Y, out nint element);
        if (status != 0)
        {
            error = $"no accessibility element at the point (AXError {status})";
            return chain;
        }
        AXUIElementGetPid(element, out pid);
        for (int depth = 0; element != 0 && depth < MaxDepth; depth++)
        {
            chain.Add(new Node(StringValue(element, Role) ?? "?", StringValue(element, Subrole), StringValue(element, Identifier),
                PathValue(element, Url), StringValue(element, Title)));
            nint parent = Copy(element, Parent);
            CFRelease(element);
            element = parent;
        }
        if (element != 0)
            CFRelease(element);
        return chain;
    }

    static nint Copy(nint element, nint attribute) =>
        AXUIElementCopyAttributeValue(element, attribute, out nint value) == 0 ? value : 0;

    static string? StringValue(nint element, nint attribute)
    {
        nint value = Copy(element, attribute);
        if (value == 0)
            return null;
        string? text = CFGetTypeID(value) == CFStringGetTypeID() ? CFStringToString(value) : null;
        CFRelease(value);
        return text;
    }

    /// <summary>A file URL attribute (a CFURL, or a URL string) as a POSIX path.</summary>
    static string? PathValue(nint element, nint attribute)
    {
        nint value = Copy(element, attribute);
        if (value == 0)
            return null;
        nint url = value, created = 0;
        if (CFGetTypeID(value) == CFStringGetTypeID())
            url = created = CFURLCreateWithString(0, value, 0);
        else if (CFGetTypeID(value) != CFURLGetTypeID())
            url = 0;
        string? path = null;
        if (url != 0)
        {
            nint text = CFURLCopyFileSystemPath(url, 0 /* POSIX */);
            path = CFStringToString(text);
            if (text != 0)
                CFRelease(text);
        }
        if (created != 0)
            CFRelease(created);
        CFRelease(value);
        return path;
    }
}
