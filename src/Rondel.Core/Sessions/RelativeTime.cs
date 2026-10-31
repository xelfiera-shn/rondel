using Rondel.Core.Localization;

namespace Rondel.Core.Sessions;

public static class RelativeTime
{
    /// <summary>"2 h ago", "dün", "3 Oct" … in the UI language.</summary>
    public static string Describe(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        if (age < TimeSpan.FromMinutes(1))
            return Loc.Get("Time.JustNow");
        if (age < TimeSpan.FromHours(1))
            return Loc.Format("Time.Minutes", (int)age.TotalMinutes);
        if (age < TimeSpan.FromHours(24))
            return Loc.Format("Time.Hours", (int)age.TotalHours);
        int days = (DateTime.Today - utc.ToLocalTime().Date).Days;
        if (days <= 1)
            return Loc.Get("Time.Yesterday");
        if (days < 7)
            return Loc.Format("Time.Days", days);
        return utc.ToLocalTime().ToString("d MMM", Loc.Culture);
    }

    /// <summary>How long something has been going on: "4 min", "2 h"; null under a minute.</summary>
    public static string? Duration(TimeSpan length) =>
        length < TimeSpan.FromMinutes(1) ? null
        : length < TimeSpan.FromHours(1) ? Loc.Format("Duration.Minutes", (int)length.TotalMinutes)
        : Loc.Format("Duration.Hours", (int)length.TotalHours);
}
