using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Rondel.Core.Context;
using Rondel.Core.Localization;
using Rondel.Core.Menus;
using Rondel.Core.Settings;
using Rondel.Core.Themes;

namespace Rondel.MacOS.UI;

/// <summary>
/// Small pictures of the menu in each theme, for choosing one in Settings: a folder's menu with its first slice
/// highlighted, drawn by a menu window that is never shown. Kept while the settings window is open.
/// </summary>
internal sealed class ThemePreviews : IDisposable
{
    RadialMenuWindow? _window;
    readonly Dictionary<string, IImage> _cache = [];

    public IImage Render(MenuTheme theme, ThemePalette palette)
    {
        string key = $"{theme.Id}|{palette.AccentFrom:X8}|{palette.AccentTo:X8}|{palette.Text:X8}|{Loc.Language}";
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var look = MenuPalette.For(theme, palette);
        _window ??= new RadialMenuWindow(look);
        _window.ApplyLook(look);
        var settings = new AppSettings();
        settings.Normalize();
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Projects", "Rondel");
        var model = MenuBuilder.Build(new TriggerContext(ContextKind.FolderItem, folder, "Rondel", "preview"), settings);
        var hints = model.Items.Select(_ => ((string?)null, false)).ToList();
        var children = model.Items.Select(_ => (IReadOnlyList<MenuItemModel>)[]).ToList();
        var full = _window.RenderPreview(model, hints, children, [0], Colors.Transparent);

        // The ring and a little of its shadow, from the middle of the stage (drawn at twice the size). Cropping the 2x
        // render target itself cuts in the wrong place, so it is cut from a plain bitmap of the same pixels.
        int center = full.PixelSize.Width / 2, half = 2 * (168 + 22);
        using var png = new MemoryStream();
        full.Save(png, new PngBitmapEncoderOptions());
        full.Dispose();
        png.Position = 0;
        return _cache[key] = new CroppedBitmap(new Bitmap(png), new PixelRect(center - half, center - half, 2 * half, 2 * half));
    }

    public void Dispose() => _window?.Close();
}
