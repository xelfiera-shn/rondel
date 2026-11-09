using Rondel.Core.Localization;

namespace Rondel.Core.Activity;

/// <summary>How the agents card words the usage limits.</summary>
public static class UsageText
{
    /// <summary>Figures older than this are shown with the time they are from.</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(10);

    /// <summary>A share from here on is shown in amber.</summary>
    public const double High = 75;

    /// <summary>A share from here on is shown in red.</summary>
    public const double Critical = 90;

    /// <summary>The window started over since it was reported, so its share no longer holds.</summary>
    public static bool HasReset(UsageWindow window, DateTimeOffset now) => now >= window.ResetsAt;

    /// <summary>The share that holds now; null when there is none (not reported, or reset since).</summary>
    public static double? Share(UsageWindow? window, DateTimeOffset now) =>
        window is null || HasReset(window, now) ? null : window.Percent;

    /// <summary>"81%" ("%81" in Turkish), or "—" when no share holds.</summary>
    public static string Percent(UsageWindow? window, DateTimeOffset now) =>
        Share(window, now) is { } share ? (Math.Round(share) / 100).ToString("P0", Loc.Culture) : "—";

    /// <summary>
    /// When the window starts over: "Resets 6:30 PM · in 1 h 25 min" (<paramref name="countdown"/>), "Resets Sat 10:00 AM",
    /// or, once it has, that the next reply brings the new share.
    /// </summary>
    public static string Resets(UsageWindow window, DateTimeOffset now, bool countdown)
    {
        string at = When(window.ResetsAt, now);
        if (HasReset(window, now))
            return Loc.Format("Card.Usage.WasReset", at);
        return countdown ? Loc.Format("Card.Usage.ResetsIn", at, Duration(window.ResetsAt - now)) : Loc.Format("Card.Usage.Resets", at);
    }

    /// <summary>"as of 2:05 PM" when the figures are older than <see cref="Stale"/>; null while they are fresh.</summary>
    public static string? AsOf(UsageLimits limits, DateTimeOffset now) =>
        now - limits.Reported > Stale ? Loc.Format("Card.Usage.AsOf", When(limits.Reported, now)) : null;

    /// <summary>The local time, with the day in front when it is not today.</summary>
    public static string When(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime();
        string time = local.ToString("t", Loc.Culture);
        return local.Date == now.ToLocalTime().Date ? time : local.ToString("ddd", Loc.Culture) + " " + time;
    }

    /// <summary>"1 h 25 min", "2 h", "40 min"; at least a minute.</summary>
    static string Duration(TimeSpan left)
    {
        int minutes = Math.Max(1, (int)Math.Ceiling(left.TotalMinutes));
        int hours = minutes / 60;
        minutes %= 60;
        if (hours == 0)
            return Loc.Format("Duration.Minutes", minutes);
        string text = Loc.Format("Duration.Hours", hours);
        return minutes == 0 ? text : text + " " + Loc.Format("Duration.Minutes", minutes);
    }
}
