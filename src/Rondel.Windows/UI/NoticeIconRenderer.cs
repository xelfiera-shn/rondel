using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rondel.Core.Activity;

namespace Rondel.Windows.UI;

/// <summary>What a notification about an agent is for; each has its own badge.</summary>
internal enum NoticeIcon
{
    Permission,
    Question,
    Done,
    Error,
}

/// <summary>
/// The large icon of a notification about an agent: Rondel's mark, so the notification is known as Rondel's at a
/// glance (Windows names the app only in small print), with a badge in the agent's state color for what happened.
/// The gap around the badge is transparent, so the icon sits on light and dark notifications alike.
/// </summary>
internal static class NoticeIconRenderer
{
    /// <summary>The state colors, also used for the states in the menu: waiting for the user, done, failed.</summary>
    public static readonly Color Waiting = Color.FromRgb(0xF5, 0x9E, 0x0B);

    public static readonly Color Done = Color.FromRgb(0x22, 0xC5, 0x5E);

    public static readonly Color Failed = Color.FromRgb(0xEF, 0x44, 0x44);

    static readonly Typeface IconFont = new(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal,
        FontStretches.Normal);

    /// <summary>The icon for a change worth a notification.</summary>
    public static NoticeIcon For(ActiveAgent agent, AgentNotice notice) => notice switch
    {
        AgentNotice.Asks when agent.Tool is not null => NoticeIcon.Permission,
        AgentNotice.Asks => NoticeIcon.Question,
        AgentNotice.Finished => NoticeIcon.Done,
        _ => NoticeIcon.Error,
    };

    public static BitmapSource Render(NoticeIcon icon, int px)
    {
        var (color, glyph) = icon switch
        {
            NoticeIcon.Permission => (Waiting, "\uE72E"), // lock
            NoticeIcon.Question => (Waiting, "\uE897"),   // question mark
            NoticeIcon.Done => (Done, "\uE73E"),          // check mark
            _ => (Failed, "\uE7BA"),                      // warning
        };
        // The mark a little smaller, in the top left, so the badge covers only the ring's corner and the hub stays.
        double mark = px * 0.84, radius = px * 0.25, gap = px * 0.05;
        var center = new Point(px - radius - px * 0.005, px - radius - px * 0.005);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // The mark, with the badge's place cut out of it.
            dc.PushClip(Geometry.Combine(new RectangleGeometry(new Rect(0, 0, px, px)), new EllipseGeometry(center, radius + gap, radius + gap),
                GeometryCombineMode.Exclude, null));
            dc.DrawImage(AppIconRenderer.Render((int)Math.Round(mark)), new Rect(0, 0, mark, mark));
            dc.Pop();

            var badge = new SolidColorBrush(color);
            badge.Freeze();
            dc.DrawEllipse(badge, null, center, radius, radius);

            // The glyph's own outline, centered on its ink rather than its line box.
            var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, IconFont, radius * 1.1, Brushes.White, 1.0);
            var shape = text.BuildGeometry(new Point(0, 0));
            var bounds = shape.Bounds;
            shape.Transform = new TranslateTransform(center.X - bounds.X - bounds.Width / 2, center.Y - bounds.Y - bounds.Height / 2);
            dc.DrawGeometry(Brushes.White, null, shape);
        }

        var bitmap = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
