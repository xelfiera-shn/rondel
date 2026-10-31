using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rondel.Core.Context;
using Rondel.Core.Localization;
using Rondel.Core.Menus;
using Rondel.Core.Settings;
using Rondel.Core.Themes;

namespace Rondel.Windows.UI;

/// <summary>
/// Small pictures of the menu in each theme, for choosing one in Settings: a folder's menu with its first slice
/// highlighted, drawn by a menu window that is never shown. Kept while the settings window is open.
/// </summary>
internal sealed class ThemePreviews : IDisposable
{
    RadialMenuWindow? _window;
    readonly Dictionary<string, BitmapSource> _cache = [];

    public BitmapSource Render(MenuTheme theme, ThemePalette palette)
    {
        string key = $"{theme.Id}|{palette.AccentFrom:X8}|{palette.AccentTo:X8}|{palette.Text:X8}|{Loc.Language}";
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        _window ??= new RadialMenuWindow();
        var settings = new AppSettings();
        settings.Normalize();
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Projects", "Rondel");
        var model = MenuBuilder.Build(new TriggerContext(ContextKind.FolderItem, folder, "Rondel", "preview"), settings);
        var hints = model.Items.Select(_ => ((string?)null, false)).ToList();
        var children = model.Items.Select(_ => (IReadOnlyList<MenuItemModel>)[]).ToList();
        var full = _window.RenderPreview(model, RondelPalette.For(theme, palette), hints, children, [0], Colors.Transparent);

        // The ring and a little of its shadow, from the middle of the stage (drawn at twice the size).
        int center = full.PixelWidth / 2, half = 2 * (168 + 22);
        var ring = new CroppedBitmap(full, new Int32Rect(center - half, center - half, 2 * half, 2 * half));
        ring.Freeze();
        return _cache[key] = ring;
    }

    public void Dispose() => _window?.Close();
}
