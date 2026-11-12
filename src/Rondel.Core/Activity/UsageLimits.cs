using System.Text.Json;

namespace Rondel.Core.Activity;

/// <summary>One of the plan's usage limits: how much of it is used (0–100), and when it starts over.</summary>
public sealed record UsageWindow(double Percent, DateTimeOffset ResetsAt)
{
    /// <summary>Two reports of one window name the same reset time; a little apart still counts as the same.</summary>
    static readonly TimeSpan SameWindow = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The newer of two reports. A window that resets later is a newer window; within one window the use only grows, so
    /// the larger share is the newer one. A session that reports late, after a pause, cannot take the figures back.
    /// </summary>
    public static UsageWindow? Newer(UsageWindow? saved, UsageWindow? fresh)
    {
        if (saved is null)
            return fresh;
        if (fresh is null)
            return saved;
        if (fresh.ResetsAt - saved.ResetsAt > SameWindow)
            return fresh;
        if (saved.ResetsAt - fresh.ResetsAt > SameWindow)
            return saved;
        return fresh.Percent >= saved.Percent ? fresh : saved;
    }
}

/// <summary>
/// The plan's usage limits as Claude Code last reported them to a status line: the five-hour window and the weekly
/// one. Claude Code reports them only for claude.ai subscriptions, and only after a session's first reply.
/// </summary>
/// <param name="Reported">When a session last reported them.</param>
public sealed record UsageLimits(UsageWindow? FiveHour, UsageWindow? Weekly, DateTimeOffset Reported)
{
    /// <summary>The limits in a status line's input (its "rate_limits"); null when it has none.</summary>
    public static UsageLimits? FromStatusLine(byte[] json, DateTimeOffset now)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var fiveHour = Window(limits, "five_hour");
            var weekly = Window(limits, "seven_day");
            return fiveHour is null && weekly is null ? null : new UsageLimits(fiveHour, weekly, now);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>What is kept, with a session's new report added (<see cref="UsageWindow.Newer"/>).</summary>
    public static UsageLimits Merge(UsageLimits? saved, UsageLimits fresh) => saved is null
        ? fresh
        : new UsageLimits(UsageWindow.Newer(saved.FiveHour, fresh.FiveHour), UsageWindow.Newer(saved.Weekly, fresh.Weekly),
            fresh.Reported > saved.Reported ? fresh.Reported : saved.Reported);

    static UsageWindow? Window(JsonElement limits, string name)
    {
        if (!limits.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("used_percentage", out var used) || used.ValueKind != JsonValueKind.Number
            || !window.TryGetProperty("resets_at", out var resets) || resets.ValueKind != JsonValueKind.Number
            || !used.TryGetDouble(out double percent) || !resets.TryGetDouble(out double seconds)
            || !double.IsFinite(percent) || !double.IsFinite(seconds) || seconds <= 0 || seconds > 253_402_300_799)
        {
            return null;
        }
        return new UsageWindow(Math.Clamp(percent, 0, 100), DateTimeOffset.FromUnixTimeSeconds((long)seconds));
    }
}
