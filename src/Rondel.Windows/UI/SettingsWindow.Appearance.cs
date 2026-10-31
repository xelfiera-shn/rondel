using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Rondel.Core.Localization;
using Rondel.Core.Settings;
using Rondel.Core.Themes;

namespace Rondel.Windows.UI;

/// <summary>
/// The Appearance section: a theme chosen from pictures of the menu, dark or light, and the accent colors
/// (the theme's own, a ready-made pair or a custom one). Every change applies to the menu at once.
/// </summary>
public partial class SettingsWindow
{
    const string ThemeTag = "theme", CustomTag = "custom";
    static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    readonly Dictionary<string, Image> _themeImages = [];
    ThemePreviews? _previews;
    string? _previewKey;

    /// <summary>Called from <see cref="LoadValues"/> (also when the language changes, for the names).</summary>
    void LoadAppearance(AppSettings settings)
    {
        var appearance = settings.Appearance;
        BuildThemeCards(appearance);
        Select(ModeBox, appearance.Mode.ToString());
        BuildSwatches(appearance);
        RefreshPreviews(appearance, force: true);
    }

    void BuildThemeCards(AppearanceOptions appearance)
    {
        ThemeCards.Children.Clear();
        _themeImages.Clear();
        string chosen = appearance.ResolveTheme().Id;
        foreach (var theme in BuiltInThemes.All)
        {
            string name = Loc.Resolve(theme.Name);
            var image = new Image { Width = 88, Height = 88, Margin = new Thickness(2, 0, 2, 6) };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var card = new RadioButton
            {
                Style = (Style)FindResource("ThemeCard"),
                GroupName = "Theme",
                IsChecked = theme.Id == chosen,
                Content = new StackPanel
                {
                    Children =
                    {
                        image,
                        new TextBlock { Text = name, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
            };
            AutomationProperties.SetName(card, name);
            card.Checked += (_, _) => OnThemeChosen(theme.Id);
            _themeImages[theme.Id] = image;
            ThemeCards.Children.Add(card);
        }
    }

    void OnThemeChosen(string id)
    {
        if (_loading)
            return;
        Store.Update(s => s.Appearance.Theme = id);
        BuildSwatches(Store.Current.Appearance); // "the theme's own" shows the new theme's colors
    }

    void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !Enum.TryParse(SelectedTag(ModeBox), out ColorMode mode))
            return;
        Store.Update(s => s.Appearance.Mode = mode);
        BuildSwatches(Store.Current.Appearance);
        RefreshPreviews(Store.Current.Appearance);
    }

    void BuildSwatches(AppearanceOptions appearance)
    {
        bool loading = _loading;
        _loading = true;
        try
        {
            AccentSwatches.Children.Clear();
            var own = appearance.ResolveTheme().For(appearance.IsDark(SystemTheme.Read().IsDark));
            string current = CurrentAccent(appearance);
            AddSwatch(ThemeTag, Loc.Get("Accent.Theme"), own.AccentFrom, own.AccentTo, current);
            foreach (var preset in BuiltInThemes.Accents)
                AddSwatch(preset.Id, Loc.Resolve(preset.Name), preset.From, preset.To, current);
            uint? from = AppearanceOptions.ParseColor(appearance.AccentFrom), to = AppearanceOptions.ParseColor(appearance.AccentTo);
            AddSwatch(CustomTag, Loc.Get("Accent.Custom"), from ?? 0xFF6B7280, to ?? from ?? 0xFFD1D5DB, current, glyph: "");
            AccentFromBox.Text = appearance.AccentFrom;
            AccentToBox.Text = appearance.AccentTo;
            ShowAccent(current);
        }
        finally
        {
            _loading = loading;
        }
    }

    void AddSwatch(string tag, string name, uint from, uint to, string current, string? glyph = null)
    {
        var fill = new LinearGradientBrush(ToColor(from), ToColor(to), new Point(0, 0), new Point(1, 1));
        fill.Freeze();
        var swatch = new RadioButton
        {
            Style = (Style)FindResource("Swatch"),
            GroupName = "Accent",
            Background = fill,
            ToolTip = name,
            IsChecked = tag == current,
            Content = glyph is null ? null : new TextBlock { FontFamily = IconFont, FontSize = 11, Text = glyph, Foreground = Brushes.White },
        };
        AutomationProperties.SetName(swatch, name);
        swatch.Checked += (_, _) => OnAccentChosen(tag);
        AccentSwatches.Children.Add(swatch);
    }

    /// <summary>Which swatch the saved colors are: none (the theme's own), a ready-made pair, or custom ones.</summary>
    static string CurrentAccent(AppearanceOptions appearance)
    {
        uint? from = AppearanceOptions.ParseColor(appearance.AccentFrom), to = AppearanceOptions.ParseColor(appearance.AccentTo);
        if (from is null && to is null)
            return ThemeTag;
        (from, to) = (from ?? to, to ?? from);
        return BuiltInThemes.Accents.FirstOrDefault(p => p.From == from && p.To == to)?.Id ?? CustomTag;
    }

    void ShowAccent(string tag)
    {
        AccentName.Text = tag switch
        {
            ThemeTag => Loc.Get("Accent.Theme"),
            CustomTag => Loc.Get("Accent.Custom"),
            _ => Loc.Resolve(BuiltInThemes.Accents.First(p => p.Id == tag).Name),
        };
        CustomAccent.Visibility = tag == CustomTag ? Visibility.Visible : Visibility.Collapsed;
        SetStatus(AccentStatus, null);
    }

    void OnAccentChosen(string tag)
    {
        if (_loading)
            return;
        ShowAccent(tag);
        if (tag == CustomTag)
        {
            // Start from the colors in use, so choosing Custom changes nothing until they are edited.
            var palette = Store.Current.Appearance.ResolvePalette(SystemTheme.Read().IsDark);
            AccentFromBox.Text = AppearanceOptions.FormatColor(palette.AccentFrom);
            AccentToBox.Text = AppearanceOptions.FormatColor(palette.AccentTo);
            CommitCustomAccent();
            AccentFromBox.Focus();
            return;
        }
        var preset = BuiltInThemes.Accents.FirstOrDefault(p => p.Id == tag);
        Store.Update(s =>
        {
            s.Appearance.AccentFrom = preset is null ? "" : AppearanceOptions.FormatColor(preset.From);
            s.Appearance.AccentTo = preset is null ? "" : AppearanceOptions.FormatColor(preset.To);
        });
        RefreshPreviews(Store.Current.Appearance);
    }

    void OnCustomAccentCommitted(object sender, RoutedEventArgs e) => CommitCustomAccent();

    void CommitCustomAccent()
    {
        if (_loading)
            return;
        string from = AccentFromBox.Text.Trim(), to = AccentToBox.Text.Trim();
        bool valid = AppearanceOptions.ParseColor(from) is not null && (to.Length == 0 || AppearanceOptions.ParseColor(to) is not null);
        SetStatus(AccentStatus, valid ? null : Loc.Get("Settings.Accent.Invalid"));
        if (!valid)
            return;
        var appearance = Store.Current.Appearance;
        if (AppearanceOptions.ParseColor(from) == AppearanceOptions.ParseColor(appearance.AccentFrom)
            && AppearanceOptions.ParseColor(to) == AppearanceOptions.ParseColor(appearance.AccentTo))
            return;
        Store.Update(s =>
        {
            s.Appearance.AccentFrom = from;
            s.Appearance.AccentTo = to;
        });
        // The custom swatch shows the new colors; the ring stays on Custom even when they match a ready-made pair.
        if (AccentSwatches.Children.OfType<RadioButton>().LastOrDefault() is { } custom)
        {
            uint first = AppearanceOptions.ParseColor(from)!.Value;
            var fill = new LinearGradientBrush(ToColor(first), ToColor(AppearanceOptions.ParseColor(to) ?? first), new Point(0, 0), new Point(1, 1));
            fill.Freeze();
            custom.Background = fill;
        }
        RefreshPreviews(Store.Current.Appearance);
    }

    /// <summary>
    /// Draws the theme pictures in the current mode and accent, one at a time while the window is idle,
    /// so opening Settings never waits for them.
    /// </summary>
    void RefreshPreviews(AppearanceOptions appearance, bool force = false)
    {
        bool systemDark = SystemTheme.Read().IsDark;
        string key = $"{appearance.IsDark(systemDark)}|{appearance.AccentFrom}|{appearance.AccentTo}|{Loc.Language}";
        if (!force && key == _previewKey)
            return;
        _previewKey = key;
        _previews ??= new ThemePreviews();
        foreach (var theme in BuiltInThemes.All)
        {
            var palette = new AppearanceOptions
            {
                Theme = theme.Id,
                Mode = appearance.Mode,
                AccentFrom = appearance.AccentFrom,
                AccentTo = appearance.AccentTo,
            }.ResolvePalette(systemDark);
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (_previewKey != key || _previews is null || !_themeImages.TryGetValue(theme.Id, out var image))
                    return;
                image.Source = _previews.Render(theme, palette);
            });
        }
    }

    static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}
