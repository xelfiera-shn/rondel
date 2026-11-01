using System.Globalization;

namespace Rondel.Core.Updates;

/// <summary>A release's version, major.minor.patch, as its tag carries it ("v0.6.0").</summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch) : IComparable<AppVersion>
{
    /// <summary>This build's version; 0.0.0 for local and CI test builds.</summary>
    public static AppVersion Current { get; } = TryParse(AppInfo.Version, out var version) ? version : default;

    /// <summary>Local and CI test builds are 0.0.0: they never look for updates, so they never replace themselves.</summary>
    public bool IsDevelopment => this == default;

    /// <summary>
    /// "0.6.0" or "v0.6.0". A pre-release ("0.7.0-beta.1") or anything else is not a version Rondel updates to.
    /// </summary>
    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;
        string value = text?.Trim() ?? "";
        if (value.StartsWith('v') || value.StartsWith('V'))
            value = value[1..];
        string[] parts = value.Split('.');
        if (parts.Length != 3)
            return false;
        int[] numbers = new int[3];
        for (int i = 0; i < 3; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return false;
        }
        version = new AppVersion(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    public int CompareTo(AppVersion other) => (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));

    public static bool operator <(AppVersion left, AppVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(AppVersion left, AppVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(AppVersion left, AppVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(AppVersion left, AppVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}
